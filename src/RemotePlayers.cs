using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>What a figure wears: hat, scarf and gloves on or off, and pieces in a colour of their own (by Figure's colour index).</summary>
    internal sealed class FigureLook
    {
        public bool Hat = true, Scarf = true, Mitts = true;
        public readonly bool[] HasTint = new bool[Figure.Colours];
        public readonly Color[] Tint = new Color[Figure.Colours];

        public void SetTint(int colour, Color c) { if (colour >= 0 && colour < Figure.Colours) { HasTint[colour] = true; Tint[colour] = c; } }
        public void ClearTints() { for (int i = 0; i < HasTint.Length; i++) HasTint[i] = false; }
    }

    /// <summary>
    /// The other players as this game shows them. Since 0.10.0 a friend is a human figure (see Figure.cs) that walks,
    /// crouches, lies down and holds a light; the setting "Figur" switched off, or a figure that cannot be built,
    /// brings back the simple capsule of the earlier versions. Since 0.12.0 the figure is a modelled, skinned body
    /// when that can be built (SkinnedBody.cs), and it wears what the other player really wears (LookSync.cs).
    /// </summary>
    internal sealed class RemotePlayers
    {
        private sealed class Avatar
        {
            public byte Id;
            public GameObject Root;
            public Transform Body;      // the old capsule (null when the human figure is used)
            public Figure Fig;          // the human figure (null when the capsule is used)
            public int ColourSet = -1;  // -1 = by player id
            public FigureLook Look;     // the preview's own look (a friend's look comes from LookOf); null = the fixed outfit
            public string Scene = "";
            public Vector3 Target;
            public float TargetYaw;
            public bool Crouch;
            public byte Light;
            public bool Away;
            public byte Sleep; // 0 awake, 1 sleeping, 2 passing time
            public byte Downed; // 0 up, 1 on the ground (waiting to be lifted)
            public ushort DownedLeft; // seconds they still have, as of their last state
            public sbyte Pitch; // where they look, degrees, down is positive
            public readonly LightSync.Carried Carried = new();
            public bool HasState;
            public float LastSeen;
            public int MoveErrors;
            public bool Preview;        // the test figure: not a player, never counted as one
        }

        private readonly Dictionary<byte, Avatar> _avatars = new();

        /// <summary>The setting "Figur": draw friends as people (false = the old capsule).</summary>
        internal static Func<bool> FigureWanted = () => true;
        private static bool _figureBroken;
        private static readonly HashSet<string> _saidFigure = new();
        internal static int FigureErrors;
        internal static string FigureLastError = "-";
        internal static bool FigureInUse => !_figureBroken && SafeWanted();
        private static bool SafeWanted() { try { return FigureWanted(); } catch { return true; } }

        /// <summary>What a friend wears, by player id (null = the fixed outfit). Set by LookSync.</summary>
        internal static Func<byte, FigureLook> LookOf = id => null;
        /// <summary>Harness: which body the preview figure gets (Figure.KindAuto / KindSkinned / KindPrimitives).</summary>
        internal static int PreviewKind = Figure.KindAuto;

        public int Count => _avatars.Count;

        /// <summary>Test helper: where is a remote player (only if we have a state).</summary>
        /// <summary>Is any other player actually playing right now (not in a menu, not idle)?</summary>
        public bool AnyRemoteActive()
        {
            float now = Time.realtimeSinceStartup;
            // someone we have not heard from for a few seconds (loading screen, main menu) is not playing
            foreach (var a in _avatars.Values) if (a.HasState && !a.Away && now - a.LastSeen < 5f) return true;
            return false;
        }

        /// <summary>Is any other player on the ground right now, waiting to be lifted.</summary>
        public bool AnyDowned()
        {
            foreach (var a in _avatars.Values) if (a.HasState && a.Downed != 0) return true;
            return false;
        }

        /// <summary>How many other players are in this scene right now (heard from recently).</summary>
        public int CountInScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return 0;
            float now = Time.realtimeSinceStartup; int n = 0;
            foreach (var a in _avatars.Values) if (a.HasState && a.Scene == scene && now - a.LastSeen < 5f) n++;
            return n;
        }

        /// <summary>Test helper: every other player as this game currently sees them.</summary>
        internal List<(byte Id, bool HasState, string Scene, Vector3 Pos, bool Away, byte Light, float Age, byte Sleep, byte Downed, ushort DownedLeft)> Snapshot()
        {
            var list = new List<(byte, bool, string, Vector3, bool, byte, float, byte, byte, ushort)>();
            foreach (var a in _avatars.Values)
                list.Add((a.Id, a.HasState, a.Scene, a.Target, a.Away, a.Light, Time.realtimeSinceStartup - a.LastSeen, a.Sleep, a.Downed, a.DownedLeft));
            return list;
        }

        /// <summary>Where each friend's body is drawn in this scene, for shots: feet position, facing, crouching, lying.</summary>
        internal List<(byte Id, Vector3 Pos, float Yaw, bool Crouch, bool Lying)> Bodies(string scene)
        {
            var list = new List<(byte, Vector3, float, bool, bool)>();
            foreach (var a in _avatars.Values)
            {
                if (!a.HasState || a.Scene != scene || Time.realtimeSinceStartup - a.LastSeen > 5f) continue;
                Vector3 p = a.Target; float yaw = a.TargetYaw;
                try { if (a.Root != null && a.Root.activeSelf) { p = a.Root.transform.position; yaw = a.Root.transform.eulerAngles.y; } } catch { }
                list.Add((a.Id, p, yaw, a.Crouch, a.Downed != 0 || a.Sleep == 1));
            }
            return list;
        }

        /// <summary>Test helper: how each other player is drawn right now (pose, speed of the figure, where they look).</summary>
        internal List<(byte Id, string Pose, float Speed, float Pitch, bool Figure)> Drawn()
        {
            var list = new List<(byte, string, float, float, bool)>();
            foreach (var a in _avatars.Values) list.Add((a.Id, PoseOf(a), a.Fig != null ? a.Fig.Speed : 0f, a.Pitch, a.Fig != null));
            return list;
        }

        public bool TryGetState(byte id, out string scene, out Vector3 pos)
        {
            scene = null; pos = default;
            if (!_avatars.TryGetValue(id, out var a) || !a.HasState) return false;
            scene = a.Scene; pos = a.Target; return true;
        }

        public void Add(byte id)
        {
            if (_avatars.ContainsKey(id)) return;
            _avatars[id] = new Avatar { Id = id };
        }

        public void Remove(byte id)
        {
            if (!_avatars.TryGetValue(id, out var a)) return;
            Scrap(a);
            _avatars.Remove(id);
        }

        private static void Scrap(Avatar a)
        {
            try { a.Fig?.Destroy(); } catch { }
            a.Fig = null; a.Body = null;
            if (a.Root != null) UnityEngine.Object.Destroy(a.Root);
            a.Root = null;
        }

        public void Clear()
        {
            foreach (var id in new List<byte>(_avatars.Keys)) Remove(id);
        }

        public void ApplyState(byte id, string scene, float x, float y, float z, float yaw, bool crouch, byte light = 0, bool away = false, byte sleep = 0, byte downed = 0, ushort downedLeft = 0, sbyte pitch = 0)
        {
            if (!_avatars.TryGetValue(id, out var a)) { Add(id); a = _avatars[id]; }
            bool teleport = !a.HasState || a.Scene != scene;
            a.Scene = scene;
            a.Target = new Vector3(x, y, z);
            a.TargetYaw = yaw;
            a.Crouch = crouch;
            a.Pitch = pitch;
            if (light != a.Light) CoopMod.Log($"Player {id} light -> {light}");
            a.Light = light;
            if (away != a.Away) CoopMod.Log($"Player {id} is {(away ? "away (menu, background or idle)" : "back")}");
            a.Away = away;
            if (sleep != a.Sleep)
            {
                CoopMod.Log($"Player {id} {(sleep == 1 ? "went to sleep" : sleep == 2 ? "is passing time" : "got up")}");
                a.Sleep = sleep;
                CoopMod.Instance?.Sleep?.RemoteSleepChanged(id, sleep);
            }
            a.DownedLeft = downedLeft;
            if (downed != a.Downed)
            {
                a.Downed = downed;
                CoopMod.Instance?.Downed?.RemoteDownedChanged(id, downed, scene, a.Target);
            }
            a.HasState = true;
            a.LastSeen = Time.realtimeSinceStartup;
            if (teleport && a.Root != null) { a.Root.transform.position = a.Target; a.Fig?.Teleported(); }
        }

        public void Update(string localScene)
        {
            float dt = Time.unscaledDeltaTime;
            foreach (var a in _avatars.Values)
            {
                try { Tick(a, localScene, dt); }
                catch (Exception e) { FigureError("drawing a friend", e); }
            }
            if (_preview != null)
            {
                try { DrivePreview(localScene, dt); Tick(_preview, localScene, dt); }
                catch (Exception e) { FigureError("the preview figure", e); }
            }
        }

        private void Tick(Avatar a, string localScene, float dt)
        {
            bool visible = a.HasState && !string.IsNullOrEmpty(localScene) && a.Scene == localScene
                           && Time.realtimeSinceStartup - a.LastSeen < 10f;
            if (!visible)
            {
                if (a.Root != null && a.Root.activeSelf) a.Root.SetActive(false);
                return;
            }
            if (a.Root == null) Build(a);
            bool jumped = false;
            if (!a.Root.activeSelf) { a.Root.SetActive(true); a.Root.transform.position = a.Target; jumped = true; }

            var t = a.Root.transform;
            var dist = Vector3.Distance(t.position, a.Target);
            if (dist > 8f) jumped = true;
            t.position = dist > 8f ? a.Target : Vector3.Lerp(t.position, a.Target, 1f - Mathf.Exp(-12f * dt));
            var want = Quaternion.Euler(0f, a.TargetYaw, 0f);
            t.rotation = Quaternion.Slerp(t.rotation, want, 1f - Mathf.Exp(-12f * dt));
            if (a.Fig != null) Person(a, t, dt, jumped);
            else Capsule(a, dt);
        }

        // ---------------- the human figure ----------------
        private void Person(Avatar a, Transform t, float dt, bool jumped)
        {
            try
            {
                if (jumped) a.Fig.Teleported(); // put somewhere else: that was not a stride
                var pos = t.position; float yaw = t.eulerAngles.y;
                a.Fig.Animate(dt, pos.x, pos.z, yaw, a.Crouch, a.Downed != 0, a.Sleep, a.Light != LightSync.None, a.Pitch);

                // the carried light sits in the raised right hand (it used to hang at a fixed spot in front of the capsule)
                a.Carried.Set(t, a.Light);
                if (a.Light != LightSync.None && a.Fig.Hand != null)
                {
                    var h = a.Fig.Hand.position;
                    var rot = t.rotation;
                    float yr = yaw * 0.017453292f, fwd = 0f, up = 0f;
                    switch (a.Light)
                    {
                        // the light itself is the tip of the thing held: a torch's flame is well above the fist,
                        // a lantern hangs under it, a flashlight's lens is in front of it
                        case LightSync.Torch: up = 0.40f; break;
                        case LightSync.Flare: up = 0.18f; break;
                        case LightSync.Lamp: up = -0.14f; break;
                        case LightSync.Flashlight: fwd = 0.12f; rot = Quaternion.Euler(a.Fig.LookPitch, yaw, 0f); break; // the beam goes where they look
                    }
                    a.Carried.Place(new Vector3(h.x + MathF.Sin(yr) * fwd, h.y + up, h.z + MathF.Cos(yr) * fwd), rot);
                }
                a.Carried.Tick();
                a.MoveErrors = 0;
            }
            catch (Exception e)
            {
                FigureError("moving the human figure", e);
                // a figure that keeps failing is replaced by the capsule, which is known to work
                if (++a.MoveErrors >= 20 && !a.Preview)
                {
                    CoopMod.Log($"The human figure of player {a.Id} keeps failing: this player is drawn as the old capsule from now on");
                    _figureBroken = true;
                    Scrap(a);
                }
            }
        }

        // ---------------- the old capsule (setting "Figur" off, or the figure could not be built) ----------------
        private static void Capsule(Avatar a, float dt)
        {
            // crouch: squash the body a bit
            var s = a.Body.localScale;
            float wantY = a.Crouch ? 0.55f : 0.85f;
            s.y = Mathf.Lerp(s.y, wantY, 1f - Mathf.Exp(-10f * dt));
            a.Body.localScale = s;
            a.Body.localPosition = new Vector3(0f, s.y, 0f);
            // on the ground: the body lies flat, so a downed friend can be told from a standing one at a glance
            bool lying = a.Downed != 0;
            a.Body.localRotation = Quaternion.Slerp(a.Body.localRotation, lying ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity, 1f - Mathf.Exp(-8f * dt));
            if (lying) a.Body.localPosition = new Vector3(0f, 0.25f, 0f);
            a.Carried.Set(a.Root.transform, a.Light);
            a.Carried.Tick();
        }

        private static string PoseOf(Avatar a)
        {
            float v = a.Fig != null ? a.Fig.Speed : 0f;
            string p;
            if (a.Downed != 0) p = "down";
            else if (a.Sleep == 1) p = "sleep";
            else if (a.Sleep == 2) p = "sit";
            else if (a.Crouch) p = v > 0.3f ? "crouch-walk" : "crouch";
            else p = v > 3.2f ? "run" : v > 0.3f ? "walk" : "stand";
            return a.Light != LightSync.None ? p + "+light" : p;
        }

        private static void FigureError(string where, Exception e)
        {
            FigureErrors++;
            FigureLastError = where + ": " + e.Message;
            if (_saidFigure.Add(where)) CoopMod.Log($"Something went wrong while {where} ({e.Message}); said once, counted in the state dump");
        }

        private static void Build(Avatar a)
        {
            a.Root = new GameObject($"TLDCoop_Player_{a.Id}");
            UnityEngine.Object.DontDestroyOnLoad(a.Root);
            a.Fig = null; a.Body = null; a.MoveErrors = 0;

            // the preview is there to look at the figure, so it is tried even with the setting off
            if (a.Preview || (SafeWanted() && !_figureBroken))
            {
                try
                {
                    a.Fig = Figure.Build(a.Root.transform, a.ColourSet >= 0 ? a.ColourSet : Figure.SetFor(a.Id), a.Preview ? PreviewKind : Figure.KindAuto);
                    // said once per kind of body, so a later change (skinned -> basic shapes, or back) shows in the log too
                    if (_saidFigure.Add("built " + (a.Fig.Skinned ? "skinned" : "primitives")))
                        CoopMod.Log($"Friends are drawn as people: {a.Fig.KindText}, lit by the shader '{Figure.ShaderName}'");
                    Dress(a);
                    return;
                }
                catch (Exception e)
                {
                    a.Fig = null;
                    FigureError("building the human figure", e);
                    if (!a.Preview)
                    {
                        _figureBroken = true;
                        CoopMod.Log("The human figure could not be built: friends are drawn as the old capsule instead");
                    }
                }
            }

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            StripCollider(body);
            body.transform.SetParent(a.Root.transform, false);
            body.transform.localScale = new Vector3(0.5f, 0.85f, 0.4f);
            body.transform.localPosition = new Vector3(0f, 0.85f, 0f);
            Tint(body, new Color(0.22f, 0.25f, 0.28f));
            a.Body = body.transform;

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            StripCollider(head);
            head.transform.SetParent(body.transform, false);
            head.transform.localScale = new Vector3(0.55f, 0.3f, 0.65f);
            head.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            Tint(head, new Color(0.55f, 0.18f, 0.14f)); // red toque, easy to spot in snow

            // small "nose" so you can tell which way they face
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(nose);
            nose.transform.SetParent(head.transform, false);
            nose.transform.localScale = new Vector3(0.25f, 0.3f, 0.4f);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.55f);
            Tint(nose, new Color(0.8f, 0.65f, 0.5f));
        }

        private static void StripCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
        }

        private static void Tint(GameObject go, Color c)
        {
            try
            {
                var r = go.GetComponent<Renderer>();
                if (r != null && r.material != null) r.material.color = c;
            }
            catch { /* shader without _Color: keep default look */ }
        }

        // =====================================================================================
        // what a figure wears
        // =====================================================================================
        /// <summary>Puts the look on a figure: the preview's own, or what LookSync knows of that player; none = the fixed outfit.</summary>
        private static void Dress(Avatar a)
        {
            if (a.Fig == null) return;
            try
            {
                FigureLook look = a.Preview ? a.Look : LookOf(a.Id);
                a.Fig.ClearTints();
                if (look == null) { a.Fig.SetWorn(true, true, true); return; }
                a.Fig.SetWorn(look.Hat, look.Scarf, look.Mitts);
                for (int i = 0; i < Figure.Colours; i++) if (look.HasTint[i]) a.Fig.SetTint(i, look.Tint[i]);
            }
            catch (Exception e) { FigureError("dressing the figure", e); }
        }

        /// <summary>A player's look has changed (or the setting has): dress the figure again if it stands.</summary>
        internal void LookChanged(byte id)
        {
            if (_avatars.TryGetValue(id, out var a)) Dress(a);
        }

        internal void LookChangedAll()
        {
            foreach (var a in _avatars.Values) Dress(a);
        }

        /// <summary>For the state dump: how a friend's figure is dressed right now ("-" = no figure).</summary>
        internal string LookTextOf(byte id) => _avatars.TryGetValue(id, out var a) && a.Fig != null ? a.Fig.LookText : "-";

        // =====================================================================================
        // the preview (test mode): one figure in front of the local player, no connection needed
        // =====================================================================================
        internal const byte PreviewId = 9;
        private Avatar _preview;
        private Vector3 _pvBase, _pvAlong;
        private float _pvYaw, _pvDist, _pvAt, _pvDir = 1f;
        private string _pvPose = "stand";
        private byte _pvLight;

        internal bool PreviewOn => _preview != null;

        /// <summary>Put the preview figure dist metres in front of the local player, facing them. Null = done, else why not.</summary>
        internal string PreviewShow(float dist)
        {
            var me = GameManager_PlayerTransform();
            if (me == null) return "no player to stand in front of";
            float yaw = me.eulerAngles.y;
            try { var cam = Il2Cpp.GameManager.GetVpFPSCamera(); if (cam != null) yaw = cam.transform.eulerAngles.y; } catch { }
            float yr = yaw * 0.017453292f;
            var mine = me.position;
            var at = new Vector3(mine.x + MathF.Sin(yr) * dist, mine.y, mine.z + MathF.Cos(yr) * dist);
            at.y = GroundNear(at, mine.y);
            _pvBase = at; _pvDist = dist;
            _pvYaw = yaw + 180f;                                        // facing the player
            _pvAlong = new Vector3(MathF.Cos(yr), 0f, -MathF.Sin(yr));  // walking goes across the view, so the gait is seen from the side
            _pvAt = 0f; _pvDir = 1f;
            if (_preview == null) _preview = new Avatar { Id = PreviewId, Preview = true };
            _preview.Target = at; _preview.TargetYaw = _pvYaw; _preview.HasState = true;
            if (_preview.Root != null) { _preview.Root.transform.position = at; _preview.Fig?.Teleported(); }
            return null;
        }

        /// <summary>stand | walk | run | sneak | crouch | down | sleep | sit | light [1-4] | look up | look down; "light" may follow any of them.</summary>
        internal string PreviewPose(string[] words)
        {
            if (_preview == null) return "no preview figure (use: figure show)";
            if (words == null || words.Length == 0) return "which pose?";
            string pose = words[0].ToLowerInvariant();
            int next = 1;
            byte light = LightSync.None;
            if (pose == "look")
            {
                string dir = words.Length > 1 ? words[1].ToLowerInvariant() : "";
                if (dir != "up" && dir != "down") return "look up or look down";
                pose = "look " + dir; next = 2;
            }
            else if (pose == "light") { pose = "stand"; next = 0; }
            else if (pose == "passtime") pose = "sit";
            switch (pose)
            {
                case "stand": case "walk": case "run": case "sneak": case "crouch": case "down": case "sleep": case "sit": case "look up": case "look down": break;
                default: return "unknown pose " + pose;
            }
            if (words.Length > next && words[next].ToLowerInvariant() == "light")
            {
                light = LightSync.Torch;
                if (words.Length > next + 1 && byte.TryParse(words[next + 1], out var kind) && kind >= LightSync.Flare && kind <= LightSync.Flashlight) light = kind;
            }
            _pvPose = pose; _pvLight = light;
            _pvAt = 0f; _pvDir = 1f;
            return null;
        }

        /// <summary>Colour set n on the preview figure (without one: on every friend's figure). Returns what was done.</summary>
        internal string PreviewColours(int n)
        {
            n = ((n % Figure.Sets) + Figure.Sets) % Figure.Sets;
            if (_preview != null)
            {
                _preview.ColourSet = n;
                try { _preview.Fig?.SetColours(n); } catch (Exception e) { FigureError("recolouring the figure", e); }
                return $"preview figure wears colour set {n}";
            }
            int done = 0;
            foreach (var a in _avatars.Values)
            {
                a.ColourSet = n;
                try { if (a.Fig != null) { a.Fig.SetColours(n); done++; } } catch (Exception e) { FigureError("recolouring the figure", e); }
            }
            return $"{done} friend figure(s) wear colour set {n}";
        }

        /// <summary>Which body the preview gets from now on; a preview that stands is taken down and built again at the next frame.</summary>
        internal void PreviewKindSet(int kind)
        {
            PreviewKind = kind;
            if (_preview == null) return;
            var at = _preview.Target;
            Scrap(_preview);                 // the avatar stays, its figure is built again by the next Update
            _preview.Target = at;
        }

        /// <summary>hat, scarf or mitts on or off on the preview figure. Null = done, else why not.</summary>
        internal string PreviewWear(string what, bool on)
        {
            if (_preview == null) return "no preview figure (use: figure show)";
            var look = _preview.Look ??= new FigureLook();
            switch ((what ?? "").ToLowerInvariant())
            {
                case "hat": look.Hat = on; break;
                case "scarf": look.Scarf = on; break;
                case "mitts": case "gloves": look.Mitts = on; break;
                default: return "hat, scarf or mitts";
            }
            Dress(_preview);
            return null;
        }

        /// <summary>One piece of the preview figure in a colour of its own (index as in Figure: parka 0, trousers 1, hat 2, mitts 5, boots 6, scarf 7).</summary>
        internal string PreviewTint(int colour, Color c)
        {
            if (_preview == null) return "no preview figure (use: figure show)";
            if (colour < 0 || colour >= Figure.Colours) return "colour index 0 to " + (Figure.Colours - 1);
            (_preview.Look ??= new FigureLook()).SetTint(colour, c);
            Dress(_preview);
            return null;
        }

        internal string PreviewTintClear()
        {
            if (_preview == null) return "no preview figure (use: figure show)";
            _preview.Look?.ClearTints();
            Dress(_preview);
            return null;
        }

        /// <summary>A whole look on the preview figure (null = the fixed outfit again).</summary>
        internal string PreviewLook(FigureLook look)
        {
            if (_preview == null) return "no preview figure (use: figure show)";
            _preview.Look = look;
            Dress(_preview);
            return null;
        }

        internal bool PreviewOff()
        {
            if (_preview == null) return false;
            Scrap(_preview);
            _preview = null;
            return true;
        }

        /// <summary>For the state dump: off, or what the preview shows.</summary>
        internal string PreviewLine()
        {
            var a = _preview;
            if (a == null) return "off";
            string what = a.Fig != null ? $"figure parts={a.Fig.Parts} colors={a.Fig.ColourSet} kind={(a.Fig.Skinned ? "skinned" : "primitives")} {a.Fig.LookText} [{a.Fig.BodyText}]" : a.Root != null ? "capsule (the figure could not be built)" : "not drawn yet";
            return FormattableString.Invariant($"on id={a.Id} asked={_pvPose}{(_pvLight != 0 ? "+light" : "")} pose={PoseOf(a)} speed={(a.Fig != null ? a.Fig.Speed : 0f):0.00} pitch={a.Pitch} dist={_pvDist:0.0} pos={a.Target.x:0.00},{a.Target.y:0.00},{a.Target.z:0.00} {what}");
        }

        private void DrivePreview(string localScene, float dt)
        {
            var a = _preview;
            if (!string.IsNullOrEmpty(localScene)) a.Scene = localScene;
            a.HasState = true; a.LastSeen = Time.realtimeSinceStartup;
            a.Crouch = false; a.Sleep = 0; a.Downed = 0; a.Pitch = 0; a.Light = _pvLight;
            var pos = _pvBase; float yaw = _pvYaw;
            switch (_pvPose)
            {
                case "walk": case "run": case "sneak":
                {
                    // back and forth along a short line across the view
                    float v = _pvPose == "run" ? 4.5f : _pvPose == "sneak" ? 0.9f : 1.5f, half = _pvPose == "run" ? 5f : 3f;
                    _pvAt += _pvDir * v * dt;
                    if (_pvAt > half) { _pvAt = half; _pvDir = -1f; } else if (_pvAt < -half) { _pvAt = -half; _pvDir = 1f; }
                    pos = new Vector3(_pvBase.x + _pvAlong.x * _pvAt, _pvBase.y, _pvBase.z + _pvAlong.z * _pvAt);
                    yaw = MathF.Atan2(_pvAlong.x * _pvDir, _pvAlong.z * _pvDir) * 57.29578f;
                    a.Crouch = _pvPose == "sneak";
                    break;
                }
                case "crouch": a.Crouch = true; break;
                case "down": a.Downed = 1; break;
                case "sleep": a.Sleep = 1; yaw = _pvYaw + 90f; break; // lying across the view, not feet first
                case "sit": a.Sleep = 2; yaw = _pvYaw + 35f; break;
                case "look up": a.Pitch = -40; yaw = _pvYaw + 60f; break;   // turned, so the tilt of the head shows
                case "look down": a.Pitch = 40; yaw = _pvYaw + 60f; break;
            }
            a.Target = pos; a.TargetYaw = yaw;
        }

        private static Transform GameManager_PlayerTransform()
        {
            try { return Il2Cpp.GameManager.GetPlayerTransform(); } catch { return null; }
        }

        /// <summary>The ground under a spot, looked for near a known height (the player's feet); that height itself if nothing is found.</summary>
        private static float GroundNear(Vector3 p, float refY)
        {
            try
            {
                var hits = Physics.RaycastAll(new Vector3(p.x, refY + 2.5f, p.z), Vector3.down, 5f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.NaN, bestDiff = float.MaxValue;
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    float y = hits[i].point.y, diff = Mathf.Abs(y - refY);
                    if (diff < bestDiff) { bestDiff = diff; best = y; }
                }
                if (!float.IsNaN(best)) return best;
            }
            catch { }
            return refY;
        }
    }
}
