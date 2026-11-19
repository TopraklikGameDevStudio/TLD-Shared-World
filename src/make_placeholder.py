#!/usr/bin/env python3
"""
make_placeholder.py - writes a crude but valid survivor.bin (format "TLDF" version 1) for the TLDCoop figure.

It is a stand-in until the real model is dropped in: tubes and boxes along the bones, a few hundred vertices,
one part per colour index (0..10), sensible two-bone weights at the joints, Unity winding
(for a triangle (a, b, c): cross(b - a, c - a) points out of the body).

    python3 make_placeholder.py [survivor.bin]

File format (little endian):
    4 bytes "TLDF", u16 version = 1
    u16 boneCount (= 16); per bone: u8 nameLength, ASCII name, 3 x f32 rest position (figure root space)
    u16 partCount; per part: u8 colourIndex (0..10), u32 vertexCount, u32 indexCount,
        per vertex: 3 x f32 position, 3 x f32 normal, u8 bone0, u8 bone1, f32 weight0 (weight1 = 1 - weight0),
        then indexCount x u16 triangle indices
Space: Unity's (x right, y up, z forward), feet at y = 0, facing +z, rest pose = the joints as Figure.Make creates them.
"""
import math
import struct
import sys

BONES = [
    ("Hips", (0.0, 0.94, 0.0)), ("Spine", (0.0, 1.00, 0.0)), ("Head", (0.0, 1.50, 0.01)), ("Pack", (0.0, 1.25, -0.145)),
    ("ShoulderL", (-0.235, 1.435, 0.0)), ("ElbowL", (-0.235, 1.145, 0.0)), ("HandL", (-0.235, 0.845, 0.03)),
    ("ShoulderR", (0.235, 1.435, 0.0)), ("ElbowR", (0.235, 1.145, 0.0)), ("HandR", (0.235, 0.845, 0.03)),
    ("HipL", (-0.095, 0.92, 0.0)), ("KneeL", (-0.095, 0.49, 0.0)), ("AnkleL", (-0.095, 0.09, 0.0)),
    ("HipR", (0.095, 0.92, 0.0)), ("KneeR", (0.095, 0.49, 0.0)), ("AnkleR", (0.095, 0.09, 0.0)),
]
B = {name: i for i, (name, _) in enumerate(BONES)}

PARKA, TROUSERS, HAT, CUFF, PACK, MITTS, BOOTS, SCARF, SKIN, HAIR, BEDROLL = range(11)
COLOUR_NAMES = ["Parka", "Trousers", "Hat", "Cuff", "Pack", "Mitts", "Boots", "Scarf", "Skin", "Hair", "Bedroll"]

X, Y, Z = (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)


def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    n = math.sqrt(dot(a, a))
    return (0.0, 1.0, 0.0) if n < 1e-12 else (a[0] / n, a[1] / n, a[2] / n)


def w(bone, other=None, share=1.0):
    """A weight: all on one bone, or 'share' on the first and the rest on the other."""
    return (B[bone], B[other if other else bone], float(share))


class Part:
    def __init__(self, colour):
        self.colour = colour
        self.verts = []   # (pos, normal, bone0, bone1, weight0)
        self.idx = []

    def vert(self, p, n, wt):
        self.verts.append((p, norm(n), wt[0], wt[1], wt[2]))
        return len(self.verts) - 1

    def tri(self, a, b, c, out):
        """One triangle, turned so that cross(b - a, c - a) points the way 'out' does (Unity's front side)."""
        pa, pb, pc = self.verts[a][0], self.verts[b][0], self.verts[c][0]
        if dot(cross(sub(pb, pa), sub(pc, pa)), out) < 0.0:
            b, c = c, b
        self.idx += [a, b, c]

    def loft(self, rings, u=X, v=Z, sides=10, caps=(True, True)):
        """
        A tube through rings. A ring: (centre, radius along u, radius along v, weight).
        u and v span the plane of every ring; the tube runs along cross(v, u) or against it, whatever the centres say.
        """
        first = []
        for (c, ru, rv, wt) in rings:
            start = len(self.verts)
            for j in range(sides):
                a = 2.0 * math.pi * j / sides
                ca, sa = math.cos(a), math.sin(a)
                p = add(c, add(mul(u, ca * ru), mul(v, sa * rv)))
                n = add(mul(u, ca / max(ru, 1e-6)), mul(v, sa / max(rv, 1e-6)))   # the normal of an ellipse
                self.vert(p, n, wt)
            first.append(start)
        for i in range(len(rings) - 1):
            mid = mul(add(rings[i][0], rings[i + 1][0]), 0.5)
            for j in range(sides):
                k = (j + 1) % sides
                a, b, c, d = first[i] + j, first[i] + k, first[i + 1] + k, first[i + 1] + j
                centre = mul(add(add(self.verts[a][0], self.verts[b][0]), add(self.verts[c][0], self.verts[d][0])), 0.25)
                out = sub(centre, mid)
                self.tri(a, b, c, out)
                self.tri(a, c, d, out)
        axis = norm(sub(rings[-1][0], rings[0][0]))
        for end, on in ((0, caps[0]), (len(rings) - 1, caps[1])):
            if not on:
                continue
            c, ru, rv, wt = rings[end]
            out = mul(axis, -1.0) if end == 0 else axis
            centre = self.vert(c, out, wt)
            rim = []
            for j in range(sides):
                a = 2.0 * math.pi * j / sides
                rim.append(self.vert(add(c, add(mul(u, math.cos(a) * ru), mul(v, math.sin(a) * rv))), out, wt))
            for j in range(sides):
                self.tri(centre, rim[j], rim[(j + 1) % sides], out)

    def box(self, centre, size, wt):
        """A box with flat sides: 24 vertices, 12 triangles."""
        hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0
        for n, a, b in ((X, Y, Z), (Y, Z, X), (Z, X, Y)):
            for s in (1.0, -1.0):
                h = dot((hx, hy, hz), (abs(n[0]), abs(n[1]), abs(n[2])))
                ha = dot((hx, hy, hz), (abs(a[0]), abs(a[1]), abs(a[2])))
                hb = dot((hx, hy, hz), (abs(b[0]), abs(b[1]), abs(b[2])))
                out = mul(n, s)
                c = add(centre, mul(out, h))
                q = [self.vert(add(c, add(mul(a, sa * ha), mul(b, sb * hb))), out, wt)
                     for sa, sb in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
                self.tri(q[0], q[1], q[2], out)
                self.tri(q[0], q[2], q[3], out)


def build():
    parts = [Part(i) for i in range(11)]
    parka, trousers, hat, cuff, pack, mitts, boots, scarf, skin, hair, bedroll = parts

    # ---- trousers: seat, then one tube per leg from the hip to above the ankle (bends at the knee) ----
    trousers.loft([((0, 0.84, 0), 0.150, 0.110, w("Hips")),
                   ((0, 0.93, 0), 0.178, 0.128, w("Hips")),
                   ((0, 1.04, 0), 0.172, 0.122, w("Hips", "Spine", 0.5))])
    for side, s in (("L", -1.0), ("R", 1.0)):
        x = 0.095 * s
        hip, knee, ankle = "Hip" + side, "Knee" + side, "Ankle" + side
        trousers.loft([((x, 0.905, 0), 0.086, 0.092, w(hip, "Hips", 0.5)),
                       ((x, 0.800, 0), 0.086, 0.092, w(hip)),
                       ((x, 0.560, 0), 0.072, 0.076, w(hip)),
                       ((x, 0.490, 0), 0.069, 0.072, w(hip, knee, 0.5)),
                       ((x, 0.420, 0), 0.066, 0.069, w(knee)),
                       ((x, 0.140, 0), 0.056, 0.059, w(knee))])
        # ---- boots: a shaft that follows the shin into the ankle, and the foot lying forward ----
        boots.loft([((x, 0.185, 0), 0.071, 0.074, w(knee)),
                    ((x, 0.090, 0), 0.071, 0.076, w(ankle, knee, 0.5)),
                    ((x, 0.020, 0), 0.068, 0.076, w(ankle))])
        boots.box((x, 0.045, 0.060), (0.115, 0.090, 0.250), w(ankle))

        # ---- arms: one tube per arm in the parka's colour (bends at the elbow) ----
        ax = 0.235 * s
        sh, el, hand = "Shoulder" + side, "Elbow" + side, "Hand" + side
        parka.loft([((ax, 1.470, 0), 0.058, 0.060, w(sh, "Spine", 0.5)),
                    ((ax, 1.400, 0), 0.064, 0.064, w(sh)),
                    ((ax, 1.195, 0), 0.056, 0.056, w(sh)),
                    ((ax, 1.145, 0), 0.054, 0.054, w(sh, el, 0.5)),
                    ((ax, 1.095, 0), 0.052, 0.052, w(el)),
                    ((ax, 0.905, 0.012), 0.046, 0.046, w(el))])
        # the bare hand, and the mitt over it (the mitt is hidden when no gloves are worn)
        skin.box((ax, 0.850, 0.028), (0.060, 0.120, 0.075), w(hand))
        mitts.box((ax, 0.840, 0.032), (0.088, 0.150, 0.104), w(hand))
        # ---- pack straps over the chest: they stay with the back, not with the pack ----
        pack.box((0.105 * s, 1.300, 0.128), (0.040, 0.330, 0.030), w("Spine"))

    # ---- parka: the trunk from the hem over the hips to the shoulders ----
    parka.loft([((0, 0.955, 0), 0.200, 0.145, w("Spine", "Hips", 0.5)),
                ((0, 1.020, 0), 0.206, 0.150, w("Spine")),
                ((0, 1.250, 0), 0.202, 0.146, w("Spine")),
                ((0, 1.405, 0), 0.226, 0.132, w("Spine")),
                ((0, 1.470, 0), 0.120, 0.100, w("Spine"))])

    # ---- neck and head ----
    skin.loft([((0, 1.440, 0.005), 0.050, 0.052, w("Spine")),
               ((0, 1.500, 0.008), 0.048, 0.050, w("Head", "Spine", 0.5)),
               ((0, 1.545, 0.010), 0.048, 0.052, w("Head"))])
    skin.loft([((0, 1.525, 0.015), 0.050, 0.056, w("Head")),
               ((0, 1.565, 0.015), 0.086, 0.096, w("Head")),
               ((0, 1.630, 0.015), 0.096, 0.106, w("Head")),
               ((0, 1.700, 0.015), 0.086, 0.096, w("Head")),
               ((0, 1.745, 0.015), 0.042, 0.048, w("Head"))])
    skin.box((0, 1.615, 0.128), (0.030, 0.040, 0.034), w("Head"))          # nose: shows which way they face
    # hair under the hat (it shows when no hat is worn), eyes in the same colour
    hair.loft([((0, 1.655, 0.008), 0.100, 0.110, w("Head")),
               ((0, 1.715, 0.008), 0.092, 0.102, w("Head")),
               ((0, 1.756, 0.008), 0.046, 0.052, w("Head"))])
    for s in (-1.0, 1.0):
        hair.box((0.034 * s, 1.645, 0.116), (0.018, 0.010, 0.010), w("Head"))
    # wool toque with its turned-up edge and a bobble
    hat.loft([((0, 1.690, 0.003), 0.110, 0.120, w("Head")),
              ((0, 1.745, 0.003), 0.102, 0.112, w("Head")),
              ((0, 1.795, 0.003), 0.060, 0.066, w("Head"))])
    cuff.loft([((0, 1.664, 0.004), 0.116, 0.126, w("Head")),
               ((0, 1.702, 0.004), 0.116, 0.126, w("Head"))])
    cuff.box((0, 1.815, -0.006), (0.050, 0.050, 0.050), w("Head"))
    # scarf round the neck: its lower edge stays with the shoulders, the upper edge half follows the head
    scarf.loft([((0, 1.452, 0.004), 0.088, 0.094, w("Spine")),
                ((0, 1.525, 0.006), 0.082, 0.088, w("Spine", "Head", 0.6))])

    # ---- backpack (its own bone: it shrinks away when the figure sleeps) and the bedroll on top ----
    pack.box((0, 1.250, -0.230), (0.300, 0.400, 0.170), w("Pack"))
    pack.box((0, 1.180, -0.335), (0.200, 0.160, 0.050), w("Pack"))
    bedroll.loft([((-0.165, 1.485, -0.220), 0.0625, 0.0625, w("Pack")),
                  ((0.165, 1.485, -0.220), 0.0625, 0.0625, w("Pack"))], u=Y, v=Z)
    return parts


def write(path, parts):
    out = bytearray()
    out += struct.pack("<4sH", b"TLDF", 1)
    out += struct.pack("<H", len(BONES))
    for name, pos in BONES:
        raw = name.encode("ascii")
        out += struct.pack("<B", len(raw)) + raw + struct.pack("<3f", *pos)
    out += struct.pack("<H", len(parts))
    for p in parts:
        assert len(p.verts) <= 65536 and len(p.idx) % 3 == 0
        out += struct.pack("<BII", p.colour, len(p.verts), len(p.idx))
        for (pos, n, b0, b1, w0) in p.verts:
            out += struct.pack("<3f3fBBf", pos[0], pos[1], pos[2], n[0], n[1], n[2], b0, b1, w0)
        out += struct.pack("<%dH" % len(p.idx), *p.idx)
    with open(path, "wb") as f:
        f.write(out)
    return len(out)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "survivor.bin"
    parts = build()
    size = write(path, parts)
    verts = sum(len(p.verts) for p in parts)
    tris = sum(len(p.idx) // 3 for p in parts)
    for p in parts:
        print("  part colour %2d %-8s  %4d verts  %4d tris" % (p.colour, COLOUR_NAMES[p.colour], len(p.verts), len(p.idx) // 3))
    print("%s: %d bytes, %d bones, %d parts, %d verts, %d tris (placeholder)" % (path, size, len(BONES), len(parts), verts, tris))


if __name__ == "__main__":
    main()
