#!/usr/bin/env python3
"""
gen_survivor.py - builds the low-poly winter survivor for the co-op mod and writes survivor.bin (format "TLDF" v1).

Deterministic: no random numbers, no clock; the same source always writes the same bytes.

Space: Unity (x right, y up, z forward, metres), standing on y = 0, facing +z.
Every triangle (a, b, c) has cross(b-a, c-a) pointing outward.

How it is put together ("ball joint" style, so no pose can open a gap):
  * every piece is its own closed shell; pieces overlap where they meet;
  * shoulder, hip: the limb ends in a ball centred on the joint and hidden in the body;
  * elbow, knee: both halves have the same cross-section at the joint; the upper half ends in the half-ellipsoid that
    this cross-section sweeps when the joint bends about x, so the outside of the bend keeps its full volume at any
    angle (122 degrees for the crouch) and the rest pose shows one continuous limb;
  * two-bone blends: waist of the parka (Hips/Spine), neck (Spine/Head), boot shaft (Ankle/Knee);
  * everything of the pack hangs on the Pack bone, which sits 1.5 cm inside the back: scaled to 0 it is gone.

Colours (one part each): 0 Parka 1 Trousers 2 Hat 3 Cuff(+bobble) 4 Pack(+straps) 5 Mitts 6 Boots 7 Scarf 8 Skin
9 Hair(+brows, eye dots) 10 Bedroll.  Hat, cuff, mitts and scarf can be hidden: head with hair, bare hands, neck
and parka collar are complete underneath.
"""
import math
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "survivor.bin")

# ---------------------------------------------------------------------------------------------
# skeleton
# ---------------------------------------------------------------------------------------------
BONES = [
    ("Hips", (0.0, 0.94, 0.0)),
    ("Spine", (0.0, 1.00, 0.0)),
    ("Head", (0.0, 1.50, 0.01)),
    ("Pack", (0.0, 1.25, -0.145)),
    ("ShoulderL", (-0.235, 1.435, 0.0)),
    ("ElbowL", (-0.235, 1.145, 0.0)),
    ("HandL", (-0.235, 0.845, 0.03)),
    ("ShoulderR", (0.235, 1.435, 0.0)),
    ("ElbowR", (0.235, 1.145, 0.0)),
    ("HandR", (0.235, 0.845, 0.03)),
    ("HipL", (-0.095, 0.92, 0.0)),
    ("KneeL", (-0.095, 0.49, 0.0)),
    ("AnkleL", (-0.095, 0.09, 0.0)),
    ("HipR", (0.095, 0.92, 0.0)),
    ("KneeR", (0.095, 0.49, 0.0)),
    ("AnkleR", (0.095, 0.09, 0.0)),
]
(HIPS, SPINE, HEAD, PACK, SHL, ELL, HAL, SHR, ELR, HAR, HIPL, KNL, ANL, HIPR, KNR, ANR) = range(16)
MIRROR_BONE = np.array([HIPS, SPINE, HEAD, PACK, SHR, ELR, HAR, SHL, ELL, HAL, HIPR, KNR, ANR, HIPL, KNL, ANL])

(PARKA, TROUSERS, HAT, CUFF, PACKC, MITTS, BOOTS, SCARF, SKIN, HAIR, BEDROLL) = range(11)
COLOUR_NAMES = ["Parka", "Trousers", "Hat", "Cuff", "Pack", "Mitts", "Boots", "Scarf", "Skin", "Hair", "Bedroll"]

MAX_VERTS, MAX_TRIS = 6000, 10000


# ---------------------------------------------------------------------------------------------
# small maths
# ---------------------------------------------------------------------------------------------
def smooth(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def spow(v, e):
    return np.sign(v) * np.abs(v) ** e


def unit(v):
    v = np.asarray(v, float)
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.maximum(n, 1e-12)


def lerp(a, b, t):
    return a + (b - a) * t


def table(tab, y):
    """Linear interpolation of the columns 1.. of a table whose column 0 (ascending) is the key."""
    tab = np.asarray(tab, float)
    y = np.asarray(y, float)
    return [np.interp(y, tab[:, 0], tab[:, k]) for k in range(1, tab.shape[1])]


# ---------------------------------------------------------------------------------------------
# skin (which joints a vertex follows)
# ---------------------------------------------------------------------------------------------
def rigid(b):
    def f(P):
        n = len(P)
        return np.full(n, b), np.full(n, b), np.ones(n)
    return f


def blend_y(b_hi, b_lo, y0, y1):
    """Below y0 the joint b_lo, above y1 the joint b_hi, a smooth step in between (by rest height)."""
    def f(P):
        w = smooth((P[:, 1] - y0) / (y1 - y0))
        w = np.round(w, 4)
        b0 = np.where(w > 0, b_hi, b_lo)
        b1 = np.where(w < 1, b_lo, b_hi)
        b1 = np.where(w > 0, b1, b_lo)
        w0 = np.where(w > 0, w, 1.0)
        return b0, b1, w0
    return f


# ---------------------------------------------------------------------------------------------
# shells
# ---------------------------------------------------------------------------------------------
class Shell:
    """One closed surface. Vertices are shared where the shading is smooth and doubled along a hard crease."""

    def __init__(self, name):
        self.name = name
        self.P, self.b0, self.b1, self.w0 = [], [], [], []
        self.T = []
        self.flat_axes = []     # (indices, axis): keep these normals square to the axis (limb ends at a joint)

    def add(self, pts, skin):
        pts = np.atleast_2d(np.asarray(pts, float))
        i0 = len(self.P)
        b0, b1, w0 = skin(pts)
        for k in range(len(pts)):
            self.P.append(pts[k]); self.b0.append(int(b0[k])); self.b1.append(int(b1[k])); self.w0.append(float(w0[k]))
        return np.arange(i0, i0 + len(pts))

    def dup(self, idx):
        i0 = len(self.P)
        for i in idx:
            self.P.append(self.P[i]); self.b0.append(self.b0[i]); self.b1.append(self.b1[i]); self.w0.append(self.w0[i])
        return np.arange(i0, i0 + len(idx))

    def tri(self, a, b, c):
        self.T.append((int(a), int(b), int(c)))

    def band(self, a, b, closed=True):
        n = len(a)
        for i in range(n if closed else n - 1):
            j = (i + 1) % n
            self.tri(a[i], a[j], b[j])
            self.tri(a[i], b[j], b[i])

    def fan(self, ring, c, start):
        n = len(ring)
        for i in range(n):
            j = (i + 1) % n
            if start:
                self.tri(c, ring[j], ring[i])
            else:
                self.tri(c, ring[i], ring[j])

    def poly(self, idx, start):
        """A flat polygon (convex), as a fan from its first corner."""
        for i in range(1, len(idx) - 1):
            if start:
                self.tri(idx[0], idx[i + 1], idx[i])
            else:
                self.tri(idx[0], idx[i], idx[i + 1])

    def finish(self):
        P = np.array(self.P, float)
        T = np.array(self.T, np.int64)
        # no degenerate triangles
        a, b, c = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
        area = np.linalg.norm(np.cross(b - a, c - a), axis=1)
        T = T[area > 1e-10]
        # outward: a closed surface wound outward has a positive volume
        a, b, c = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
        vol = np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6.0
        if vol < 0:
            T = T[:, [0, 2, 1]]
            vol = -vol
        N = vertex_normals(P, T)
        for idx, axis in self.flat_axes:
            ax = unit(np.asarray(axis, float))
            n = N[idx] - np.outer(N[idx] @ ax, ax)
            N[idx] = unit(n)
        opens = open_edges(P, T)
        if opens:
            raise RuntimeError("shell %s is not closed: %d open edges" % (self.name, opens))
        used = np.zeros(len(P), bool); used[T.ravel()] = True
        if not used.all():
            raise RuntimeError("shell %s has unused vertices" % self.name)
        return dict(name=self.name, P=P, N=N, T=T, b0=np.array(self.b0), b1=np.array(self.b1),
                    w0=np.array(self.w0), volume=vol)


def vertex_normals(P, T):
    N = np.zeros_like(P)
    tri = P[T]
    fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    fnu = unit(fn)
    for i in range(3):
        u = unit(tri[:, (i + 1) % 3] - tri[:, i])
        v = unit(tri[:, (i + 2) % 3] - tri[:, i])
        ang = np.arccos(np.clip(np.einsum("ij,ij->i", u, v), -1.0, 1.0))
        np.add.at(N, T[:, i], fnu * ang[:, None])
    return unit(N)


def open_edges(P, T):
    """Edges that are not shared by exactly two triangles running opposite ways (vertices welded by position)."""
    key = np.round(P / 1e-6).astype(np.int64)
    _, wid = np.unique(key, axis=0, return_inverse=True)
    wid = wid.ravel()
    W = wid[T]
    cnt = {}
    for t in W:
        for i in range(3):
            a, b = int(t[i]), int(t[(i + 1) % 3])
            if a == b:
                continue
            cnt[(a, b)] = cnt.get((a, b), 0) + 1
    bad = 0
    for (a, b), n in cnt.items():
        if n != 1 or cnt.get((b, a), 0) != 1:
            bad += 1
    return bad


def mirrored(sd, name=None):
    """The same shell on the other side of the body (x -> -x, left and right joints swapped)."""
    P = sd["P"].copy(); P[:, 0] *= -1
    N = sd["N"].copy(); N[:, 0] *= -1
    return dict(name=name or (sd["name"] + "_L"), P=P, N=N, T=sd["T"][:, [0, 2, 1]].copy(),
                b0=MIRROR_BONE[sd["b0"]], b1=MIRROR_BONE[sd["b1"]], w0=sd["w0"].copy(), volume=sd["volume"])


# ---------------------------------------------------------------------------------------------
# building blocks
# ---------------------------------------------------------------------------------------------
def ring(y, hw, zf, zb=None, n=12, cx=0.0, cz=0.0, p=2.0, dy=None):
    """A superellipse loop lying level at height y: phi = 0 is the front (+z), it runs on towards +x."""
    if zb is None:
        zb = zf
    phi = np.arange(n) * 2.0 * np.pi / n
    c, s = np.cos(phi), np.sin(phi)
    e = 2.0 / p
    x = cx + hw * spow(s, e)
    z = cz + np.where(c >= 0, zf, zb) * spow(c, e)
    yy = np.full(n, float(y)) if dy is None else y + dy(phi)
    return np.stack([x, yy, z], 1)


def ring_x(x, cy, cz, ry, rz, n=10, p=2.0):
    """A loop standing in the plane x = const (for things that lie across the body)."""
    a = np.arange(n) * 2.0 * np.pi / n
    e = 2.0 / p
    return np.stack([np.full(n, float(x)), cy + ry * spow(np.cos(a), e), cz + rz * spow(np.sin(a), e)], 1)


def dome(base, apex, m):
    """Rings of a half-ellipsoid standing on the loop 'base' with its pole at 'apex' (without base and pole)."""
    base = np.asarray(base, float); apex = np.asarray(apex, float)
    c = base.mean(axis=0)
    out = []
    for k in range(1, m):
        a = (k / m) * (np.pi / 2)
        out.append(c + (base - c) * math.cos(a) + (apex - c) * math.sin(a))
    return out


def loft(sh, rings, skin, start=None, end=None, crease=(), loop=False, start_hard=False, end_hard=False):
    """
    Skins a row of loops. start/end: a point = closed with a fan to it; None = left open (only with loop=True, where
    the last loop is joined to the first). crease: loops along which the shading breaks.
    """
    ids = []
    prev = sh.add(rings[0], skin)
    first = prev
    ids.append(prev)
    if start is not None:
        c = sh.add(start, skin)[0]
        sh.fan(sh.dup(prev) if start_hard else prev, c, True)
    if 0 in crease and not loop:
        prev = sh.dup(prev)
    for k in range(1, len(rings)):
        cur = sh.add(rings[k], skin)
        ids.append(cur)
        sh.band(prev, cur)
        if k in crease:
            cur = sh.dup(cur)
        prev = cur
    if loop:
        sh.band(prev, sh.dup(first) if 0 in crease else first)
    elif end is not None:
        c = sh.add(end, skin)[0]
        sh.fan(sh.dup(prev) if end_hard else prev, c, False)
    return ids


def loft_hard(sh, rings, skin, smooth_along=True):
    """Skins a row of small polygons with hard edges between its sides (straps, flat bands); flat end caps."""
    m = len(rings[0])
    for j in range(m):
        a = [sh.add(r[j], skin)[0] for r in rings]
        b = [sh.add(r[(j + 1) % m], skin)[0] for r in rings]
        for k in range(len(rings) - 1):
            if not smooth_along and k > 0:
                a[k] = sh.dup([a[k]])[0]; b[k] = sh.dup([b[k]])[0]
            sh.tri(a[k], b[k], b[k + 1])
            sh.tri(a[k], b[k + 1], a[k + 1])
    s = sh.add(rings[0], skin); sh.poly(s, True)
    e = sh.add(rings[-1], skin); sh.poly(e, False)


def ellipsoid(sh, c, r, skin, nlon=8, nlat=5, p=2.0, rot=None):
    """A closed blob. rot: 3x3 turning it about its centre."""
    c = np.asarray(c, float)
    rings = []
    for k in range(1, nlat):
        a = -np.pi / 2 + np.pi * k / nlat
        rr = math.cos(a)
        rg = ring(r[1] * math.sin(a), r[0] * rr, r[2] * rr, n=nlon, p=p)
        rings.append(rg)
    lo = np.array([0.0, -r[1], 0.0]); hi = np.array([0.0, r[1], 0.0])
    if rot is not None:
        rings = [rg @ np.asarray(rot).T for rg in rings]
        lo = np.asarray(rot) @ lo; hi = np.asarray(rot) @ hi
    loft(sh, [rg + c for rg in rings], skin, start=lo + c, end=hi + c)


def rot_x(deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def rot_z(deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def rot_y(deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def resample(path, n):
    path = np.asarray(path, float)
    d = np.linalg.norm(np.diff(path, axis=0), axis=1)
    s = np.concatenate([[0], np.cumsum(d)])
    t = np.linspace(0, s[-1], n)
    return np.stack([np.interp(t, s, path[:, k]) for k in range(3)], 1)


def relax(path, it):
    path = path.copy()
    for _ in range(it):
        path[1:-1] = 0.25 * path[:-2] + 0.5 * path[1:-1] + 0.25 * path[2:]
    return path


def tangents(path):
    t = np.zeros_like(path)
    t[1:-1] = path[2:] - path[:-2]
    t[0] = path[1] - path[0]
    t[-1] = path[-1] - path[-2]
    return unit(t)


def tube(sh, path, radius, skin, n=6, up=(0, 1, 0), squash=1.0, cap=0.6):
    """A round (or squashed) hose along a path, with rounded ends."""
    path = np.asarray(path, float)
    T = tangents(path)
    radius = np.broadcast_to(np.asarray(radius, float), (len(path),))
    rings = []
    upv = np.asarray(up, float)
    for k in range(len(path)):
        b = unit(np.cross(T[k], upv))
        nn = unit(np.cross(b, T[k]))
        a = np.arange(n) * 2 * np.pi / n
        rings.append(path[k] + radius[k] * (np.outer(np.cos(a), nn) * squash + np.outer(np.sin(a), b)))
    s_apex = path[0] - T[0] * radius[0] * cap
    e_apex = path[-1] + T[-1] * radius[-1] * cap
    d0 = dome(rings[0], s_apex, 2)
    d1 = dome(rings[-1], e_apex, 2)
    loft(sh, d0[::-1] + rings + d1, skin, start=s_apex, end=e_apex)


# ---------------------------------------------------------------------------------------------
# the body under the clothes: measures used by more than one piece
# ---------------------------------------------------------------------------------------------
#          y      half   front  back   super-
#                 width  depth  depth  ellipse
TORSO = np.array([
    [0.855, 0.207, 0.147, 0.157, 2.7],     # hem: clearly wider than the thighs, so that a raised thigh
    [0.875, 0.209, 0.150, 0.160, 2.7],     # comes out of the front and does not graze the sides
    [0.930, 0.203, 0.148, 0.158, 2.7],
    [1.000, 0.190, 0.143, 0.155, 2.6],     # waist
    [1.080, 0.183, 0.141, 0.156, 2.6],
    [1.180, 0.186, 0.145, 0.160, 2.5],
    [1.280, 0.195, 0.149, 0.163, 2.5],     # chest
    [1.360, 0.206, 0.146, 0.160, 2.4],
    [1.420, 0.216, 0.134, 0.152, 2.4],
    [1.452, 0.220, 0.119, 0.140, 2.3],     # shoulder line
    [1.470, 0.207, 0.104, 0.125, 2.3],
    [1.489, 0.160, 0.086, 0.106, 2.3],
    [1.503, 0.115, 0.068, 0.086, 2.2],
    [1.512, 0.084, 0.058, 0.074, 2.2],     # under the collar
])
TORSO_N = 16
WAIST_LO, WAIST_HI = 0.93, 1.08            # the parka follows the hips below, the spine above


def torso_point(phi, y, off=0.0):
    hw, zf, zb, p = table(TORSO, y)
    c, s = np.cos(phi), np.sin(phi)
    e = 2.0 / p
    return np.stack([(hw + off) * spow(s, e), np.broadcast_to(y, np.shape(phi)).astype(float),
                     (np.where(c >= 0, zf, zb) + off) * spow(c, e)], -1)


def torso_F(q):
    q = np.asarray(q, float)
    hw, zf, zb, p = table(TORSO, q[..., 1])
    zd = np.where(q[..., 2] >= 0, zf, zb)
    return (np.abs(q[..., 0]) / hw) ** p + (np.abs(q[..., 2]) / zd) ** p


def torso_normal(q):
    q = np.asarray(q, float)
    h = 1e-4
    g = np.stack([(torso_F(q + d) - torso_F(q - d)) / (2 * h)
                  for d in (np.array([h, 0, 0]), np.array([0, h, 0]), np.array([0, 0, h]))], -1)
    return unit(g)


def torso_z(x, y, front=True):
    hw, zf, zb, p = table(TORSO, y)
    t = np.clip(np.abs(x) / hw, 0, 1)
    return (zf if front else -zb) * (1 - t ** p) ** (1.0 / p)


def torso_y(x, z):
    """Height of the shoulder top above the point (x, z): the y where the surface passes through it."""
    lo, hi = 1.40, 1.512
    for _ in range(40):
        mid = 0.5 * (lo + hi)
        if torso_F(np.array([x, mid, z])) < 1.0:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# head: y, half width, front depth, back depth (about the axis z = HCZ)
HCZ = 0.012
HEAD_P = 2.2
HEAD_N = 16
HEADT = np.array([
    [1.468, 0.050, 0.050, 0.053],    # neck, inside the collar
    [1.490, 0.051, 0.051, 0.055],
    [1.505, 0.052, 0.053, 0.057],
    [1.518, 0.054, 0.058, 0.060],
    [1.527, 0.058, 0.082, 0.063],    # under the jaw
    [1.540, 0.066, 0.097, 0.075],    # chin
    [1.570, 0.080, 0.103, 0.092],    # mouth
    [1.605, 0.0885, 0.104, 0.100],   # cheeks, nose base
    [1.638, 0.0895, 0.100, 0.104],   # eyes (set back)
    [1.662, 0.0895, 0.107, 0.105],   # brow
    [1.700, 0.0875, 0.104, 0.104],   # forehead
    [1.735, 0.078, 0.091, 0.096],
    [1.760, 0.054, 0.062, 0.068],
])
HEAD_TOP = (0.0, 1.771, HCZ - 0.004)


def head_loop(yfun, off, n=HEAD_N):
    """A loop round the head at the heights yfun(phi), 'off' metres outside the skin."""
    phi = np.arange(n) * 2.0 * np.pi / n
    y = np.broadcast_to(np.asarray(yfun(phi) if callable(yfun) else yfun, float), (n,))
    hw, zf, zb = table(HEADT, y)
    c, s = np.cos(phi), np.sin(phi)
    e = 2.0 / HEAD_P
    return np.stack([(hw + off) * spow(s, e), y, HCZ + (np.where(c >= 0, zf, zb) + off) * spow(c, e)], 1)


def head_front(x, y):
    hw, zf, zb = table(HEADT, y)
    t = np.clip(abs(x) / hw, 0, 1)
    return HCZ + zf * (1 - t ** HEAD_P) ** (1.0 / HEAD_P)


def head_normal(q):
    q = np.asarray(q, float)
    def F(q):
        hw, zf, zb = table(HEADT, q[1])
        zd = zf if q[2] >= HCZ else zb
        return (abs(q[0]) / hw) ** HEAD_P + (abs(q[2] - HCZ) / zd) ** HEAD_P
    h = 1e-4
    g = np.array([(F(q + d) - F(q - d)) / (2 * h) for d in np.eye(3) * h])
    return unit(g)


# the lower edge of the toque round the head, and the hairline (always above it, so the hat covers the hair)
def hat_low(phi):
    return 1.6245 + 0.036 * np.cos(phi) + 0.0095 * np.cos(2 * phi)     # front 1.670, sides 1.615, back 1.598


def hair_low(phi):
    return hat_low(phi) + 0.009 + 0.028 * np.maximum(0.0, np.cos(phi)) ** 2   # front 1.707, back 1.607


# ---------------------------------------------------------------------------------------------
# the pieces
# ---------------------------------------------------------------------------------------------
def build():
    parts = {c: [] for c in range(len(COLOUR_NAMES))}

    def put(colour, sh, mirror=False):
        sd = sh.finish() if isinstance(sh, Shell) else sh
        parts[colour].append(sd)
        if mirror:
            parts[colour].append(mirrored(sd))
        return sd

    # =========================================================================================
    # 0 PARKA
    # =========================================================================================
    # ---- body: hem to collar; waist blended between hips and spine ----
    sh = Shell("parka_body")
    sk = blend_y(SPINE, HIPS, WAIST_LO, WAIST_HI)
    ys = [0.855, 0.875, 0.93, 0.965, 1.00, 1.04, 1.08, 1.18, 1.28, 1.36, 1.42, 1.452, 1.470, 1.489, 1.503, 1.512]
    rings = []
    for y in ys:
        hw, zf, zb, p = table(TORSO, y)
        rings.append(ring(y, hw, zf, zb, n=TORSO_N, p=float(p)))
    # underneath: the hem turns in and closes inside the trousers' seat
    under = [ring(0.925, 0.160, 0.108, 0.118, n=TORSO_N, p=2.6),
             ring(0.868, 0.182, 0.127, 0.135, n=TORSO_N, p=2.7),
             ring(0.857, 0.192, 0.137, 0.145, n=TORSO_N, p=2.7)]
    loft(sh, under + rings, sk, start=(0, 0.935, 0), end=(0, 1.515, -0.006), crease=(3,))
    put(PARKA, sh)

    # ---- stand-up collar: what shows round the neck when no scarf is worn ----
    sh = Shell("parka_collar")
    sk = rigid(SPINE)
    cz = 0.004
    top = lambda phi: -0.011 * np.cos(phi)            # lower under the chin, higher at the nape
    loops = [ring(1.478, 0.060, 0.058, 0.062, n=14, cz=cz),                    # inner foot, inside the body
             ring(1.474, 0.092, 0.084, 0.094, n=14, cz=cz),                    # outer foot, inside the body
             ring(1.505, 0.086, 0.081, 0.089, n=14, cz=cz),
             ring(1.531, 0.081, 0.079, 0.085, n=14, cz=cz, dy=top),            # outer top edge
             ring(1.531, 0.069, 0.068, 0.073, n=14, cz=cz, dy=top),            # inner top edge
             ring(1.500, 0.062, 0.060, 0.065, n=14, cz=cz)]
    loft(sh, loops, sk, loop=True, crease=(3, 4))
    put(PARKA, sh)

    # ---- front placket (over the zip) and two pocket flaps: they only show as shading, in the parka's colour ----
    sk = blend_y(SPINE, HIPS, WAIST_LO, WAIST_HI)
    sh = Shell("parka_placket")
    polys = []
    for y in (0.862, 0.93, 1.00, 1.08, 1.18, 1.28, 1.36, 1.42, 1.452, 1.470, 1.487):
        c = np.array([0.0, y, torso_z(0.0, y)])
        nrm = torso_normal(c + np.array([0, 0, 1e-4]))
        polys.append(np.array([c - (0.019, 0, 0) - nrm * 0.010, c + (0.019, 0, 0) - nrm * 0.010,
                               c + (0.019, 0, 0) + nrm * 0.0065, c - (0.019, 0, 0) + nrm * 0.0065]))
    loft_hard(sh, polys, sk)
    put(PARKA, sh)

    sh = Shell("parka_flap_R")
    polys = []
    for x in (0.058, 0.090, 0.122, 0.150):
        yt, yb = 1.028 - 0.10 * (x - 0.058), 0.978 - 0.10 * (x - 0.058)
        ct = np.array([x, yt, torso_z(x, yt)]); cb = np.array([x, yb, torso_z(x, yb)])
        nt, nb = torso_normal(ct), torso_normal(cb)
        polys.append(np.array([cb - nb * 0.010, ct - nt * 0.010, ct + nt * 0.0055, cb + nb * 0.0085]))
    loft_hard(sh, polys, sk)
    put(PARKA, sh, mirror=True)

    # ---- hood, lying on the upper back, and its rolled rim round the nape ----
    sh = Shell("parka_hood")
    hood = [ring(1.385, 0.075, 0.022, n=12, cz=-0.146, p=2.4),
            ring(1.410, 0.118, 0.040, n=12, cz=-0.146, p=2.4),
            ring(1.455, 0.146, 0.056, n=12, cz=-0.138, p=2.5),
            ring(1.493, 0.150, 0.060, n=12, cz=-0.126, p=2.5),
            ring(1.520, 0.128, 0.050, n=12, cz=-0.114, p=2.4)]
    loft(sh, hood, rigid(SPINE), start=(0, 1.374, -0.146), end=(0, 1.531, -0.110))
    put(PARKA, sh)

    sh = Shell("parka_hood_rim")
    th = np.radians(np.linspace(-152, 152, 17))
    path = np.stack([0.126 * np.sin(th), 1.512 - 0.0165 * (1 - np.cos(th)) ** 1.2, -0.004 - 0.124 * np.cos(th)], 1)
    rad = 0.026 - 0.013 * (np.abs(th) / th.max()) ** 2.2
    tube(sh, path, rad, rigid(SPINE), n=6, cap=0.8)
    put(PARKA, sh)

    # ---- sleeves (right; the left is the mirror image) ----
    SX = 0.235
    EL_R = (0.059, 0.063)                               # the sleeve's half widths at the elbow, both halves
    sh = Shell("sleeve_upper_R")
    sk = rigid(SHR)
    r0 = ring(1.435, 0.064, 0.073, n=10, cx=SX)
    top_apex = np.array([SX, 1.435 + 0.039, 0.0])
    up = [r0, ring(1.36, 0.067, 0.074, n=10, cx=SX), ring(1.25, 0.063, 0.068, n=10, cx=SX),
          ring(1.185, 0.060, 0.064, n=10, cx=SX), ring(1.145, EL_R[0], EL_R[1], n=10, cx=SX)]
    bot_apex = np.array([SX, 1.145 - EL_R[1], 0.0])
    rr = dome(r0, top_apex, 3)[::-1] + up + dome(up[-1], bot_apex, 3)
    ids = loft(sh, rr, sk, start=top_apex, end=bot_apex)
    sh.flat_axes.append((ids[2 + len(up) - 1], (0, 1, 0)))
    put(PARKA, sh, mirror=True)

    sh = Shell("sleeve_lower_R")
    sk = rigid(ELR)
    e0 = ring(1.145, EL_R[0], EL_R[1], n=10, cx=SX)
    in_apex = np.array([SX, 1.145 + 0.034, 0.0])
    lo = [e0, ring(1.10, 0.059, 0.063, n=10, cx=SX, cz=0.001), ring(1.02, 0.056, 0.059, n=10, cx=SX, cz=0.005),
          ring(0.95, 0.052, 0.055, n=10, cx=SX, cz=0.010), ring(0.915, 0.049, 0.052, n=10, cx=SX, cz=0.012),
          ring(0.893, 0.053, 0.056, n=10, cx=SX, cz=0.013),                     # the cuff's edge
          ring(0.895, 0.044, 0.047, n=10, cx=SX, cz=0.013),                     # ... turned in
          ring(0.935, 0.040, 0.043, n=10, cx=SX, cz=0.012)]
    rr = dome(e0, in_apex, 2)[::-1] + lo
    ids = loft(sh, rr, sk, start=in_apex, end=(SX, 0.94, 0.012), crease=(1 + 5, 1 + 6))
    sh.flat_axes.append((ids[1], (0, 1, 0)))
    put(PARKA, sh, mirror=True)

    # =========================================================================================
    # 1 TROUSERS
    # =========================================================================================
    sh = Shell("trousers_seat")
    seat = [ring(0.822, 0.105, 0.072, 0.082, n=14, p=2.3),
            ring(0.850, 0.162, 0.106, 0.120, n=14, p=2.4),
            ring(0.890, 0.185, 0.124, 0.138, n=14, p=2.4),
            ring(0.930, 0.188, 0.127, 0.141, n=14, p=2.4),
            ring(0.980, 0.180, 0.122, 0.136, n=14, p=2.4),
            ring(1.020, 0.150, 0.100, 0.112, n=14, p=2.3)]
    loft(sh, seat, rigid(HIPS), start=(0, 0.806, -0.004), end=(0, 1.042, -0.004))
    put(TROUSERS, sh)

    LX = 0.095
    KN_R = (0.080, 0.085)
    sh = Shell("thigh_R")
    sk = rigid(HIPR)
    t0 = ring(0.92, 0.092, 0.104, n=12, cx=LX)
    top_apex = np.array([LX, 0.92 + 0.090, 0.0])
    th_r = [t0, ring(0.80, 0.093, 0.104, n=12, cx=LX, cz=0.002), ring(0.68, 0.090, 0.098, n=12, cx=LX, cz=0.004),
            ring(0.57, 0.084, 0.090, n=12, cx=LX, cz=0.004), ring(0.525, 0.081, 0.086, n=12, cx=LX, cz=0.003),
            ring(0.49, KN_R[0], KN_R[1], n=12, cx=LX, cz=0.002)]
    bot_apex = np.array([LX, 0.49 - KN_R[1], 0.002])
    rr = dome(t0, top_apex, 3)[::-1] + th_r + dome(th_r[-1], bot_apex, 3)
    ids = loft(sh, rr, sk, start=top_apex, end=bot_apex)
    sh.flat_axes.append((ids[2 + len(th_r) - 1], (0, 1, 0)))
    put(TROUSERS, sh, mirror=True)

    sh = Shell("shin_R")
    sk = rigid(KNR)
    k0 = ring(0.49, KN_R[0], KN_R[1], n=12, cx=LX, cz=0.002)
    in_apex = np.array([LX, 0.49 + 0.040, 0.002])
    sn = [k0, ring(0.44, 0.080, 0.086, n=12, cx=LX, cz=0.000), ring(0.36, 0.078, 0.087, n=12, cx=LX, cz=-0.004),
          ring(0.28, 0.074, 0.080, n=12, cx=LX, cz=-0.003), ring(0.236, 0.075, 0.080, n=12, cx=LX, cz=-0.001),
          ring(0.206, 0.069, 0.073, n=12, cx=LX), ring(0.180, 0.052, 0.054, n=12, cx=LX),
          ring(0.158, 0.038, 0.040, n=12, cx=LX)]
    rr = dome(k0, in_apex, 2)[::-1] + sn
    ids = loft(sh, rr, sk, start=in_apex, end=(LX, 0.147, 0.0))
    sh.flat_axes.append((ids[1], (0, 1, 0)))
    put(TROUSERS, sh, mirror=True)

    # =========================================================================================
    # 6 BOOTS (foot on the ankle, the shaft's top on the shin, blended in between)
    # =========================================================================================
    sh = Shell("boot_R")
    sk = blend_y(KNR, ANR, 0.108, 0.180)

    def boot_ring(y, hw, zb, zf, p=2.3, dy=None):
        cz = 0.5 * (zf + zb); hl = 0.5 * (zf - zb)
        r = ring(y, hw, hl, n=12, cx=LX, cz=cz, p=p, dy=dy)
        # a boot is narrower at the heel than at the ball of the foot
        t = np.clip((r[:, 2] - zb) / (zf - zb), 0, 1)
        r[:, 0] = LX + (r[:, 0] - LX) * (0.86 + 0.14 * smooth(t * 2.2))
        return r
    # the shaft is cut lower in front: when the shin leans far forward (crouch) its front edge then comes down
    # onto the toe cap instead of standing off above it
    cut = lambda phi: -0.009 * np.cos(phi)
    bt = [boot_ring(0.000, 0.056, -0.076, 0.186),
          boot_ring(0.016, 0.064, -0.084, 0.197),      # edge of the sole
          boot_ring(0.040, 0.064, -0.084, 0.197),
          boot_ring(0.072, 0.062, -0.082, 0.186),      # top of the toe cap (a high, round one)
          boot_ring(0.098, 0.062, -0.081, 0.128),
          boot_ring(0.120, 0.064, -0.080, 0.086, p=2.1),     # instep
          boot_ring(0.148, 0.069, -0.078, 0.076, p=2.0),
          boot_ring(0.176, 0.075, -0.079, 0.077, p=2.0),
          boot_ring(0.200, 0.080, -0.082, 0.082, p=2.0, dy=cut),   # top of the shaft
          boot_ring(0.202, 0.061, -0.063, 0.063, p=2.0, dy=cut),   # ... turned in, inside the trouser leg
          boot_ring(0.172, 0.048, -0.050, 0.050, p=2.0)]
    loft(sh, bt, sk, start=(LX, 0.0, 0.05), end=(LX, 0.166, 0.0), start_hard=True, crease=(1, 8))
    put(BOOTS, sh, mirror=True)

    # =========================================================================================
    # 8 SKIN: head with neck, nose, ears; bare hands
    # =========================================================================================
    sh = Shell("head")
    sk = blend_y(HEAD, SPINE, 1.478, 1.526)
    hr = [ring(r[0], r[1], r[2], r[3], n=HEAD_N, cz=HCZ, p=HEAD_P) for r in HEADT]
    loft(sh, hr, sk, start=(0, 1.462, HCZ), end=HEAD_TOP)
    put(SKIN, sh)

    # nose: a small wedge, its back buried in the face
    sh = Shell("nose")
    sk = rigid(HEAD)
    zb_ = head_front(0, 1.652) - 0.004
    zt_ = head_front(0, 1.604)
    bridge = (0.0, 1.652, zb_ + 0.006)
    tip = (0.0, 1.606, zt_ + 0.025)
    under = (0.0, 1.594, zt_ + 0.004)
    wl = (-0.020, 1.599, zt_ - 0.002)
    wr = (0.020, 1.599, zt_ - 0.002)
    back = (0.0, 1.62, zt_ - 0.02)
    v = sh.add([bridge, tip, under, wl, wr, back], sk)
    B, Tp, U, WL, WR, BK = v
    for t in ((B, WL, Tp), (B, Tp, WR), (Tp, WL, U), (Tp, U, WR), (B, BK, WL), (B, WR, BK), (U, WL, BK), (U, BK, WR)):
        sh.tri(*t)
    put(SKIN, sh)

    # ears: flat, close to the head (so the toque's edge covers what lies under it)
    sh = Shell("ear_R")
    hw_e = float(table(HEADT, 1.632)[0])
    ellipsoid(sh, (hw_e - 0.001, 1.632, HCZ - 0.006), (0.0125, 0.029, 0.016), rigid(HEAD), nlon=6, nlat=4,
              rot=rot_y(-18) @ rot_x(-10))
    put(SKIN, sh, mirror=True)

    # bare hand: mitten-shaped with a thumb bump; the mitt below is this shape, let out all round
    #         y      half      front   back    centre z
    #                thickness
    HAND = np.array([
        [0.757, 0.0100, 0.019, 0.016, 0.034],
        [0.768, 0.0135, 0.028, 0.023, 0.034],
        [0.785, 0.0150, 0.032, 0.026, 0.033],
        [0.815, 0.0170, 0.037, 0.030, 0.031],
        [0.850, 0.0180, 0.038, 0.031, 0.028],
        [0.880, 0.0170, 0.031, 0.027, 0.022],
        [0.900, 0.0165, 0.024, 0.023, 0.017],     # wrist
        [0.940, 0.0170, 0.026, 0.025, 0.013],     # inside the sleeve
    ])

    def hand_rings(dx, dz, n, rows=HAND):
        return [ring(r[0], r[1] + dx, r[2] + dz, r[3] + dz, n=n, cx=SX, cz=r[4], p=2.0) for r in rows]

    def thumb_path(k):
        return np.array([[SX, 0.872, 0.040], [SX, 0.850, 0.066 + k * 0.3], [SX, 0.822, 0.079 + k]])

    sh = Shell("hand_R")
    sk = rigid(ELR)
    loft(sh, hand_rings(0, 0, 8), sk, start=(SX, 0.751, 0.034), end=(SX, 0.946, 0.013))
    put(SKIN, sh, mirror=True)
    sh = Shell("thumb_R")
    tube(sh, thumb_path(0.0), [0.011, 0.0105, 0.009], sk, n=6, up=(1, 0, 0), cap=0.8)
    put(SKIN, sh, mirror=True)

    # =========================================================================================
    # 5 MITTS (cover the bare hands completely)
    # =========================================================================================
    sh = Shell("mitt_R")
    MITT = HAND.copy()
    MITT[0] = [0.746, 0.0010, 0.012, 0.009, 0.034]  # the tip: two loops below the hand's, then the pole
    MITT[1] = [0.760, 0.0090, 0.026, 0.021, 0.034]
    mr = hand_rings(0.0165, 0.015, 10, MITT)
    # a gauntlet ridge at the wrist, just below the sleeve
    mr.insert(6, ring(0.888, 0.038, 0.054, 0.048, n=10, cx=SX, cz=0.021))
    mr[7] = ring(0.905, 0.034, 0.041, 0.039, n=10, cx=SX, cz=0.016)
    mr[8] = ring(0.946, 0.031, 0.037, 0.036, n=10, cx=SX, cz=0.013)
    loft(sh, mr, sk, start=(SX, 0.7375, 0.034), end=(SX, 0.954, 0.013))
    put(MITTS, sh, mirror=True)
    sh = Shell("mitt_thumb_R")
    tube(sh, thumb_path(0.007), [0.023, 0.022, 0.019], sk, n=6, up=(1, 0, 0), cap=0.8)
    put(MITTS, sh, mirror=True)

    # =========================================================================================
    # 9 HAIR: short hair cap, brows, eye dots
    # =========================================================================================
    sh = Shell("hair")
    sk = rigid(HEAD)
    hl = [head_loop(lambda f: hair_low(f) + 0.012, -0.008),           # tucked into the head
          head_loop(hair_low, 0.0065),                                  # the hairline
          head_loop(lambda f: lerp(hair_low(f), 1.722, 0.5), 0.0085),
          head_loop(1.722, 0.0090),
          ring(1.748, 0.0755, 0.0860, 0.0915, n=HEAD_N, cz=HCZ, p=HEAD_P),
          ring(1.770, 0.0500, 0.0570, 0.0650, n=HEAD_N, cz=HCZ, p=HEAD_P)]
    loft(sh, hl, sk, start=(0, 1.69, HCZ), end=(0, 1.781, HCZ - 0.004), crease=(1,))
    put(HAIR, sh)

    for side in (1, -1):
        # eye dot: a small dark lens on the face
        sh = Shell("eye")
        c0 = np.array([side * 0.0360, 1.636, 0.0])
        c0[2] = head_front(c0[0], c0[1])
        nrm = head_normal(c0 + np.array([0, 0, 0.0]))
        nrm = unit(nrm * np.array([1, 0.35, 1]))
        bx = unit(np.cross([0, 1, 0], nrm)); by = unit(np.cross(nrm, bx))
        a = np.arange(8) * 2 * np.pi / 8
        rim = c0 + nrm * 0.0005 + np.outer(np.cos(a), bx) * 0.0098 + np.outer(np.sin(a), by) * 0.0128
        loft(sh, [rim], sk, start=c0 - nrm * 0.012, end=c0 + nrm * 0.0050, start_hard=True)
        put(HAIR, sh)

        # brow: a short bar on the brow ridge
        sh = Shell("brow")
        xs = side * np.array([0.015, 0.030, 0.045, 0.059])
        ysb = np.array([1.6625, 1.6655, 1.6650, 1.6600])
        polys = []
        for x, y in zip(xs, ysb):
            c = np.array([x, y, head_front(x, y)])
            nrm = head_normal(c)
            nrm = unit(nrm * np.array([1, 0.3, 1]))
            upv = unit(np.cross(nrm, [side, 0, 0]) * side)
            if upv[1] < 0:
                upv = -upv
            h = 0.0048
            polys.append(np.array([c - upv * h - nrm * 0.006, c + upv * h - nrm * 0.006,
                                   c + upv * h + nrm * 0.0045, c - upv * h + nrm * 0.0045]))
        loft_hard(sh, polys, sk)
        put(HAIR, sh)

    # =========================================================================================
    # 2 HAT (toque) and 3 CUFF (its turned-up edge, the bobble)
    # =========================================================================================
    sh = Shell("toque")
    sk = rigid(HEAD)
    tq = [head_loop(lambda f: hat_low(f) + 0.022, 0.006),               # under the cuff
          head_loop(lambda f: hat_low(f) + 0.054, 0.0155),              # just above the cuff
          head_loop(lambda f: lerp(hat_low(f) + 0.054, 1.742, 0.55), 0.0160),
          ring(1.744, 0.0930, 0.1010, 0.1140, n=HEAD_N, cz=HCZ, p=HEAD_P),
          ring(1.774, 0.0710, 0.0720, 0.0990, n=HEAD_N, cz=HCZ, p=HEAD_P),
          ring(1.791, 0.0390, 0.0330, 0.0700, n=HEAD_N, cz=HCZ, p=HEAD_P)]
    loft(sh, tq, sk, start=(0, 1.70, HCZ), end=(0, 1.797, HCZ - 0.022))
    put(HAT, sh)

    sh = Shell("toque_cuff")
    cf = [head_loop(lambda f: hat_low(f) + 0.005, -0.008),              # inner lower edge, inside the head
          head_loop(lambda f: hat_low(f) + 0.000, 0.0185),              # outer lower edge
          head_loop(lambda f: hat_low(f) + 0.024, 0.0230),
          head_loop(lambda f: hat_low(f) + 0.050, 0.0205),              # outer upper edge
          head_loop(lambda f: hat_low(f) + 0.046, 0.0060)]              # inner upper edge, under the toque
    loft(sh, cf, sk, loop=True, crease=(1, 3))
    put(CUFF, sh)

    sh = Shell("bobble")
    bc = np.array([0.0, 1.814, HCZ - 0.038])
    # a slightly lumpy ball: 8 x 5, every other point pushed out a little
    rings_b = []
    for k in range(1, 5):
        a = -np.pi / 2 + np.pi * k / 5
        rg = ring(0.0, math.cos(a), math.cos(a), n=8)
        rg[:, 1] = math.sin(a)
        bump = 1.0 + 0.07 * np.cos(np.arange(8) * np.pi + k * np.pi)
        rings_b.append(bc + rg * (0.033 * bump)[:, None])
    loft(sh, rings_b, sk, start=bc - (0, 0.032, 0), end=bc + (0, 0.032, 0))
    put(CUFF, sh)

    # =========================================================================================
    # 7 SCARF: a thick wrap round the neck, a knot and one end hanging on the chest
    # =========================================================================================
    sh = Shell("scarf")
    sk = rigid(SPINE)
    cz = 0.004
    tl = lambda phi: -0.010 * np.cos(phi)             # lower under the chin
    lump = lambda phi: 0.004 * np.cos(3 * phi + 0.6)  # a wrap is never quite even
    sc = [ring(1.470, 0.060, 0.058, 0.062, n=14, cz=cz),
          ring(1.466, 0.100, 0.095, 0.100, n=14, cz=cz),                      # inside the shoulders
          ring(1.494, 0.113, 0.108, 0.112, n=14, cz=cz, dy=lump),
          ring(1.524, 0.111, 0.107, 0.111, n=14, cz=cz, dy=lambda f: lump(f) * 0.5 + tl(f) * 0.4),
          ring(1.550, 0.096, 0.094, 0.099, n=14, cz=cz, dy=tl),              # top edge
          ring(1.549, 0.070, 0.069, 0.074, n=14, cz=cz, dy=tl),
          ring(1.518, 0.056, 0.055, 0.060, n=14, cz=cz)]
    loft(sh, sc, sk, start=(0, 1.476, cz), end=(0, 1.512, cz))
    put(SCARF, sh)

    sh = Shell("scarf_knot")
    ellipsoid(sh, (-0.052, 1.487, 0.094), (0.036, 0.031, 0.027), sk, nlon=8, nlat=4, rot=rot_z(-20))
    put(SCARF, sh)

    sh = Shell("scarf_end")
    polys = []
    for t in np.linspace(0, 1, 5):
        y = lerp(1.488, 1.365, t)
        x = lerp(-0.056, -0.076, t)
        hwid = lerp(0.031, 0.041, t)
        c = np.array([x, y, torso_z(x, y)])
        nrm = unit(np.array([0.0, 0.0, 1.0]) * 0.6 + torso_normal(c) * 0.4)
        bx = unit(np.cross([0, 1, 0], nrm))
        out = 0.019 - 0.004 * t
        polys.append(np.array([c - bx * hwid - nrm * 0.012, c + bx * hwid - nrm * 0.012,
                               c + bx * hwid + nrm * out, c - bx * hwid + nrm * out]))
    loft_hard(sh, polys[::-1], sk)
    put(SCARF, sh)

    # =========================================================================================
    # 4 PACK: body, lid, outer pocket, shoulder straps, the two straps that hold the bedroll
    # =========================================================================================
    sk = rigid(PACK)
    PZ0, PZ1 = -0.105, -0.302                         # front (inside the back) and back of the pack
    pcz, phd = 0.5 * (PZ0 + PZ1), 0.5 * (PZ0 - PZ1)
    sh = Shell("pack_body")
    pb = [ring(1.072, 0.128, phd - 0.018, n=16, cz=pcz, p=4.0),
          ring(1.090, 0.150, phd - 0.002, n=16, cz=pcz, p=4.5),
          ring(1.140, 0.155, phd + 0.004, n=16, cz=pcz - 0.004, p=4.5),
          ring(1.300, 0.150, phd, n=16, cz=pcz, p=4.5),
          ring(1.385, 0.146, phd - 0.006, n=16, cz=pcz + 0.006, p=4.5),
          ring(1.413, 0.132, phd - 0.020, n=16, cz=pcz + 0.012, p=4.0)]
    loft(sh, pb, sk, start=(0, 1.066, pcz), end=(0, 1.419, pcz + 0.012))
    put(PACKC, sh)

    sh = Shell("pack_lid")
    ld = [ring(1.338, 0.140, phd - 0.012, n=16, cz=pcz + 0.004, p=4.5),     # tucked into the body
          ring(1.332, 0.158, phd + 0.006, n=16, cz=pcz + 0.001, p=4.5),     # the lid's lower edge
          ring(1.386, 0.156, phd + 0.004, n=16, cz=pcz + 0.004, p=4.5),
          ring(1.419, 0.144, phd - 0.008, n=16, cz=pcz + 0.010, p=4.0),
          ring(1.430, 0.110, phd - 0.040, n=16, cz=pcz + 0.012, p=3.0)]
    loft(sh, ld, sk, start=(0, 1.346, pcz), end=(0, 1.434, pcz + 0.012), crease=(1,))
    put(PACKC, sh)

    sh = Shell("pack_pocket")
    pk = [ring(1.098, 0.085, 0.030, n=12, cz=PZ1 - 0.012, p=3.5),
          ring(1.108, 0.098, 0.040, n=12, cz=PZ1 - 0.012, p=4.0),
          ring(1.240, 0.096, 0.038, n=12, cz=PZ1 - 0.010, p=4.0),
          ring(1.268, 0.090, 0.030, n=12, cz=PZ1 - 0.004, p=3.5)]
    loft(sh, pk, sk, start=(0, 1.094, PZ1 - 0.012), end=(0, 1.272, PZ1 - 0.004), crease=(2,))
    put(PACKC, sh)

    # shoulder strap (right): up the back, over the shoulder, down the chest and round the side to the pack
    pts = []
    for y in np.linspace(1.340, 1.445, 6):                                   # up the back
        x = lerp(0.116, 0.150, smooth((y - 1.340) / 0.105))
        pts.append([x, y, torso_z(x, y, front=False)])
    zb0, zf0 = torso_z(0.150, 1.445, front=False), torso_z(0.150, 1.445, front=True)
    for z in np.linspace(zb0, zf0, 12)[1:-1]:                                # over the shoulder
        pts.append([0.150, torso_y(0.150, z), z])
    for y in np.linspace(1.445, 1.215, 10):                                  # down the chest
        t = (1.445 - y) / (1.445 - 1.215)
        x = 0.150 - 0.026 * math.sin(min(t, 0.62) / 0.62 * math.pi) * (1 - 0.2 * t) + 0.012 * smooth((t - 0.6) / 0.4)
        pts.append([x, y, torso_z(x, y)])
    xe, ye = pts[-1][0], pts[-1][1]
    hw, zf, zb, p = table(TORSO, ye)
    phi0 = math.atan2((xe / hw) ** (p / 2), (pts[-1][2] / zf) ** (p / 2))
    for t in np.linspace(0, 1, 11)[1:]:                                      # round the side, under the arm
        phi = lerp(phi0, math.radians(152), t)
        y = lerp(ye, 1.118, smooth(t * 1.15))
        pts.append(torso_point(np.array(phi), y))
    path = relax(resample(np.array(pts, float), 32), 2)
    Tn = tangents(path)
    polys = []
    for k in range(len(path)):
        nrm = torso_normal(path[k])
        bx = unit(np.cross(Tn[k], nrm))
        nrm = unit(np.cross(bx, Tn[k]))
        hwid, t_in, t_out = 0.021, 0.010, 0.0095
        c = path[k]
        polys.append(np.array([c - bx * hwid - nrm * t_in, c + bx * hwid - nrm * t_in,
                               c + bx * hwid + nrm * t_out, c - bx * hwid + nrm * t_out]))
    sh = Shell("strap_R")
    loft_hard(sh, polys, sk)
    put(PACKC, sh, mirror=True)

    # the bedroll and its two straps
    BR_C = (1.476, -0.256); BR_R = 0.060; BR_H = 0.186
    for side in (1, -1):
        sh = Shell("roll_strap")
        x0 = side * 0.092
        rr = [ring_x(x0 - 0.014, BR_C[0], BR_C[1], BR_R + 0.0065, BR_R + 0.0065, n=12),
              ring_x(x0 + 0.014, BR_C[0], BR_C[1], BR_R + 0.0065, BR_R + 0.0065, n=12)]
        loft(sh, rr, sk, start=(x0 - 0.014, BR_C[0], BR_C[1]), end=(x0 + 0.014, BR_C[0], BR_C[1]),
             start_hard=True, end_hard=True)
        put(PACKC, sh)

    # =========================================================================================
    # 10 BEDROLL
    # =========================================================================================
    sh = Shell("bedroll")
    rr = [ring_x(-BR_H + 0.010, BR_C[0], BR_C[1], BR_R * 0.55, BR_R * 0.55, n=12),     # the rolled end, set in
          ring_x(-BR_H, BR_C[0], BR_C[1], BR_R * 0.80, BR_R * 0.80, n=12),
          ring_x(-BR_H + 0.004, BR_C[0], BR_C[1], BR_R, BR_R, n=12),
          ring_x(0.0, BR_C[0], BR_C[1], BR_R * 1.02, BR_R * 1.02, n=12),
          ring_x(BR_H - 0.004, BR_C[0], BR_C[1], BR_R, BR_R, n=12),
          ring_x(BR_H, BR_C[0], BR_C[1], BR_R * 0.80, BR_R * 0.80, n=12),
          ring_x(BR_H - 0.010, BR_C[0], BR_C[1], BR_R * 0.55, BR_R * 0.55, n=12)]
    loft(sh, rr, sk, start=(-BR_H + 0.010, BR_C[0], BR_C[1]), end=(BR_H - 0.010, BR_C[0], BR_C[1]), crease=(1, 2, 4, 5))
    put(BEDROLL, sh)

    return parts


# ---------------------------------------------------------------------------------------------
# writing
# ---------------------------------------------------------------------------------------------
def merge(shells):
    P = np.concatenate([s["P"] for s in shells]); N = np.concatenate([s["N"] for s in shells])
    b0 = np.concatenate([s["b0"] for s in shells]); b1 = np.concatenate([s["b1"] for s in shells])
    w0 = np.concatenate([s["w0"] for s in shells])
    T = []; off = 0
    for s in shells:
        T.append(s["T"] + off); off += len(s["P"])
    return dict(P=P, N=N, b0=b0, b1=b1, w0=w0, T=np.concatenate(T))


def write(parts, path):
    out = bytearray()
    out += b"TLDF"
    out += struct.pack("<H", 1)
    out += struct.pack("<H", len(BONES))
    for name, pos in BONES:
        nb = name.encode("ascii")
        out += struct.pack("<B", len(nb)) + nb + struct.pack("<3f", *pos)
    used = [c for c in sorted(parts) if parts[c]]
    out += struct.pack("<H", len(used))
    nv = nt = 0
    stats = []
    for c in used:
        m = merge(parts[c])
        n = len(m["P"])
        if n > 65535:
            raise RuntimeError("part too large for 16-bit indices")
        # one joint only is written as bone1 = bone0, weight0 = 1
        w0 = np.clip(m["w0"], 0.0, 1.0)
        b0, b1 = m["b0"].copy(), m["b1"].copy()
        one = w0 >= 0.9999
        b1[one] = b0[one]; w0[one] = 1.0
        zero = w0 <= 0.0001
        b0[zero] = b1[zero]; w0[zero] = 1.0
        out += struct.pack("<BII", c, n, m["T"].size)
        v = np.zeros(n, np.dtype([("p", "<f4", 3), ("n", "<f4", 3), ("b0", "u1"), ("b1", "u1"), ("w0", "<f4")]))
        v["p"] = m["P"]; v["n"] = unit(m["N"]); v["b0"] = b0; v["b1"] = b1; v["w0"] = w0
        out += v.tobytes()
        out += m["T"].astype("<u2").tobytes()
        nv += n; nt += len(m["T"])
        stats.append((c, n, len(m["T"]), len(parts[c])))
    with open(path, "wb") as f:
        f.write(out)
    return nv, nt, stats, len(out)


def main():
    parts = build()
    nv, nt, stats, size = write(parts, OUT)
    print("wrote %s (%d bytes)" % (OUT, size))
    for c, n, t, k in stats:
        print("  colour %2d %-9s %5d vertices %5d triangles  (%d shells)" % (c, COLOUR_NAMES[c], n, t, k))
    print("  total: %d vertices, %d triangles, %d parts" % (nv, nt, len(stats)))
    if nv > MAX_VERTS or nt > MAX_TRIS:
        print("OVER BUDGET"); sys.exit(1)


if __name__ == "__main__":
    main()
