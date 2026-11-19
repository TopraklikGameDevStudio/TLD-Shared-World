#!/usr/bin/env python3
"""
check_survivor.py - reads a survivor.bin exactly the way the mod does (SurvivorFile.cs) and prints what it finds.

    python3 check_survivor.py [survivor.bin]

The same checks, in the same order, with the same words as the mod's log. Exit code 0 = the mod would use the file,
1 = the mod would refuse it (and draw the figure from the 30 basic shapes instead).
What the mod checks at a later point, against the joints it has really made, is done here against the table of
joint positions from Figure.Make: each bone's rest position must be within 2 mm.
"""
import math
import struct
import sys

BONE_NAMES = ["Hips", "Spine", "Head", "Pack", "ShoulderL", "ElbowL", "HandL", "ShoulderR", "ElbowR", "HandR",
              "HipL", "KneeL", "AnkleL", "HipR", "KneeR", "AnkleR"]
# where Figure.Make puts the joints (root space, rest pose)
JOINTS = [(0.0, 0.94, 0.0), (0.0, 1.00, 0.0), (0.0, 1.50, 0.01), (0.0, 1.25, -0.145),
          (-0.235, 1.435, 0.0), (-0.235, 1.145, 0.0), (-0.235, 0.845, 0.03),
          (0.235, 1.435, 0.0), (0.235, 1.145, 0.0), (0.235, 0.845, 0.03),
          (-0.095, 0.92, 0.0), (-0.095, 0.49, 0.0), (-0.095, 0.09, 0.0),
          (0.095, 0.92, 0.0), (0.095, 0.49, 0.0), (0.095, 0.09, 0.0)]
COLOUR_NAMES = ["Parka", "Trousers", "Hat", "Cuff", "Pack", "Mitts", "Boots", "Scarf", "Skin", "Hair", "Bedroll"]
BONE_COUNT, MAX_COLOUR, VERTEX_BYTES, REST_TOLERANCE = 16, 10, 30, 0.002


class Refused(Exception):
    pass


def finite(*values):
    return all(math.isfinite(v) for v in values)


def f32str(v):
    """A 32 bit number the way C# writes it with "R": the fewest digits that still give the same number."""
    if not math.isfinite(v):
        return "NaN" if math.isnan(v) else ("Infinity" if v > 0 else "-Infinity")
    for digits in range(1, 10):
        s = "%.*g" % (digits, v)
        if struct.unpack("<f", struct.pack("<f", float(s)))[0] == v:
            break
    if "e" in s:
        m, e = s.split("e")
        s = "%sE%s%02d" % (m, "+" if int(e) >= 0 else "-", abs(int(e)))
    return s


def parse(b):
    """Returns (rest, parts, notes, parts_in_file). Raises Refused with the reason the mod would log."""
    notes = []
    o = 0

    def need(n, what):
        if o + n > len(b):
            raise Refused("the file ends early: %s needs %d byte(s) at offset %d, the file has %d" % (what, n, o, len(b)))

    need(6, "the header")
    if b[0:4] != b"TLDF":
        raise Refused("not a TLDF file (it starts with %s)" % " ".join("%02X" % x for x in b[0:4]))
    version = struct.unpack_from("<H", b, 4)[0]
    o = 6
    if version != 1:
        raise Refused("version %d, this mod reads version 1" % version)
    need(2, "the bone count")
    bone_count = struct.unpack_from("<H", b, o)[0]
    o += 2
    if bone_count != BONE_COUNT:
        raise Refused("%d bones, %d expected" % (bone_count, BONE_COUNT))
    rest = []
    for i in range(bone_count):
        need(1, "the name length of bone %d" % i)
        n = b[o]
        o += 1
        need(n, "the name of bone %d" % i)
        name = "".join(chr(c) if 32 <= c < 127 else "?" for c in b[o:o + n])
        o += n
        if name != BONE_NAMES[i]:
            raise Refused("bone %d is called '%s', expected '%s'" % (i, name, BONE_NAMES[i]))
        need(12, "the rest position of bone %s" % name)
        p = struct.unpack_from("<3f", b, o)
        o += 12
        if not finite(*p):
            raise Refused("bone %s: the rest position is not a number" % name)
        rest.append(p)
    need(2, "the part count")
    part_count = struct.unpack_from("<H", b, o)[0]
    o += 2
    if part_count < 1 or part_count > 64:
        raise Refused("%d parts (1 to 64 expected)" % part_count)
    parts = []
    seen = set()
    for p in range(part_count):
        need(9, "the header of part %d" % p)
        colour, vc, ic = struct.unpack_from("<BII", b, o)
        o += 9
        if colour > MAX_COLOUR:
            raise Refused("part %d: colour index %d (0 to %d expected)" % (p, colour, MAX_COLOUR))
        if vc > 65536:
            raise Refused("part %d: %d vertices (at most 65536, the indices are 16 bit)" % (p, vc))
        if ic % 3 != 0:
            raise Refused("part %d: %d indices is not a whole number of triangles" % (p, ic))
        need(vc * VERTEX_BYTES, "the %d vertices of part %d" % (vc, p))
        pos, nrm, bone0, bone1, weight0 = [], [], [], [], []
        fixed = two = 0
        for v in range(vc):
            px, py, pz, nx, ny, nz, b0, b1, w0 = struct.unpack_from("<3f3fBBf", b, o)
            o += VERTEX_BYTES
            if not finite(px, py, pz):
                raise Refused("part %d vertex %d: the position is not a number" % (p, v))
            if not finite(nx, ny, nz):
                raise Refused("part %d vertex %d: the normal is not a number" % (p, v))
            if b0 >= BONE_COUNT or b1 >= BONE_COUNT:
                raise Refused("part %d vertex %d: bone index %d (0 to %d expected)" % (p, v, b0 if b0 >= BONE_COUNT else b1, BONE_COUNT - 1))
            if not finite(w0) or w0 < -0.001 or w0 > 1.001:
                raise Refused("part %d vertex %d: weight %s (0 to 1 expected)" % (p, v, f32str(w0)))
            w0 = min(1.0, max(0.0, w0))
            length = math.sqrt(nx * nx + ny * ny + nz * nz)
            if abs(length - 1.0) > 0.01:
                fixed += 1
                if length < 1e-6:
                    nx, ny, nz = 0.0, 1.0, 0.0
                else:
                    nx, ny, nz = nx / length, ny / length, nz / length
            if b0 != b1 and 0.001 < w0 < 0.999:
                two += 1
            pos.append((px, py, pz))
            nrm.append((nx, ny, nz))
            bone0.append(b0)
            bone1.append(b1)
            weight0.append(w0)
        need(ic * 2, "the %d indices of part %d" % (ic, p))
        idx = struct.unpack_from("<%dH" % ic, b, o)
        o += ic * 2
        for k, i in enumerate(idx):
            if i >= vc:
                raise Refused("part %d: index %d points at vertex %d, the part has %d" % (p, k, i, vc))
        if vc == 0 or ic == 0:
            notes.append("part %d (colour %d) is empty: skipped" % (p, colour))
            continue
        against = degenerate = 0
        for t in range(0, ic, 3):
            a, bb, c = idx[t], idx[t + 1], idx[t + 2]
            e1 = [pos[bb][k] - pos[a][k] for k in range(3)]
            e2 = [pos[c][k] - pos[a][k] for k in range(3)]
            g = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            if g[0] * g[0] + g[1] * g[1] + g[2] * g[2] < 1e-16:
                degenerate += 1
                continue
            s = [nrm[a][k] + nrm[bb][k] + nrm[c][k] for k in range(3)]
            if g[0] * s[0] + g[1] * s[1] + g[2] * s[2] < 0.0:
                against += 1
        if fixed:
            notes.append("part %d: %d normal(s) were not of length 1 and have been made so" % (p, fixed))
        if against:
            notes.append("part %d: %d of %d triangles face against their normals (winding or normals flipped?)" % (p, against, ic // 3))
        if colour in seen:
            notes.append("colour %d is used by more than one part (allowed)" % colour)
        seen.add(colour)
        parts.append(dict(index=p, colour=colour, pos=pos, nrm=nrm, bone0=bone0, bone1=bone1, weight0=weight0, idx=idx,
                          two=two, against=against, degenerate=degenerate, fixed=fixed))
    if o != len(b):
        notes.append("%d byte(s) after the last part are ignored" % (len(b) - o))
    if not parts:
        raise Refused("no part with any triangles")
    return rest, parts, notes, part_count


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "survivor.bin"
    with open(path, "rb") as f:
        b = f.read()
    print("%s: %d bytes" % (path, len(b)))
    try:
        rest, parts, notes, in_file = parse(b)
        # what the mod checks when it builds a figure: every rest position within 2 mm of the joint it has made
        print("bones: %d" % len(rest))
        for i, (name, p) in enumerate(zip(BONE_NAMES, rest)):
            d = math.sqrt(sum((p[k] - JOINTS[i][k]) ** 2 for k in range(3)))
            print("  %2d %-10s (%8.4f, %8.4f, %8.4f)  %.2f mm from the joint" % (i, name, p[0], p[1], p[2], d * 1000.0))
        for i, (name, p) in enumerate(zip(BONE_NAMES, rest)):
            d = math.sqrt(sum((p[k] - JOINTS[i][k]) ** 2 for k in range(3)))
            if d > REST_TOLERANCE:
                raise Refused("bone %s: the rest position in the file (%.4f, %.4f, %.4f) is %.1f mm from the joint (%.4f, %.4f, %.4f), at most 2 mm allowed"
                              % (name, p[0], p[1], p[2], d * 1000.0, JOINTS[i][0], JOINTS[i][1], JOINTS[i][2]))
    except Refused as e:
        print("RESULT: REFUSED: %s" % e)
        return 1
    verts = tris = 0
    lo, hi = [1e30] * 3, [-1e30] * 3
    print("parts: %d in the file, %d used" % (in_file, len(parts)))
    for part in parts:
        used = sorted(set(part["bone0"]) | set(b1 for b1, w0 in zip(part["bone1"], part["weight0"]) if w0 < 0.999))
        plo = [min(v[k] for v in part["pos"]) for k in range(3)]
        phi = [max(v[k] for v in part["pos"]) for k in range(3)]
        for k in range(3):
            lo[k], hi[k] = min(lo[k], plo[k]), max(hi[k], phi[k])
        n_v, n_t = len(part["pos"]), len(part["idx"]) // 3
        verts += n_v
        tris += n_t
        print("  part %2d colour %2d %-8s %5d verts %5d tris  two-bone verts %4d  against normals %d  degenerate %d" %
              (part["index"], part["colour"], COLOUR_NAMES[part["colour"]], n_v, n_t, part["two"], part["against"], part["degenerate"]))
        print("          x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f  bones: %s" %
              (plo[0], phi[0], plo[1], phi[1], plo[2], phi[2], ", ".join(BONE_NAMES[i] for i in used)))
    missing = [COLOUR_NAMES[c] for c in range(MAX_COLOUR + 1) if c not in set(p["colour"] for p in parts)]
    print("total: %d parts, %d verts, %d tris; box x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" %
          (len(parts), verts, tris, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    if missing:
        print("colours without a part: %s" % ", ".join(missing))
    for n in notes:
        print("note: %s" % n)
    print("RESULT: OK: skinned: %d verts, %d tris, %d parts" % (verts, tris, len(parts)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
