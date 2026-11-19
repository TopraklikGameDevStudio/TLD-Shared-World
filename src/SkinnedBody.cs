using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The friend's body as one modelled, skinned mesh per colour (hat, parka, trousers ...) that bends with the
    /// figure's joints, instead of 30 stiff basic shapes. The model comes from survivor.bin inside the mod's DLL
    /// (see SurvivorFile.cs for the format); Figure makes the joints, this class hangs the meshes on them.
    ///
    /// Nothing here is certain to work in the game before it has been tried there: Unity methods the game itself
    /// never calls can be missing from it. So every step that can be done in more than one way tries the next way
    /// when one throws or does not read back what was set, the log says once which way worked and every time what
    /// failed, and when no way is left Build throws after taking away what it had made: Figure then builds the
    /// basic shapes as before.
    /// </summary>
    internal sealed class SkinnedBody
    {
        internal const string ResourceName = "TLDCoop.survivor.bin";

        /// <summary>The body cannot be built; the message says why.</summary>
        internal sealed class Refused : Exception { public Refused(string why) : base(why) { } }

        // =====================================================================================
        // the file: read once, kept
        // =====================================================================================
        private static SurvivorFile _file;
        private static string _fileError;
        private static bool _fileTried;
        /// <summary>For the state dump: what the file holds, or why it cannot be used.</summary>
        internal static string FileText = "not read yet";

        private static SurvivorFile Model()
        {
            if (!_fileTried)
            {
                _fileTried = true;
                try
                {
                    byte[] data;
                    var asm = typeof(SkinnedBody).Assembly;
                    using (var s = asm.GetManifestResourceStream(ResourceName))
                    {
                        if (s == null)
                        {
                            string has = "";
                            try { has = string.Join(", ", asm.GetManifestResourceNames()); } catch { }
                            throw new FormatException($"the mod's DLL holds no '{ResourceName}' (it holds: {(has.Length > 0 ? has : "nothing")})");
                        }
                        data = new byte[s.Length];
                        int got = 0;
                        while (got < data.Length)
                        {
                            int n = s.Read(data, got, data.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got != data.Length) throw new FormatException($"only {got} of {data.Length} bytes could be read out of the DLL");
                    }
                    _file = SurvivorFile.Parse(data);
                    FileText = "survivor.bin: " + _file.Summary;
                    CoopMod.Log("Skinned body: " + FileText + string.Format(CultureInfo.InvariantCulture,
                        "; box x {0:0.000}..{1:0.000} y {2:0.000}..{3:0.000} z {4:0.000}..{5:0.000}", _file.Min[0], _file.Max[0], _file.Min[1], _file.Max[1], _file.Min[2], _file.Max[2]));
                    foreach (var p in _file.Parts)
                        CoopMod.Log($"Skinned body:   part {p.Index}: colour {p.Colour} {Figure.ColourName(p.Colour)}, {p.Verts} verts, {p.Tris} tris, {p.TwoBone} verts on two bones");
                    foreach (var note in _file.Notes) CoopMod.Log("Skinned body: note: " + note);
                    if (_file.Notes.Count > 0) FileText += $" ({_file.Notes.Count} note(s) in the log)";
                }
                catch (Exception e)
                {
                    _fileError = e is FormatException ? e.Message : Short(e);
                    FileText = "survivor.bin cannot be used: " + _fileError;
                    CoopMod.Log("Skinned body: " + FileText);
                }
            }
            if (_file == null) throw new Refused("survivor.bin: " + _fileError);
            return _file;
        }

        // =====================================================================================
        // which way of doing a step works in this game
        // =====================================================================================
        private static readonly string[] StepOrder = { "joint positions", "bind pose matrices", "vertices", "triangles", "normals", "bone weights", "bind poses", "mesh bounds", "renderer", "material" };
        private static readonly Dictionary<string, int> _way = new();
        private static readonly Dictionary<string, string> _wayName = new();
        private static readonly HashSet<string> _said = new();
        private static readonly List<string> _optionalFailed = new();
        internal static int Builds, Failures;

        /// <summary>For the state dump: how each step was done the last time it worked.</summary>
        internal static string ApiText
        {
            get
            {
                if (_wayName.Count == 0) return "-";
                var parts = new List<string>();
                foreach (var step in StepOrder) if (_wayName.TryGetValue(step, out var name)) parts.Add(step + "=" + name);
                if (_optionalFailed.Count > 0) parts.Add("not set: " + string.Join(", ", _optionalFailed));
                return string.Join("; ", parts);
            }
        }

        /// <summary>One line of an exception: its kind and the first line of what it says (the game's own traces run over many lines).</summary>
        internal static string Short(Exception e)
        {
            if (e == null) return "?";
            if (e is Refused) return e.Message;
            string m = e.Message ?? "";
            int nl = m.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) m = m.Substring(0, nl);
            if (m.Length > 260) m = m.Substring(0, 260) + "...";
            return e.GetType().Name + ": " + m.Trim();
        }

        /// <summary>
        /// Does one step in the first way that works, starting with the way that worked last time. A way fails by
        /// throwing. Says once which way worked (and again when that changes), and once per way and reason what failed.
        /// </summary>
        private static void Step(string step, params (string Name, Action Do)[] ways)
        {
            int first = _way.TryGetValue(step, out var known) ? known : 0;
            string errors = null;
            for (int n = 0; n < ways.Length; n++)
            {
                int i = (first + n) % ways.Length;
                try
                {
                    ways[i].Do();
                    if (!_way.TryGetValue(step, out var was) || was != i || !_wayName.ContainsKey(step))
                    {
                        _way[step] = i; _wayName[step] = ways[i].Name;
                        CoopMod.Log($"Skinned body: {step}: done with {ways[i].Name}" + (errors != null ? $" (after these failed: {errors})" : ""));
                    }
                    return;
                }
                catch (Exception e)
                {
                    string msg = Short(e);
                    errors = (errors == null ? "" : errors + "; ") + ways[i].Name + " -> " + msg;
                    if (_said.Add(step + "|" + ways[i].Name + "|" + msg)) CoopMod.Log($"Skinned body: {step}: {ways[i].Name} failed: {msg}");
                }
            }
            throw new Refused($"{step}: no way of doing it worked ({errors})");
        }

        /// <summary>Something that only has one way and must work.</summary>
        private static void Must(string what, Action act)
        {
            try { act(); }
            catch (Exception e)
            {
                string msg = Short(e);
                if (_said.Add("must|" + what + "|" + msg)) CoopMod.Log($"Skinned body: {what} failed: {msg}");
                throw new Refused($"{what}: {msg}");
            }
        }

        /// <summary>Something the body works without (it may be culled too early or skin with more bones than needed): tried, said once, not fatal.</summary>
        private static void Optional(string what, Action act)
        {
            try { act(); }
            catch (Exception e)
            {
                if (!_optionalFailed.Contains(what)) _optionalFailed.Add(what);
                if (_said.Add("optional|" + what)) CoopMod.Log($"Skinned body: {what} could not be set ({Short(e)}); the body is used without it");
            }
        }

        /// <summary>Reads a number back out of the game to check a step; -1 when even that cannot be read (said once, then the step counts as done).</summary>
        private static int ReadBack(string what, Func<int> read)
        {
            try { return read(); }
            catch (Exception e)
            {
                if (_said.Add("readback|" + what)) CoopMod.Log($"Skinned body: {what} cannot be read back ({Short(e)}): not checked");
                return -1;
            }
        }

        private static void Expect(string what, int got, int want)
        {
            if (got >= 0 && got != want) throw new InvalidOperationException($"the game reads back {got} {what}, {want} were set");
        }

        // =====================================================================================
        // one body
        // =====================================================================================
        private sealed class Piece
        {
            public int Colour;
            public GameObject Go;
            public Mesh Mesh;
            public SkinnedMeshRenderer Renderer;
            public bool Shown = true;
        }

        private readonly List<Piece> _pieces = new();
        internal int Parts => _pieces.Count;
        internal int Verts { get; private set; }
        internal int Tris { get; private set; }

        /// <summary>
        /// Builds the body under root on the 16 joints (in the order of SurvivorFile.BoneNames), which must still be
        /// in their rest pose. materialFor gives the figure's material for a colour index. Throws (Refused, or
        /// whatever the game threw) after destroying everything it had made.
        /// </summary>
        internal static SkinnedBody Build(Transform root, Transform[] bones, Func<int, Material> materialFor)
        {
            var body = new SkinnedBody();
            try
            {
                body.Make(root, bones, materialFor);
                Builds++;
                return body;
            }
            catch
            {
                Failures++;
                try { body.Destroy(); } catch { }
                throw;
            }
        }

        private void Make(Transform root, Transform[] bones, Func<int, Material> materialFor)
        {
            var file = Model();
            const int nb = SurvivorFile.BoneCount;
            if (bones == null || bones.Length != nb) throw new Refused($"{(bones == null ? 0 : bones.Length)} joints handed over, {nb} needed");

            // ---- the joints: the right ones, and where the file thinks they are ----
            for (int i = 0; i < nb; i++)
            {
                int k = i;
                if (bones[k] == null) throw new Refused($"joint {SurvivorFile.BoneNames[k]} is missing");
                string name = null;
                Must($"reading the name of joint {k}", () => name = bones[k].name);
                if (name != SurvivorFile.BoneNames[k]) throw new Refused($"joint {k} is called '{name}', the file's bone {k} is '{SurvivorFile.BoneNames[k]}'");
            }
            var joints = new float[nb * 3];
            Step("joint positions",
                ("root.InverseTransformPoint(joint.position)", () =>
                {
                    for (int i = 0; i < nb; i++)
                    {
                        var p = root.InverseTransformPoint(bones[i].position);
                        joints[i * 3] = p.x; joints[i * 3 + 1] = p.y; joints[i * 3 + 2] = p.z;
                    }
                }),
                ("the sum of localPosition up to the root", () =>
                {
                    for (int i = 0; i < nb; i++)
                    {
                        float x = 0f, y = 0f, z = 0f;
                        var t = bones[i];
                        for (int depth = 0; depth < 8 && t != null && t.Pointer != root.Pointer; depth++)
                        {
                            var lp = t.localPosition; x += lp.x; y += lp.y; z += lp.z;
                            t = t.parent;
                        }
                        if (t == null || t.Pointer != root.Pointer) throw new InvalidOperationException($"joint {SurvivorFile.BoneNames[i]} does not hang under the figure's root");
                        joints[i * 3] = x; joints[i * 3 + 1] = y; joints[i * 3 + 2] = z;
                    }
                }));
            string off = file.RestMismatch(joints);
            if (off != null) throw new Refused(off);

            // ---- bind poses: where each bone is while the mesh is in the shape it was modelled in. Taken now,
            //      from the joints as they have just been made, before any pose turns them ----
            var poses = new Matrix4x4[nb];
            Step("bind pose matrices",
                ("bone.worldToLocalMatrix * root.localToWorldMatrix", () =>
                {
                    var rootToWorld = root.localToWorldMatrix;
                    for (int i = 0; i < nb; i++)
                    {
                        var m = Mul(bones[i].worldToLocalMatrix, rootToWorld);
                        // a bind pose puts its own bone at the origin: a matrix that does not is not one
                        float x = file.Rest[i * 3], y = file.Rest[i * 3 + 1], z = file.Rest[i * 3 + 2];
                        float ox = m.m00 * x + m.m01 * y + m.m02 * z + m.m03, oy = m.m10 * x + m.m11 * y + m.m12 * z + m.m13, oz = m.m20 * x + m.m21 * y + m.m22 * z + m.m23;
                        float d = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                        if (!(d <= 0.004f)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "the matrix of {0} puts its bone {1:0.0} mm from where it must be", SurvivorFile.BoneNames[i], d * 1000f));
                        poses[i] = m;
                    }
                }),
                ("a shift by each joint's rest position (rest pose: nothing turned, nothing scaled)", () =>
                {
                    for (int i = 0; i < nb; i++)
                    {
                        Matrix4x4 m = default;
                        m.m00 = 1f; m.m11 = 1f; m.m22 = 1f; m.m33 = 1f;
                        m.m03 = -joints[i * 3]; m.m13 = -joints[i * 3 + 1]; m.m23 = -joints[i * 3 + 2];
                        poses[i] = m;
                    }
                }));

            Il2CppStructArray<Matrix4x4> poseArr = null;
            Il2CppReferenceArray<Transform> boneArr = null;
            Must("making the arrays of bones and bind poses", () =>
            {
                poseArr = new Il2CppStructArray<Matrix4x4>(nb);
                boneArr = new Il2CppReferenceArray<Transform>(nb);
                for (int i = 0; i < nb; i++) { poseArr[i] = poses[i]; boneArr[i] = bones[i]; }
            });

            // generous on purpose: the renderer measures the body again every frame (updateWhenOffscreen); should that
            // not be available, this box around the hips still holds the whole figure in any pose
            Bounds wide = Box(0f, 0f, 0f, 3.2f, 3.2f, 3.2f);

            int backIndices = 0, backVerts = 0;
            foreach (var part in file.Parts)
            {
                var piece = new Piece { Colour = part.Colour };
                _pieces.Add(piece);
                BuildPiece(piece, part, root, bones, boneArr, poseArr, poses, wide, materialFor, ref backVerts, ref backIndices);
                Verts += part.Verts; Tris += part.Tris;
            }

            CoopMod.Log($"Skinned body built: {Parts} parts, {Verts} verts, {Tris} tris on {nb} bones; read back from the game: {(backVerts < 0 ? "?" : backVerts.ToString())} verts, {(backIndices < 0 ? "?" : backIndices.ToString())} indices; {ApiText}");
        }

        private static void BuildPiece(Piece piece, SurvivorFile.Part part, Transform root, Transform[] bones, Il2CppReferenceArray<Transform> boneArr,
            Il2CppStructArray<Matrix4x4> poseArr, Matrix4x4[] poses, Bounds wide, Func<int, Material> materialFor, ref int backVerts, ref int backIndices)
        {
            int nv = part.Verts, ni = part.Idx.Length;
            string label = $"part {part.Index} ({Figure.ColourName(part.Colour)})";

            Mesh mesh = null;
            Must($"{label}: new Mesh()", () => { mesh = new Mesh(); if (mesh == null) throw new InvalidOperationException("the game returned no mesh"); });
            piece.Mesh = mesh;
            try { mesh.name = "TLDCoop_Body_" + Figure.ColourName(part.Colour); } catch { }

            // ---- the data, in the game's own kinds of array ----
            Il2CppStructArray<Vector3> verts = null, norms = null;
            Il2CppStructArray<int> tris = null;
            Il2CppStructArray<BoneWeight> weights = null;
            Must($"{label}: making the arrays", () =>
            {
                verts = Vectors(part.Pos, nv);
                norms = Vectors(part.Nrm, nv);
                tris = new Il2CppStructArray<int>(ni);
                for (int i = 0; i < ni; i++) tris[i] = part.Idx[i];
                weights = new Il2CppStructArray<BoneWeight>(nv);
                BoneWeight w = default;
                for (int i = 0; i < nv; i++)
                {
                    Influences(part, i, out int b0, out float w0, out int b1, out float w1);
                    w.m_BoneIndex0 = b0; w.m_Weight0 = w0; w.m_BoneIndex1 = b1; w.m_Weight1 = w1;
                    w.m_BoneIndex2 = 0; w.m_Weight2 = 0f; w.m_BoneIndex3 = 0; w.m_Weight3 = 0f;
                    weights[i] = w;
                }
            });

            Step("vertices",
                ("mesh.vertices = array", () => { mesh.vertices = verts; Expect("vertices", ReadBack("mesh.vertexCount", () => mesh.vertexCount), nv); }),
                ("mesh.SetVertices(array)", () => { mesh.SetVertices(verts); Expect("vertices", ReadBack("mesh.vertexCount", () => mesh.vertexCount), nv); }),
                ("mesh.SetVertices(List)", () => { mesh.SetVertices(ListOf(part.Pos, nv)); Expect("vertices", ReadBack("mesh.vertexCount", () => mesh.vertexCount), nv); }));

            Step("triangles",
                ("mesh.triangles = array", () => { mesh.triangles = tris; Expect("indices", ReadBack("mesh.GetIndexCount(0)", () => (int)mesh.GetIndexCount(0)), ni); }),
                ("mesh.SetTriangles(array, 0)", () => { mesh.SetTriangles(tris, 0); Expect("indices", ReadBack("mesh.GetIndexCount(0)", () => (int)mesh.GetIndexCount(0)), ni); }),
                ("mesh.SetIndices(array, Triangles, 0)", () => { mesh.SetIndices(tris, MeshTopology.Triangles, 0); Expect("indices", ReadBack("mesh.GetIndexCount(0)", () => (int)mesh.GetIndexCount(0)), ni); }),
                ("mesh.SetTriangles(List, 0)", () =>
                {
                    var list = new Il2CppSystem.Collections.Generic.List<int>();
                    for (int i = 0; i < ni; i++) list.Add(part.Idx[i]);
                    mesh.SetTriangles(list, 0);
                    Expect("indices", ReadBack("mesh.GetIndexCount(0)", () => (int)mesh.GetIndexCount(0)), ni);
                }));

            Step("normals",
                ("mesh.normals = array", () => mesh.normals = norms),
                ("mesh.SetNormals(array)", () => mesh.SetNormals(norms)),
                ("mesh.SetNormals(List)", () => mesh.SetNormals(ListOf(part.Nrm, nv))),
                ("mesh.RecalculateNormals() (the model's own normals could not be set: every edge is shaded smooth)", () => mesh.RecalculateNormals()));

            Step("bone weights",
                ("mesh.boneWeights = array", () => { mesh.boneWeights = weights; ExpectWeights(mesh); }),
                ("mesh.SetBoneWeightsImpl(array)", () => { mesh.SetBoneWeightsImpl(weights); ExpectWeights(mesh); }),
                ("mesh.InternalSetBoneWeights(pointers)", () => { RawWeights(mesh, part); ExpectWeights(mesh); }));

            Step("bind poses",
                ("mesh.bindposes = array", () => { mesh.bindposes = poseArr; Expect("bind poses", ReadBack("mesh.bindposeCount", () => mesh.bindposeCount), poses.Length); }),
                ("mesh.SetBindposesFromScript_NativeArray(pointer)", () => { RawPoses(mesh, poses); Expect("bind poses", ReadBack("mesh.bindposeCount", () => mesh.bindposeCount), poses.Length); }));

            // the box of the mesh at rest; setting the vertices has usually done this already
            try
            {
                Step("mesh bounds",
                    ("mesh.RecalculateBounds()", () => mesh.RecalculateBounds()),
                    ("mesh.bounds = box of the vertices", () => mesh.bounds = BoxOf(part)));
            }
            catch (Refused e) { if (_said.Add("bounds")) CoopMod.Log("Skinned body: " + e.Message + "; the body is used without it"); }

            int bv = ReadBack("mesh.vertexCount", () => mesh.vertexCount), bi = ReadBack("mesh.GetIndexCount(0)", () => (int)mesh.GetIndexCount(0));
            // (a count that cannot be read back stays unknown for the whole body: "?" in the log, not a number that is too small)
            backVerts = bv < 0 || backVerts < 0 ? -1 : backVerts + bv;
            backIndices = bi < 0 || backIndices < 0 ? -1 : backIndices + bi;

            // ---- the object that draws it: under the figure's root, not under a joint (the joints move the vertices) ----
            GameObject go = null;
            Must($"{label}: new GameObject", () =>
            {
                go = new GameObject("Body_" + Figure.ColourName(part.Colour));
                piece.Go = go;
                go.transform.SetParent(root, false);
            });

            SkinnedMeshRenderer smr = null;
            Step("renderer",
                ("go.AddComponent<SkinnedMeshRenderer>()", () =>
                {
                    smr = go.AddComponent<SkinnedMeshRenderer>();
                    if (smr == null) throw new InvalidOperationException("the game returned no component");
                }),
                ("go.AddComponent(Il2CppType.Of<SkinnedMeshRenderer>())", () =>
                {
                    var type = Il2CppType.Of<SkinnedMeshRenderer>();
                    var c = go.GetComponent(type);          // the first way may have added it and failed after that
                    if (c == null) c = go.AddComponent(type);
                    smr = c == null ? null : c.Cast<SkinnedMeshRenderer>();
                    if (smr == null) throw new InvalidOperationException("the game returned no component");
                }));
            piece.Renderer = smr;

            Must($"{label}: renderer.sharedMesh", () => smr.sharedMesh = mesh);
            Must($"{label}: renderer.bones", () => smr.bones = boneArr);
            Material mat = null;
            Must($"{label}: the material for colour {part.Colour}", () => { mat = materialFor(part.Colour); if (mat == null) throw new InvalidOperationException("no material"); });
            Step("material",
                ("renderer.sharedMaterial = material", () => smr.sharedMaterial = mat),
                ("renderer.material = material", () => smr.material = mat));
            Optional("renderer.rootBone", () => smr.rootBone = bones[0]);
            Optional("renderer.quality = Bone2", () => smr.quality = SkinQuality.Bone2);
            Optional("renderer.localBounds", () => smr.localBounds = wide);
            Optional("renderer.updateWhenOffscreen", () => smr.updateWhenOffscreen = true);

            // what the renderer holds now
            int nBones = ReadBack("renderer.bones", () => { var arr = smr.bones; return arr == null ? 0 : arr.Length; });
            Expect("bones on the renderer", nBones, poses.Length);
            int hasMesh = ReadBack("renderer.sharedMesh", () => smr.sharedMesh != null ? 1 : 0);
            if (hasMesh == 0) throw new Refused($"{label}: the renderer does not hold the mesh that was set");
        }

        private static void ExpectWeights(Mesh mesh)
        {
            int has = ReadBack("mesh.HasBoneWeights()", () => mesh.HasBoneWeights() ? 1 : 0);
            if (has == 0) throw new InvalidOperationException("the game says the mesh has no bone weights after they were set");
        }

        /// <summary>The (at most two) bones of a vertex, the heavier one first, as the game wants them.</summary>
        private static void Influences(SurvivorFile.Part part, int v, out int b0, out float w0, out int b1, out float w1)
        {
            int f0 = part.Bone0[v], f1 = part.Bone1[v];
            float f = part.Weight0[v];
            if (f0 == f1 || f >= 0.999f) { b0 = f0; w0 = 1f; b1 = f0; w1 = 0f; }
            else if (f <= 0.001f) { b0 = f1; w0 = 1f; b1 = f1; w1 = 0f; }
            else if (f >= 0.5f) { b0 = f0; w0 = f; b1 = f1; w1 = 1f - f; }
            else { b0 = f1; w0 = 1f - f; b1 = f0; w1 = f; }
        }

        private static Il2CppStructArray<Vector3> Vectors(float[] src, int n)
        {
            var arr = new Il2CppStructArray<Vector3>(n);
            Vector3 v = default;
            for (int i = 0, q = 0; i < n; i++, q += 3) { v.x = src[q]; v.y = src[q + 1]; v.z = src[q + 2]; arr[i] = v; }
            return arr;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector3> ListOf(float[] src, int n)
        {
            var list = new Il2CppSystem.Collections.Generic.List<Vector3>();
            Vector3 v = default;
            for (int i = 0, q = 0; i < n; i++, q += 3) { v.x = src[q]; v.y = src[q + 1]; v.z = src[q + 2]; list.Add(v); }
            return list;
        }

        private static Bounds BoxOf(SurvivorFile.Part part)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0, q = 0; i < part.Verts; i++, q += 3)
            {
                float x = part.Pos[q], y = part.Pos[q + 1], z = part.Pos[q + 2];
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (z < z0) z0 = z; if (z > z1) z1 = z;
            }
            return Box((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f, x1 - x0, y1 - y0, z1 - z0);
        }

        // (filled in field by field: no call into the game is needed to make a box)
        private static Bounds Box(float cx, float cy, float cz, float sx, float sy, float sz)
        {
            Vector3 c = default, e = default;
            c.x = cx; c.y = cy; c.z = cz;
            e.x = sx * 0.5f; e.y = sy * 0.5f; e.z = sz * 0.5f;
            Bounds b = default;
            b.m_Center = c; b.m_Extents = e;
            return b;
        }

        /// <summary>a * b, worked out on this side (row r, column c of the result = row r of a times column c of b).</summary>
        private static Matrix4x4 Mul(Matrix4x4 a, Matrix4x4 b)
        {
            Matrix4x4 r = default;
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
            r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
            r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
            r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
            r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
            r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
            r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
            r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
            r.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
            r.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
            r.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
            r.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
            return r;
        }

        /// <summary>
        /// The bone weights through the game's pointer call (what Unity's own SetBoneWeights(NativeArray, NativeArray)
        /// ends in): one byte per vertex = how many bones, then (weight, bone) pairs, the heavier first.
        /// </summary>
        private static unsafe void RawWeights(Mesh mesh, SurvivorFile.Part part)
        {
            int nv = part.Verts;
            IntPtr per = Marshal.AllocHGlobal(nv), pairs = Marshal.AllocHGlobal(nv * 2 * 8);
            try
            {
                byte* pp = (byte*)per; byte* pw = (byte*)pairs;
                int count = 0;
                for (int i = 0; i < nv; i++)
                {
                    Influences(part, i, out int b0, out float w0, out int b1, out float w1);
                    *(float*)(pw + count * 8) = w0; *(int*)(pw + count * 8 + 4) = b0; count++;
                    if (w1 > 0f) { *(float*)(pw + count * 8) = w1; *(int*)(pw + count * 8 + 4) = b1; count++; pp[i] = 2; }
                    else pp[i] = 1;
                }
                mesh.InternalSetBoneWeights(per, nv, pairs, count);
            }
            finally { Marshal.FreeHGlobal(per); Marshal.FreeHGlobal(pairs); }
        }

        /// <summary>The bind poses through the game's pointer call: 16 numbers per matrix, column after column, as Unity keeps a matrix.</summary>
        private static unsafe void RawPoses(Mesh mesh, Matrix4x4[] poses)
        {
            IntPtr mem = Marshal.AllocHGlobal(poses.Length * 64);
            try
            {
                float* f = (float*)mem;
                for (int i = 0; i < poses.Length; i++)
                {
                    var m = poses[i];
                    f[0] = m.m00; f[1] = m.m10; f[2] = m.m20; f[3] = m.m30;
                    f[4] = m.m01; f[5] = m.m11; f[6] = m.m21; f[7] = m.m31;
                    f[8] = m.m02; f[9] = m.m12; f[10] = m.m22; f[11] = m.m32;
                    f[12] = m.m03; f[13] = m.m13; f[14] = m.m23; f[15] = m.m33;
                    f += 16;
                }
                mesh.SetBindposesFromScript_NativeArray(mem, poses.Length);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // =====================================================================================
        // using it
        // =====================================================================================
        /// <summary>Shows or hides every part of one colour (a hat that is not worn, mitts that are off).</summary>
        internal void Show(int colour, bool show)
        {
            foreach (var p in _pieces)
            {
                if (p.Colour != colour || p.Shown == show || p.Go == null) continue;
                p.Go.SetActive(show);
                p.Shown = show;
            }
        }

        /// <summary>
        /// For the state dump, so that a test can tell without a screenshot whether the body is really there: how many
        /// parts still exist, how many of the shown ones a camera has in view, and the size of the box the game draws
        /// them in (world space: about 0.6 x 1.85 x 0.55 standing, long and flat lying down).
        /// </summary>
        internal string Report()
        {
            int alive = 0, shown = 0, seen = 0;
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            string problem = "";
            foreach (var p in _pieces)
            {
                try
                {
                    if (p.Go == null || p.Mesh == null || p.Renderer == null) continue;
                    alive++;
                    if (!p.Shown) continue;
                    shown++;
                    if (p.Renderer.isVisible) seen++;
                    var b = p.Renderer.bounds;
                    var c = b.m_Center; var e = b.m_Extents;
                    if (c.x - e.x < x0) x0 = c.x - e.x; if (c.x + e.x > x1) x1 = c.x + e.x;
                    if (c.y - e.y < y0) y0 = c.y - e.y; if (c.y + e.y > y1) y1 = c.y + e.y;
                    if (c.z - e.z < z0) z0 = c.z - e.z; if (c.z + e.z > z1) z1 = c.z + e.z;
                }
                catch (Exception ex) { problem = " (" + Short(ex) + ")"; }
            }
            string box = shown > 0 && x1 >= x0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.00}x{1:0.00}x{2:0.00}", x1 - x0, y1 - y0, z1 - z0) : "-";
            return $"{alive}/{_pieces.Count} parts alive, {seen}/{shown} in view, box {box}{problem}";
        }

        /// <summary>The meshes are ours to clean up (the objects would go with the root, the meshes would not).</summary>
        internal void Destroy()
        {
            foreach (var p in _pieces)
            {
                try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch { }
                try { if (p.Mesh != null) UnityEngine.Object.Destroy(p.Mesh); } catch { }
                p.Go = null; p.Mesh = null; p.Renderer = null;
            }
            _pieces.Clear();
        }
    }
}
