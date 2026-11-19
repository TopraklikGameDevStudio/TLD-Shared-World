using System;
using System.Collections.Generic;
using System.Globalization;

namespace TLDCoop
{
    /// <summary>
    /// The model of the friend's body as it comes in the file survivor.bin (format "TLDF", version 1, little endian).
    /// Reading only: nothing in here touches the game, so it can be tried outside it (check_survivor.py reads the
    /// same way, check for check and word for word).
    ///
    ///   4 bytes "TLDF", u16 version = 1
    ///   u16 boneCount (= 16); per bone: u8 nameLength, ASCII name, 3 x f32 rest position in the figure root's space
    ///   u16 partCount; per part: u8 colourIndex (0..10), u32 vertexCount, u32 indexCount,
    ///       per vertex: 3 x f32 position, 3 x f32 normal, u8 bone0, u8 bone1, f32 weight0 (weight1 = 1 - weight0),
    ///       then indexCount x u16 triangle indices
    /// </summary>
    internal sealed class SurvivorFile
    {
        internal const int BoneCount = 16, MaxColour = 10, VertexBytes = 30;
        /// <summary>The bones in the order of the file; Figure hands its joints over in this order.</summary>
        internal static readonly string[] BoneNames =
        {
            "Hips", "Spine", "Head", "Pack", "ShoulderL", "ElbowL", "HandL", "ShoulderR", "ElbowR", "HandR",
            "HipL", "KneeL", "AnkleL", "HipR", "KneeR", "AnkleR",
        };

        internal sealed class Part
        {
            public int Index, Colour, Verts;
            public float[] Pos, Nrm;        // 3 per vertex
            public byte[] Bone0, Bone1;
            public float[] Weight0;
            public ushort[] Idx;
            public int TwoBone, Against, Degenerate, Fixed;
            public int Tris => Idx.Length / 3;
        }

        internal int Bytes, PartsInFile, Verts, Tris;
        internal readonly float[] Rest = new float[BoneCount * 3];
        internal readonly List<Part> Parts = new();
        internal readonly List<string> Notes = new();
        internal readonly float[] Min = { float.MaxValue, float.MaxValue, float.MaxValue }, Max = { float.MinValue, float.MinValue, float.MinValue };

        internal string Summary => string.Format(CultureInfo.InvariantCulture, "{0} bytes, version 1, {1} bones, {2} parts, {3} verts, {4} tris", Bytes, BoneCount, Parts.Count, Verts, Tris);

        private static string S(float v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Reads the file. Throws FormatException with the reason when the mod must not use it.</summary>
        internal static SurvivorFile Parse(byte[] b)
        {
            if (b == null) throw new FormatException("no data");
            var f = new SurvivorFile { Bytes = b.Length };
            int o = 0;

            void Need(long n, string what)
            {
                if (o + n > b.Length) throw new FormatException($"the file ends early: {what} needs {n} byte(s) at offset {o}, the file has {b.Length}");
            }
            int U8() => b[o++];
            int U16() { int v = b[o] | (b[o + 1] << 8); o += 2; return v; }
            long U32() { long v = (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); o += 4; return v; }
            float F32() { int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); o += 4; return BitConverter.Int32BitsToSingle(v); }

            Need(6, "the header");
            if (b[0] != (byte)'T' || b[1] != (byte)'L' || b[2] != (byte)'D' || b[3] != (byte)'F')
                throw new FormatException($"not a TLDF file (it starts with {b[0]:X2} {b[1]:X2} {b[2]:X2} {b[3]:X2})");
            o = 4;
            int version = U16();
            if (version != 1) throw new FormatException($"version {version}, this mod reads version 1");
            Need(2, "the bone count");
            int boneCount = U16();
            if (boneCount != BoneCount) throw new FormatException($"{boneCount} bones, {BoneCount} expected");
            for (int i = 0; i < boneCount; i++)
            {
                Need(1, $"the name length of bone {i}");
                int n = U8();
                Need(n, $"the name of bone {i}");
                var chars = new char[n];
                for (int k = 0; k < n; k++) { int c = b[o + k]; chars[k] = c >= 32 && c < 127 ? (char)c : '?'; }
                o += n;
                string name = new string(chars);
                if (name != BoneNames[i]) throw new FormatException($"bone {i} is called '{name}', expected '{BoneNames[i]}'");
                Need(12, $"the rest position of bone {name}");
                float x = F32(), y = F32(), z = F32();
                if (!Finite(x) || !Finite(y) || !Finite(z)) throw new FormatException($"bone {name}: the rest position is not a number");
                f.Rest[i * 3] = x; f.Rest[i * 3 + 1] = y; f.Rest[i * 3 + 2] = z;
            }
            Need(2, "the part count");
            int partCount = U16();
            if (partCount < 1 || partCount > 64) throw new FormatException($"{partCount} parts (1 to 64 expected)");
            f.PartsInFile = partCount;
            var seen = new HashSet<int>();
            for (int p = 0; p < partCount; p++)
            {
                Need(9, $"the header of part {p}");
                int colour = U8(); long vc = U32(), ic = U32();
                if (colour > MaxColour) throw new FormatException($"part {p}: colour index {colour} (0 to {MaxColour} expected)");
                if (vc > 65536) throw new FormatException($"part {p}: {vc} vertices (at most 65536, the indices are 16 bit)");
                if (ic % 3 != 0) throw new FormatException($"part {p}: {ic} indices is not a whole number of triangles");
                Need(vc * VertexBytes, $"the {vc} vertices of part {p}");
                int nv = (int)vc;
                var part = new Part { Index = p, Colour = colour, Verts = nv, Pos = new float[nv * 3], Nrm = new float[nv * 3], Bone0 = new byte[nv], Bone1 = new byte[nv], Weight0 = new float[nv] };
                for (int v = 0; v < nv; v++)
                {
                    float px = F32(), py = F32(), pz = F32(), nx = F32(), ny = F32(), nz = F32();
                    int b0 = U8(), b1 = U8();
                    float w0 = F32();
                    if (!Finite(px) || !Finite(py) || !Finite(pz)) throw new FormatException($"part {p} vertex {v}: the position is not a number");
                    if (!Finite(nx) || !Finite(ny) || !Finite(nz)) throw new FormatException($"part {p} vertex {v}: the normal is not a number");
                    if (b0 >= BoneCount || b1 >= BoneCount) throw new FormatException($"part {p} vertex {v}: bone index {(b0 >= BoneCount ? b0 : b1)} (0 to {BoneCount - 1} expected)");
                    if (!Finite(w0) || w0 < -0.001f || w0 > 1.001f) throw new FormatException($"part {p} vertex {v}: weight {w0.ToString("R", CultureInfo.InvariantCulture)} (0 to 1 expected)");
                    w0 = w0 < 0f ? 0f : w0 > 1f ? 1f : w0;
                    double len = Math.Sqrt((double)nx * nx + (double)ny * ny + (double)nz * nz);
                    if (Math.Abs(len - 1.0) > 0.01)
                    {
                        part.Fixed++;
                        if (len < 1e-6) { nx = 0f; ny = 1f; nz = 0f; }
                        else { nx = (float)(nx / len); ny = (float)(ny / len); nz = (float)(nz / len); }
                    }
                    if (b0 != b1 && w0 > 0.001f && w0 < 0.999f) part.TwoBone++;
                    int q = v * 3;
                    part.Pos[q] = px; part.Pos[q + 1] = py; part.Pos[q + 2] = pz;
                    part.Nrm[q] = nx; part.Nrm[q + 1] = ny; part.Nrm[q + 2] = nz;
                    part.Bone0[v] = (byte)b0; part.Bone1[v] = (byte)b1; part.Weight0[v] = w0;
                }
                Need(ic * 2, $"the {ic} indices of part {p}");
                int ni = (int)ic;
                part.Idx = new ushort[ni];
                for (int k = 0; k < ni; k++)
                {
                    int i = U16();
                    if (i >= nv) throw new FormatException($"part {p}: index {k} points at vertex {i}, the part has {nv}");
                    part.Idx[k] = (ushort)i;
                }
                if (nv == 0 || ni == 0) { f.Notes.Add($"part {p} (colour {colour}) is empty: skipped"); continue; }

                // which way do the triangles face: cross(b - a, c - a) is Unity's front, and must go with the normals
                for (int t = 0; t < ni; t += 3)
                {
                    int a = part.Idx[t] * 3, bb = part.Idx[t + 1] * 3, c = part.Idx[t + 2] * 3;
                    double e1x = (double)part.Pos[bb] - part.Pos[a], e1y = (double)part.Pos[bb + 1] - part.Pos[a + 1], e1z = (double)part.Pos[bb + 2] - part.Pos[a + 2];
                    double e2x = (double)part.Pos[c] - part.Pos[a], e2y = (double)part.Pos[c + 1] - part.Pos[a + 1], e2z = (double)part.Pos[c + 2] - part.Pos[a + 2];
                    double gx = e1y * e2z - e1z * e2y, gy = e1z * e2x - e1x * e2z, gz = e1x * e2y - e1y * e2x;
                    if (gx * gx + gy * gy + gz * gz < 1e-16) { part.Degenerate++; continue; }
                    double sx = (double)part.Nrm[a] + part.Nrm[bb] + part.Nrm[c], sy = (double)part.Nrm[a + 1] + part.Nrm[bb + 1] + part.Nrm[c + 1], sz = (double)part.Nrm[a + 2] + part.Nrm[bb + 2] + part.Nrm[c + 2];
                    if (gx * sx + gy * sy + gz * sz < 0.0) part.Against++;
                }
                if (part.Fixed > 0) f.Notes.Add($"part {p}: {part.Fixed} normal(s) were not of length 1 and have been made so");
                if (part.Against > 0) f.Notes.Add($"part {p}: {part.Against} of {part.Tris} triangles face against their normals (winding or normals flipped?)");
                if (!seen.Add(colour)) f.Notes.Add($"colour {colour} is used by more than one part (allowed)");
                for (int v = 0; v < nv; v++)
                    for (int k = 0; k < 3; k++)
                    {
                        float c = part.Pos[v * 3 + k];
                        if (c < f.Min[k]) f.Min[k] = c;
                        if (c > f.Max[k]) f.Max[k] = c;
                    }
                f.Parts.Add(part);
                f.Verts += nv; f.Tris += part.Tris;
            }
            if (o != b.Length) f.Notes.Add($"{b.Length - o} byte(s) after the last part are ignored");
            if (f.Parts.Count == 0) throw new FormatException("no part with any triangles");
            return f;
        }

        /// <summary>
        /// The bones of the file against the joints that have really been made (root space, 3 numbers per joint):
        /// null when every rest position is within 2 mm, else which bone is where it should not be.
        /// </summary>
        internal string RestMismatch(float[] joints, float tolerance = 0.002f)
        {
            for (int i = 0; i < BoneCount; i++)
            {
                double dx = (double)Rest[i * 3] - joints[i * 3], dy = (double)Rest[i * 3 + 1] - joints[i * 3 + 1], dz = (double)Rest[i * 3 + 2] - joints[i * 3 + 2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > tolerance || double.IsNaN(d))
                    return $"bone {BoneNames[i]}: the rest position in the file ({S(Rest[i * 3])}, {S(Rest[i * 3 + 1])}, {S(Rest[i * 3 + 2])}) is {(d * 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} mm from the joint ({S(joints[i * 3])}, {S(joints[i * 3 + 1])}, {S(joints[i * 3 + 2])}), at most 2 mm allowed";
            }
            return null;
        }
    }
}
