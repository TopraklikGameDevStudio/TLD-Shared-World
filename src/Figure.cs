using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The friend as a person: an adult in winter gear, about 1.78 m tall standing. It is put together at run time
    /// on a small skeleton of joints: a modelled, skinned body out of the mod's own DLL (SkinnedBody.cs) when that can
    /// be built, else Unity's own basic shapes (nothing is loaded from disk, so nothing can be missing). Either way
    /// it is moved by what the other player does: walking and running from the speed the figure really
    /// moves at, crouching, lying on its side when downed, on its back when asleep, sitting while passing time, one
    /// arm raised for a carried light, the head following where they look.
    ///
    /// How a pose is made: a handful of weights (crouch, sit, on the back, on the side, light, gait) glide towards
    /// what the player is doing, and the joint angles are worked out from those weights every frame. Nothing is ever
    /// switched from one frame to the next, so nothing pops.
    ///
    /// Axes as Unity has them: x right, y up, z forward. A limb hangs down from its joint; turning the joint about x
    /// by a negative angle swings the limb forward. The shapes hang under the joints and carry their own size, the
    /// joints themselves are never scaled, so a turned joint cannot skew anything.
    /// </summary>
    internal sealed class Figure
    {
        // ---- for the state dump ----
        internal static int LastParts;
        internal static string ShaderName = "-";
        /// <summary>How the last figure was built: "skinned: N verts, M tris, P parts" or "primitives (why)".</summary>
        internal static string Kind = "-";
        /// <summary>Why the skinned body was not used the last time it was tried ("-" = it was used, or never tried).</summary>
        internal static string MeshError = "-";

        // ---- which body: the modelled, skinned one when it can be built, else the 30 basic shapes ----
        internal const int KindAuto = 0, KindSkinned = 1, KindPrimitives = 2;
        internal static string KindName(int kind) => kind == KindSkinned ? "skinned" : kind == KindPrimitives ? "primitives" : "auto";
        // a skinned body that could not be built is not tried again for every figure (unless it is asked for by name)
        private static string _skinnedBroken;

        // ---- body measures (metres) ----
        private const float Thigh = 0.43f, Shin = 0.40f, FootH = 0.09f, HipSocket = 0.02f;
        private const float StandHips = HipSocket + Thigh + Shin + FootH; // 0.94

        // ---- colours: what is painted with what ----
        internal const int Parka = 0, Trousers = 1, Hat = 2, Cuff = 3, Pack = 4, Mitts = 5, Boots = 6, Scarf = 7, Skin = 8, Hair = 9, Bedroll = 10, Colours = 11;
        private static readonly string[] ColourNames = { "Parka", "Trousers", "Hat", "Cuff", "Pack", "Mitts", "Boots", "Scarf", "Skin", "Hair", "Bedroll" };
        internal static string ColourName(int colour) => colour >= 0 && colour < Colours ? ColourNames[colour] : "colour" + colour;
        internal const int Sets = 3;
        // muted, like clothes that have been outdoors for a while; no glow anywhere, so the scene's own light decides
        // how the figure looks (dark at night, warm next to a fire)
        private static readonly float[][] Palette =
        {
            //        parka              trousers           hat                cuff               pack               mitts              boots              scarf              skin
            new[] { 0.13f,0.17f,0.23f, 0.40f,0.37f,0.31f, 0.47f,0.16f,0.13f, 0.36f,0.12f,0.10f, 0.25f,0.26f,0.17f, 0.20f,0.14f,0.10f, 0.11f,0.09f,0.08f, 0.44f,0.43f,0.40f, 0.74f,0.58f,0.47f },
            new[] { 0.16f,0.23f,0.16f, 0.34f,0.34f,0.36f, 0.60f,0.46f,0.17f, 0.46f,0.35f,0.13f, 0.31f,0.22f,0.15f, 0.13f,0.13f,0.14f, 0.16f,0.11f,0.08f, 0.33f,0.20f,0.17f, 0.70f,0.55f,0.45f },
            new[] { 0.31f,0.14f,0.12f, 0.20f,0.22f,0.27f, 0.56f,0.56f,0.52f, 0.42f,0.42f,0.40f, 0.19f,0.19f,0.21f, 0.30f,0.24f,0.16f, 0.10f,0.10f,0.10f, 0.19f,0.25f,0.31f, 0.77f,0.61f,0.50f },
        };
        private const float CuffShade = 0.77f; // the turned-up edge of a hat is that much darker than the hat

        /// <summary>Which of the colour sets a player gets: by id, so two friends never look the same.</summary>
        internal static int SetFor(byte id) => id % Sets;

        /// <summary>
        /// A colour of one of the fixed outfits. Hair is the same dark brown in every set; the bedroll has the set's
        /// scarf colour (it is a colour of its own so that it stays as it is when the scarf is another one, or off).
        /// </summary>
        internal static Color BaseColour(int set, int colour)
        {
            if (colour == Hair) return Rgb(0.16f, 0.11f, 0.08f);
            if (colour == Bedroll) colour = Scarf;
            var p = Palette[((set % Sets) + Sets) % Sets]; int i = colour * 3;
            return Rgb(p[i], p[i + 1], p[i + 2]);
        }

        // (filled in field by field: no call into the game is needed to make a colour)
        internal static Color Rgb(float r, float g, float b) { Color c = default; c.r = r; c.g = g; c.b = b; c.a = 1f; return c; }

        // ---- joints ----
        private Transform _root, _hips, _spine, _head, _pack, _shL, _elL, _handL, _shR, _elR, _hipL, _knL, _anL, _hipR, _knR, _anR;
        /// <summary>The right hand: where a carried light is held.</summary>
        internal Transform Hand { get; private set; }
        /// <summary>How many things are drawn: basic shapes, or parts of the skinned body.</summary>
        internal int Parts { get; private set; }
        internal int ColourSet { get; private set; }
        /// <summary>How this figure was built (see Kind).</summary>
        internal string KindText { get; private set; } = "-";
        internal bool Skinned => _body != null;

        private readonly Material[] _mat = new Material[Colours];
        private readonly bool[] _matMade = new bool[Colours];
        private readonly List<GameObject> _made = new();
        private readonly List<(GameObject Go, int Colour)> _shapes = new();   // the basic shapes, by colour (to take a hat off)
        private SkinnedBody _body;

        // ---- what the figure wears (all on, nothing tinted = one of the fixed outfits) ----
        private bool _hat = true, _scarf = true, _mitts = true;
        private readonly bool[] _tinted = new bool[Colours];
        private readonly Color[] _tint = new Color[Colours];

        // ---- what the figure is doing ----
        private float _wCrouch, _wSit, _wBack, _wSide, _wLight, _pitch;
        private float _speed, _fwd, _phase;
        private float _lx, _lz; private bool _hasLast;
        private float _packShown = -1f;
        private readonly float _seed = UnityEngine.Random.value * 50f;
        private float _clock;

        /// <summary>Metres per second the figure is really moving at (smoothed), from where it was drawn frame to frame.</summary>
        internal float Speed => _speed;
        /// <summary>Where the head looks, degrees, down is positive (smoothed).</summary>
        internal float LookPitch => _pitch;

        // =====================================================================================
        // building
        // =====================================================================================
        /// <summary>
        /// Builds the figure under root (which stands on the ground at the feet). kind: KindAuto = the skinned body if
        /// it can be built, else the basic shapes; KindSkinned = try the skinned body even if it failed before;
        /// KindPrimitives = the basic shapes. Throws if not even a basic shape can be made.
        /// </summary>
        internal static Figure Build(Transform root, int colourSet, int kind = KindAuto)
        {
            var f = new Figure { ColourSet = ((colourSet % Sets) + Sets) % Sets };
            try { f.Make(root, kind); }
            catch (Exception e) { f.Destroy(); Kind = "not built (" + SkinnedBody.Short(e) + ")"; throw; }
            LastParts = f.Parts;
            Kind = f.KindText;
            return f;
        }

        private void Make(Transform root, int kind)
        {
            _root = root;
            MakeJoints(root);
            string why = TrySkinned(root, kind);
            if (why == null) return;
            MakeShapes();
            KindText = "primitives (" + why + ")";
        }

        /// <summary>The skeleton: 16 joints in their rest pose (nothing turned, nothing scaled). The skinned body's file is modelled on exactly these.</summary>
        private void MakeJoints(Transform root)
        {
            _hips = Joint("Hips", root, 0f, StandHips, 0f);
            _spine = Joint("Spine", _hips, 0f, 0.06f, 0f);                      // the upper body turns at the small of the back
            _pack = Joint("Pack", _spine, 0f, 0.25f, -0.145f);                  // the backpack is taken off to sleep: its own joint, so it can shrink away
            _head = Joint("Head", _spine, 0f, 0.50f, 0.01f);
            ArmJoints(-1f, out _shL, out _elL, out _handL);
            ArmJoints(1f, out _shR, out _elR, out var hand);
            Hand = hand;
            LegJoints(-1f, out _hipL, out _knL, out _anL);
            LegJoints(1f, out _hipR, out _knR, out _anR);
        }

        private void ArmJoints(float side, out Transform shoulder, out Transform elbow, out Transform hand)
        {
            shoulder = Joint(side < 0f ? "ShoulderL" : "ShoulderR", _spine, 0.235f * side, 0.435f, 0f);
            elbow = Joint(side < 0f ? "ElbowL" : "ElbowR", shoulder, 0f, -0.29f, 0f);
            hand = Joint(side < 0f ? "HandL" : "HandR", elbow, 0f, -0.30f, 0.03f);
        }

        private void LegJoints(float side, out Transform hip, out Transform knee, out Transform ankle)
        {
            hip = Joint(side < 0f ? "HipL" : "HipR", _hips, 0.095f * side, -HipSocket, 0f);
            knee = Joint(side < 0f ? "KneeL" : "KneeR", hip, 0f, -Thigh, 0f);
            ankle = Joint(side < 0f ? "AnkleL" : "AnkleR", knee, 0f, -Shin, 0f);
        }

        /// <summary>
        /// The modelled body on the joints. Null = it stands; else why not (then nothing of the attempt is left, and
        /// the basic shapes are built).
        /// </summary>
        private string TrySkinned(Transform root, int kind)
        {
            if (kind == KindPrimitives) return "asked for";
            if (kind == KindAuto && _skinnedBroken != null) return "the skinned body failed before: " + _skinnedBroken;
            try
            {
                // in the order of the file's bones (SurvivorFile.BoneNames)
                var bones = new[] { _hips, _spine, _head, _pack, _shL, _elL, _handL, _shR, _elR, Hand, _hipL, _knL, _anL, _hipR, _knR, _anR };
                _body = SkinnedBody.Build(root, bones, MaterialFor);
                Parts = _body.Parts;
                KindText = FormattableString.Invariant($"skinned: {_body.Verts} verts, {_body.Tris} tris, {_body.Parts} parts");
                _skinnedBroken = null; MeshError = "-";
                return null;
            }
            catch (Exception e)
            {
                string why = SkinnedBody.Short(e);
                // nothing of the attempt stays: the body has taken its own objects and meshes away, the materials go here
                try { _body?.Destroy(); } catch { }
                _body = null;
                DestroyMaterials();
                Parts = 0;
                _skinnedBroken = why; MeshError = why;
                CoopMod.Log($"Skinned body not used: {why}. The figure is built from the basic shapes instead");
                return why;
            }
        }

        /// <summary>The 30 basic shapes on the joints: the figure as it was before the skinned body, and still is whenever that cannot be built.</summary>
        private void MakeShapes()
        {
            Shape(PrimitiveType.Sphere, _hips, 0f, -0.01f, 0f, 0.36f, 0.24f, 0.27f, Trousers);              // seat of the trousers

            // ---- upper body ----
            Shape(PrimitiveType.Capsule, _spine, 0f, 0.22f, 0f, 0.41f, 0.33f, 0.29f, Parka);                 // parka, a little bulky
            Shape(PrimitiveType.Cylinder, _spine, 0f, -0.06f, 0f, 0.42f, 0.095f, 0.30f, Parka);              // its hem, over the hips
            Shape(PrimitiveType.Sphere, _spine, 0f, 0.42f, 0f, 0.53f, 0.18f, 0.28f, Parka);                  // shoulders
            Shape(PrimitiveType.Sphere, _spine, 0f, 0.49f, -0.10f, 0.27f, 0.15f, 0.17f, Parka);              // hood, lying on the back of the neck
            Shape(PrimitiveType.Cylinder, _spine, 0f, 0.515f, 0.005f, 0.175f, 0.04f, 0.185f, Scarf);         // scarf
            Shape(PrimitiveType.Cube, _spine, -0.105f, 0.30f, 0.125f, 0.04f, 0.33f, 0.03f, Pack, -6f);       // pack straps
            Shape(PrimitiveType.Cube, _spine, 0.105f, 0.30f, 0.125f, 0.04f, 0.33f, 0.03f, Pack, -6f);

            // ---- backpack ----
            Shape(PrimitiveType.Cube, _pack, 0f, 0f, -0.085f, 0.30f, 0.40f, 0.17f, Pack);
            Shape(PrimitiveType.Cube, _pack, 0f, -0.07f, -0.19f, 0.20f, 0.16f, 0.05f, Pack);                 // outer pocket
            Shape(PrimitiveType.Cylinder, _pack, 0f, 0.235f, -0.075f, 0.125f, 0.165f, 0.125f, Bedroll, 0f, 90f); // bedroll on top

            // ---- head ----
            Shape(PrimitiveType.Sphere, _head, 0f, 0.13f, 0.005f, 0.19f, 0.23f, 0.21f, Skin);
            Shape(PrimitiveType.Sphere, _head, 0f, 0.115f, 0.108f, 0.03f, 0.04f, 0.035f, Skin);               // nose: shows which way they face
            Shape(PrimitiveType.Sphere, _head, 0f, 0.192f, -0.012f, 0.215f, 0.20f, 0.235f, Hat);              // wool toque
            Shape(PrimitiveType.Cylinder, _head, 0f, 0.192f, -0.008f, 0.222f, 0.028f, 0.24f, Cuff);           // its turned-up edge
            Shape(PrimitiveType.Sphere, _head, 0f, 0.302f, -0.02f, 0.065f, 0.065f, 0.065f, Cuff);             // bobble

            // ---- arms ----
            foreach (var (shoulder, elbow) in new[] { (_shL, _elL), (_shR, _elR) })
            {
                Shape(PrimitiveType.Capsule, shoulder, 0f, -0.145f, 0f, 0.125f, 0.165f, 0.125f, Parka);
                Shape(PrimitiveType.Capsule, elbow, 0f, -0.125f, 0f, 0.105f, 0.145f, 0.105f, Parka);
                Shape(PrimitiveType.Sphere, elbow, 0f, -0.30f, 0.005f, 0.095f, 0.14f, 0.11f, Mitts);
            }

            // ---- legs ----
            foreach (var (hip, knee, ankle) in new[] { (_hipL, _knL, _anL), (_hipR, _knR, _anR) })
            {
                Shape(PrimitiveType.Capsule, hip, 0f, -0.215f, 0f, 0.185f, 0.235f, 0.19f, Trousers);
                Shape(PrimitiveType.Capsule, knee, 0f, -0.19f, 0f, 0.15f, 0.21f, 0.155f, Trousers);
                Shape(PrimitiveType.Cylinder, ankle, 0f, 0.03f, 0f, 0.155f, 0.075f, 0.16f, Boots);               // boot shaft
                Shape(PrimitiveType.Capsule, ankle, 0f, -0.045f, 0.045f, 0.125f, 0.145f, 0.09f, Boots, 90f);      // the foot, lying forward
            }
        }

        private Transform Joint(string name, Transform parent, float x, float y, float z)
        {
            var go = new GameObject(name);
            _made.Add(go);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = new Vector3(x, y, z);
            return t;
        }

        private void Shape(PrimitiveType type, Transform parent, float x, float y, float z, float sx, float sy, float sz, int colour, float turnX = 0f, float turnZ = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            _made.Add(go);
            // nothing of the figure may be in the way of the local player, of animals or of a bullet
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                try { col.enabled = false; } catch { } // off at once: taking it away only happens at the end of the frame
                UnityEngine.Object.Destroy(col);
            }
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = new Vector3(x, y, z);
            t.localScale = new Vector3(sx, sy, sz);
            if (turnX != 0f || turnZ != 0f) t.localRotation = Turn(turnX, 0f, turnZ);
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            Paint(r, colour);
            _shapes.Add((go, colour));
            Parts++;
        }

        // =====================================================================================
        // materials and colours
        // =====================================================================================
        /// <summary>
        /// The same way the old capsule got a look that the scene lights: the material Unity gives its basic shapes,
        /// in a copy of our own (one per colour, shared by every shape of that colour), with only its colour changed.
        /// </summary>
        private void Paint(Renderer r, int colour)
        {
            var m = _mat[colour];
            if (m != null) { r.sharedMaterial = m; return; }
            m = r.material;
            _mat[colour] = m; _matMade[colour] = true;
            Dress(m, colour);
        }

        /// <summary>
        /// The same material for a part of the skinned body, which has no basic shape to take it from: a basic shape
        /// is made for a moment, its material copied the proven way, and the shape thrown away again.
        /// </summary>
        private Material MaterialFor(int colour)
        {
            var m = _mat[colour];
            if (m != null) return m;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                try { var col = go.GetComponent<Collider>(); if (col != null) col.enabled = false; } catch { }
                go.transform.SetParent(_root, false);
                var r = go.GetComponent<Renderer>();
                if (r == null) throw new InvalidOperationException("a basic shape came without a renderer");
                m = r.material;
                if (m == null) throw new InvalidOperationException("a basic shape came without a material");
                _mat[colour] = m; _matMade[colour] = true;
                Dress(m, colour);
                return m;
            }
            finally
            {
                // never seen, never in the way: switched off now, gone at the end of the frame (the copy of the material stays, it is ours)
                try { go.SetActive(false); } catch { }
                try { UnityEngine.Object.Destroy(go); } catch { }
            }
        }

        private void Dress(Material m, int colour)
        {
            SetColour(m, colour);
            // cloth and wool, not plastic; a shader without these settings just keeps its own
            try { if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.06f); } catch { }
            try { if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.06f); } catch { }
            try { if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f); } catch { }
            try { var sh = m.shader; if (sh != null) ShaderName = sh.name; } catch { }
        }

        /// <summary>The colour a piece has right now: its own tint if it has one, else what the fixed outfit says.</summary>
        internal Color ColourOf(int colour)
        {
            // bare hands on the basic shapes: the mitts stay where they are and are painted as skin
            if (colour == Mitts && !_mitts && _body == null) return ColourOf(Skin);
            if (_tinted[colour]) return _tint[colour];
            if (colour == Cuff && _tinted[Hat]) { var h = _tint[Hat]; return Rgb(h.r * CuffShade, h.g * CuffShade, h.b * CuffShade); }
            return BaseColour(ColourSet, colour);
        }

        private void SetColour(Material m, int colour)
        {
            var c = ColourOf(colour);
            try { m.color = c; } catch { }
            try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
        }

        private void Repaint(int colour) { if (_mat[colour] != null) SetColour(_mat[colour], colour); }
        private void RepaintAll() { for (int i = 0; i < Colours; i++) Repaint(i); }

        /// <summary>Another of the colour sets, on the figure as it stands (pieces with a tint of their own keep it).</summary>
        internal void SetColours(int set)
        {
            ColourSet = ((set % Sets) + Sets) % Sets;
            RepaintAll();
        }

        /// <summary>
        /// What is worn on the head, round the neck and on the hands. The skinned body has hair, a neck and bare hands
        /// underneath, so those parts are simply hidden; on the basic shapes the toque (with its edge and bobble) and
        /// the scarf are hidden and the mitts are painted as skin.
        /// </summary>
        internal void SetWorn(bool hat, bool scarf, bool mitts)
        {
            _hat = hat; _scarf = scarf; _mitts = mitts;
            if (_body != null)
            {
                _body.Show(Hat, hat); _body.Show(Cuff, hat);
                _body.Show(Scarf, scarf);
                _body.Show(Mitts, mitts);
            }
            else
            {
                foreach (var (go, colour) in _shapes)
                {
                    bool show = colour == Hat || colour == Cuff ? hat : colour != Scarf || scarf;
                    if (go != null && go.activeSelf != show) go.SetActive(show);
                }
            }
            Repaint(Mitts);
        }

        /// <summary>
        /// One piece in a colour of its own: parka 0, trousers 1, hat 2 (its edge, 3, follows as a darker shade unless
        /// it has a tint itself), mitts 5, boots 6, scarf 7; any other index works the same way.
        /// </summary>
        internal void SetTint(int colour, Color c)
        {
            if (colour < 0 || colour >= Colours) return;
            c.a = 1f;
            _tinted[colour] = true; _tint[colour] = c;
            Repaint(colour);
            if (colour == Hat) Repaint(Cuff);
        }

        /// <summary>Back to the colours of the fixed outfit.</summary>
        internal void ClearTints()
        {
            bool any = false;
            for (int i = 0; i < Colours; i++) { any |= _tinted[i]; _tinted[i] = false; }
            if (any) RepaintAll();
        }

        /// <summary>For the state dump: what is worn and which pieces have a colour of their own.</summary>
        internal string LookText
        {
            get
            {
                string worn = (_hat ? "hat" : "") + (_scarf ? (_hat ? "+" : "") + "scarf" : "") + (_mitts ? (_hat || _scarf ? "+" : "") + "mitts" : "");
                string tints = "";
                for (int i = 0; i < Colours; i++) if (_tinted[i]) tints += (tints.Length > 0 ? "," : "") + i + ":" + Hex(_tint[i]);
                return $"worn={(worn.Length > 0 ? worn : "none")} tints={(tints.Length > 0 ? tints : "-")}";
            }
        }

        internal static string Hex(Color c)
        {
            static int B(float v) => v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f);
            return $"#{B(c.r):x2}{B(c.g):x2}{B(c.b):x2}";
        }

        private void DestroyMaterials()
        {
            for (int i = 0; i < Colours; i++)
            {
                try { if (_mat[i] != null) UnityEngine.Object.Destroy(_mat[i]); } catch { }
                _mat[i] = null; _matMade[i] = false;
            }
        }

        /// <summary>
        /// For the state dump: are the things this figure made still there (materials made / still alive; for the
        /// skinned body its parts, whether a camera has them in view, and the size of the box they are drawn in).
        /// </summary>
        internal string BodyText
        {
            get
            {
                int made = 0, alive = 0;
                for (int i = 0; i < Colours; i++)
                {
                    if (!_matMade[i]) continue;
                    made++;
                    try { if (_mat[i] != null) alive++; } catch { }
                }
                string body = "";
                try { if (_body != null) body = _body.Report() + ", "; } catch { }
                return $"{body}{alive}/{made} materials alive";
            }
        }

        /// <summary>The copies of the material and the meshes of the skinned body are ours to clean up; the shapes and joints go with the root they hang under.</summary>
        internal void Destroy()
        {
            try { _body?.Destroy(); } catch { }
            _body = null;
            DestroyMaterials();
            foreach (var go in _made) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
            _made.Clear();
            _shapes.Clear();
        }

        // =====================================================================================
        // posing
        // =====================================================================================
        // One pose = these numbers (angles in degrees). "Pitch" of a limb is forward, "roll" is out to its own side.
        private const int HY = 0,      // height of the hips above the ground when the pose fixes it
                          HF = 1,      // 1 = the pose fixes the hips' height, 0 = the hips sit wherever the legs put them
                          HZ = 2,      // hips forward (+) or back (-) of the feet
                          BP = 3, BR = 4,          // whole body tipped over: backwards (-), onto its left side (+)
                          SP = 5,                  // upper body leaning forward
                          HP = 6, HR = 7,          // head: nod down (+), tilt to the left shoulder (+)
                          ALP = 8, ALR = 9, ALE = 10, ARP = 11, ARR = 12, ARE = 13,   // arms: pitch, roll, elbow bend
                          LLP = 14, LLR = 15, LLK = 16, LRP = 17, LRR = 18, LRK = 19, // legs: pitch, roll, knee bend
                          FLAT = 20,   // 1 = the feet are kept flat on the ground
                          N = 21;

        private static readonly float[] StandP  = { StandHips, 0f,  0f,     0f,  0f,  2f,   0f,  0f,   3f,  7f, 12f,   3f,   7f, 12f,    0f, 2.5f,   2f,    0f, 2.5f,   2f, 1f };
        // (an arm's pitch is measured from the body it hangs on: with the back leaning forward by 38 it takes 38 to hang straight down)
        private static readonly float[] CrouchP = { StandHips, 0f, -0.14f,  0f,  0f, 38f, -24f,  0f,  46f,  8f, 50f,  46f,   8f, 50f,   74f,   5f, 122f,   74f,   5f, 122f, 1f };
        // on the ground with the knees up and the forearms on the knees (these leg angles put the seat on the ground by themselves)
        private static readonly float[] SitP    = { 0.13f,     0f, -0.28f,  0f,  0f, 20f,  -8f,  0f,  55f, -5f, 55f,  55f,  -5f, 55f,  112f,   4f,  49f,  112f,   4f,  49f, 1f };
        // asleep: flat on the back, arms along the body
        private static readonly float[] BackP   = { 0.16f,     1f,  0f,   -90f,  0f,  0f,  -4f,  0f, -10f,  9f,  4f, -10f,   9f,  4f,   -2f,   5f,   2f,   -2f,   5f,   2f, 0f };
        // downed: on the left side, rolled a little towards the back, knees drawn up, the upper arm fallen across the chest
        private static readonly float[] SideP   = { 0.235f,    1f,  0f,   -10f, 90f,  6f,   8f, 42f,  62f,  2f, 48f,  40f, -62f, 42f,   24f,  10f,  38f,   52f, -24f,  72f, 0f };

        private readonly float[] _p = new float[N];

        /// <summary>Forget where the figure was: it has been put somewhere else, that was not a stride.</summary>
        internal void Teleported() { _hasLast = false; }

        /// <summary>
        /// One frame. x, z: where the figure's root is drawn now; yaw: the way it faces; the rest is what the other
        /// player is doing. dt is real time, so the figure keeps moving while the game clock is paused or fast.
        /// </summary>
        internal void Animate(float dt, float x, float z, float yaw, bool crouch, bool downed, byte sleep, bool light, float lookPitch)
        {
            if (dt < 0.0005f) dt = 0.0005f;
            if (dt > 0.1f) dt = 0.1f;
            _clock += dt;

            // ---- how fast is it really going (the legs follow what the eye sees, not what was sent) ----
            if (!_hasLast) { _lx = x; _lz = z; _hasLast = true; }
            float dx = x - _lx, dz = z - _lz; _lx = x; _lz = z;
            float inst = MathF.Sqrt(dx * dx + dz * dz) / dt;
            float yr = yaw * Deg;
            float instFwd = (dx * MathF.Sin(yr) + dz * MathF.Cos(yr)) / dt;
            if (inst > 12f) { inst = 0f; instFwd = 0f; } // a jump of the position, not a stride
            float k = 1f - MathF.Exp(-dt / 0.18f);
            _speed += (inst - _speed) * k;
            _fwd += (instFwd - _fwd) * k;

            // ---- glide towards what the player is doing ----
            bool lying = downed || sleep == 1, sitting = !downed && sleep == 2;
            Glide(ref _wSide, downed ? 1f : 0f, 3.5f, dt);
            Glide(ref _wBack, !downed && sleep == 1 ? 1f : 0f, 3f, dt);
            Glide(ref _wSit, sitting ? 1f : 0f, 3.5f, dt);
            Glide(ref _wCrouch, crouch && !lying && !sitting ? 1f : 0f, 7f, dt);
            Glide(ref _wLight, light ? 1f : 0f, 6f, dt);
            _pitch += (Clamp(lookPitch, -80f, 80f) - _pitch) * (1f - MathF.Exp(-10f * dt));

            float wC = Ease(_wCrouch), wSit = Ease(_wSit), wBack = Ease(_wBack), wSide = Ease(_wSide), wL = Ease(_wLight);
            float up = (1f - wSit) * (1f - wBack) * (1f - wSide);   // how much it is on its feet
            float lie = MathF.Max(wBack, wSide);

            var p = _p;
            for (int i = 0; i < N; i++)
            {
                float v = StandP[i];
                v += (CrouchP[i] - v) * wC;
                v += (SitP[i] - v) * wSit;
                v += (BackP[i] - v) * wBack;
                v += (SideP[i] - v) * wSide;
                p[i] = v;
            }

            // ---- walking and running ----
            float gait = Ease(Clamp((_speed - 0.15f) / 0.45f, 0f, 1f)) * up;
            float run = Ease(Clamp((_speed - 2.6f) / 1.6f, 0f, 1f));
            float slow = Clamp(_speed / 1.6f, 0f, 1f);
            float swing = Lerp(Lerp(14f, 24f, slow), 34f, run);                 // how far a thigh swings
            float step = 2f * (Thigh + Shin) * MathF.Sin(swing * Deg) * Lerp(1.15f, 1.45f, run); // metres per step (a running step has air in it)
            // the stride follows the distance covered: faster means longer steps and a quicker beat, and walking
            // backwards runs the same steps the other way round
            float dir = _fwd < -0.25f && -_fwd > 0.5f * _speed ? -1f : 1f;
            _phase += dir * _speed / (2f * step) * 6.2831853f * dt;
            if (_phase > 6.2831853f) _phase -= 6.2831853f; else if (_phase < 0f) _phase += 6.2831853f;
            float s = MathF.Sin(_phase), c = MathF.Cos(_phase);
            float low = Lerp(1f, 0.6f, wC);                                      // crouched: shorter, flatter steps
            float A = swing * low, K = Lerp(46f, 82f, run) * Lerp(1f, 0.55f, wC), B = Lerp(Lerp(10f, 22f, slow), 36f, run) * low;
            float swL = Ease(MathF.Max(0f, c)), swR = Ease(MathF.Max(0f, -c));   // which leg is in the air, coming forward
            p[LLP] += gait * (A * s + 7f * run); p[LRP] += gait * (-A * s + 7f * run);
            p[LLK] += gait * (K * swL + 5f); p[LRK] += gait * (K * swR + 5f);
            p[ALP] += gait * -B * s; p[ARP] += gait * B * s;
            p[ALE] += gait * (Lerp(6f, 62f, run) + 8f * MathF.Max(0f, -s)); p[ARE] += gait * (Lerp(6f, 62f, run) + 8f * MathF.Max(0f, s));
            p[SP] += gait * Lerp(3f, 13f, run);
            float twist = -gait * Lerp(4f, 9f, run) * s;                         // shoulders turn against the hips

            // ---- standing still: breathing, shifting the weight, the head never quite still ----
            float still = (1f - gait) * up;
            float breath = MathF.Sin(_clock * 1.7f + _seed);
            float sway = MathF.Sin(_clock * 0.69f + _seed * 2f);
            p[SP] += 0.8f * breath * (still + wSit * (1f - lie)) + 0.7f * breath * wBack * (1f - wSide); // asleep: only the chest
            p[ALR] += 0.5f * breath * still; p[ARR] += 0.5f * breath * still;
            float lean = -1.2f * sway * still;
            p[BR] += lean; p[LLR] += lean; p[LRR] -= lean;                       // the hips tip, the legs stay on their feet
            float hipsX = 0.012f * sway * still;
            float headYaw = 5f * MathF.Sin(_clock * 0.31f + _seed) * MathF.Sin(_clock * 0.17f + _seed * 3f) * still;

            // ---- looking up and down: mostly the head, a little of the back ----
            p[HP] += _pitch * 0.6f * (1f - lie);
            p[SP] += _pitch * 0.2f * up;

            // ---- a light in the right hand: forearm raised, held out in front, and that arm does not swing ----
            float hold = wL * (1f - lie);
            p[ARP] = Lerp(p[ARP], 42f + 0.9f * p[SP], hold); // (measured from the back: a leaning back takes the arm with it)
            p[ARR] = Lerp(p[ARR], 4f, hold);
            p[ARE] = Lerp(p[ARE], 72f, hold);

            // ---- the hips sit where the longer-reaching leg puts them: that is the bob of a walk, and the drop of a crouch ----
            float dropL = Thigh * MathF.Cos(p[LLP] * Deg) + Shin * MathF.Cos((p[LLP] - p[LLK]) * Deg);
            float dropR = Thigh * MathF.Cos(p[LRP] * Deg) + Shin * MathF.Cos((p[LRP] - p[LRK]) * Deg);
            float onLegs = HipSocket + FootH + MathF.Max(dropL, dropR);
            // running has both feet off the ground between steps: the body does not drop all the way with the legs
            onLegs += MathF.Max(0f, StandHips - 0.004f - onLegs) * 0.55f * run * gait * (1f - wC);
            float hipsY = Lerp(onLegs, p[HY], Clamp(p[HF], 0f, 1f));
            float flat = Clamp(p[FLAT], 0f, 1f);

            _hips.localPosition = new Vector3(hipsX, hipsY, p[HZ]);
            _hips.localRotation = Turn(p[BP], 0f, p[BR]);
            _spine.localRotation = Turn(p[SP], twist, 0f);
            _head.localRotation = Turn(p[HP], headYaw, p[HR]);
            _shL.localRotation = Turn(-p[ALP], 0f, -p[ALR]); _elL.localRotation = Turn(-p[ALE], 0f, 0f);
            _shR.localRotation = Turn(-p[ARP], 0f, p[ARR]); _elR.localRotation = Turn(-p[ARE], 0f, 0f);
            _hipL.localRotation = Turn(-p[LLP], 0f, -p[LLR]); _knL.localRotation = Turn(p[LLK], 0f, 0f);
            _hipR.localRotation = Turn(-p[LRP], 0f, p[LRR]); _knR.localRotation = Turn(p[LRK], 0f, 0f);
            // a planted foot stays level whatever the leg above it does; the foot in the air lets its toes hang while
            // it is still behind and lifts them as it comes forward to land on the heel
            _anL.localRotation = Turn(flat * (p[LLP] - p[LLK]) - gait * 16f * swL * s, 0f, 0f);
            _anR.localRotation = Turn(flat * (p[LRP] - p[LRK]) + gait * 16f * swR * s, 0f, 0f);

            // nobody sleeps on their backpack
            float pack = 1f - wBack;
            if (MathF.Abs(pack - _packShown) > 0.004f)
            {
                _packShown = pack;
                bool show = pack > 0.03f;
                if (_body != null)
                {
                    // the pack is part of the skinned mesh and its joint is one of the mesh's bones: the joint stays
                    // switched on and only shrinks, to next to nothing (never to nothing: that is no size for a bone)
                    float size = show ? pack : 0.001f;
                    _pack.localScale = new Vector3(size, size, size);
                }
                else
                {
                    if (_pack.gameObject.activeSelf != show) _pack.gameObject.SetActive(show);
                    if (show) _pack.localScale = new Vector3(pack, pack, pack);
                }
            }
        }

        // ---- small maths, kept on this side so a frame costs as few calls into the game as possible ----
        private const float Deg = 0.017453292f;
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        private static float Ease(float t) => t * t * (3f - 2f * t);
        private static void Glide(ref float w, float to, float rate, float dt)
        {
            w += (to - w) * (1f - MathF.Exp(-rate * dt));
            if (w < 0.0005f) w = 0f; else if (w > 0.9995f) w = 1f;
        }

        /// <summary>The turn Unity calls Euler(x, y, z) (about z first, then x, then y), in degrees.</summary>
        internal static Quaternion Turn(float x, float y, float z)
        {
            float hx = x * Deg * 0.5f, hy = y * Deg * 0.5f, hz = z * Deg * 0.5f;
            float sx = MathF.Sin(hx), cx = MathF.Cos(hx), sy = MathF.Sin(hy), cy = MathF.Cos(hy), sz = MathF.Sin(hz), cz = MathF.Cos(hz);
            Quaternion q = default;
            q.x = sx * cy * cz + cx * sy * sz;
            q.y = cx * sy * cz - sx * cy * sz;
            q.z = cx * cy * sz - sx * sy * cz;
            q.w = cx * cy * cz + sx * sy * sz;
            return q;
        }
    }
}
