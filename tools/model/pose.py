#!/usr/bin/env python3
"""
Shared helpers for the previews: reads survivor.bin, ports the joint maths of Figure.cs (Animate, the pose tables,
Turn) to numpy and applies linear-blend skinning.

Nothing here is used by the game; it only has to move the mesh exactly the way Figure.cs moves the joints.
"""
import math
import struct
import numpy as np

# ---------------------------------------------------------------------------------------------
# file
# ---------------------------------------------------------------------------------------------
def read_tldf(path):
    """-> dict(bones=[(name, (x,y,z))], parts=[dict(colour, P, N, b0, b1, w0, T)])"""
    with open(path, "rb") as f:
        d = f.read()
    o = 0
    if d[:4] != b"TLDF":
        raise ValueError("not a TLDF file")
    o = 4
    (ver,) = struct.unpack_from("<H", d, o); o += 2
    (nb,) = struct.unpack_from("<H", d, o); o += 2
    bones = []
    for _ in range(nb):
        ln = d[o]; o += 1
        name = d[o:o + ln].decode("ascii"); o += ln
        pos = struct.unpack_from("<3f", d, o); o += 12
        bones.append((name, pos))
    (npart,) = struct.unpack_from("<H", d, o); o += 2
    parts = []
    vdt = np.dtype([("p", "<f4", 3), ("n", "<f4", 3), ("b0", "u1"), ("b1", "u1"), ("w0", "<f4")])
    assert vdt.itemsize == 30
    for _ in range(npart):
        col = d[o]; o += 1
        nv, ni = struct.unpack_from("<II", d, o); o += 8
        v = np.frombuffer(d, vdt, nv, o); o += nv * 30
        idx = np.frombuffer(d, "<u2", ni, o).astype(np.int64); o += ni * 2
        parts.append(dict(colour=col, P=v["p"].astype(np.float64), N=v["n"].astype(np.float64),
                          b0=v["b0"].astype(np.int64), b1=v["b1"].astype(np.int64),
                          w0=v["w0"].astype(np.float64), T=idx.reshape(-1, 3)))
    return dict(version=ver, bones=bones, parts=parts, size=len(d), consumed=o)


# ---------------------------------------------------------------------------------------------
# skeleton (as in Figure.Make)
# ---------------------------------------------------------------------------------------------
NAMES = ["Hips", "Spine", "Head", "Pack", "ShoulderL", "ElbowL", "HandL", "ShoulderR", "ElbowR", "HandR",
         "HipL", "KneeL", "AnkleL", "HipR", "KneeR", "AnkleR"]
PARENT = [-1, 0, 1, 1, 1, 4, 5, 1, 7, 8, 0, 10, 11, 0, 13, 14]
(HIPS, SPINE, HEAD, PACK, SHL, ELL, HAL, SHR, ELR, HAR, HIPL, KNL, ANL, HIPR, KNR, ANR) = range(16)

Thigh, Shin, FootH, HipSocket = 0.43, 0.40, 0.09, 0.02
StandHips = HipSocket + Thigh + Shin + FootH

(HY, HF, HZ, BP, BR, SP, HP, HR, ALP, ALR, ALE, ARP, ARR, ARE, LLP, LLR, LLK, LRP, LRR, LRK, FLAT, NP) = range(22)

StandP = [StandHips, 0, 0, 0, 0, 2, 0, 0, 3, 7, 12, 3, 7, 12, 0, 2.5, 2, 0, 2.5, 2, 1]
CrouchP = [StandHips, 0, -0.14, 0, 0, 38, -24, 0, 46, 8, 50, 46, 8, 50, 74, 5, 122, 74, 5, 122, 1]
SitP = [0.13, 0, -0.28, 0, 0, 20, -8, 0, 55, -5, 55, 55, -5, 55, 112, 4, 49, 112, 4, 49, 1]
BackP = [0.16, 1, 0, -90, 0, 0, -4, 0, -10, 9, 4, -10, 9, 4, -2, 5, 2, -2, 5, 2, 0]
SideP = [0.235, 1, 0, -10, 90, 6, 8, 42, 62, 2, 48, 40, -62, 42, 24, 10, 38, 52, -24, 72, 0]

Deg = 0.017453292


def Lerp(a, b, t): return a + (b - a) * t
def Clamp(v, lo, hi): return lo if v < lo else hi if v > hi else v
def Ease(t): return t * t * (3.0 - 2.0 * t)


def Turn(x, y, z):
    """Figure.Turn: Unity's Euler(x, y, z) as a 3x3 matrix (same quaternion, then Unity's q*v)."""
    hx, hy, hz = x * Deg * 0.5, y * Deg * 0.5, z * Deg * 0.5
    sx, cx, sy, cy, sz, cz = math.sin(hx), math.cos(hx), math.sin(hy), math.cos(hy), math.sin(hz), math.cos(hz)
    qx = sx * cy * cz + cx * sy * sz
    qy = cx * sy * cz - sx * cy * sz
    qz = cx * cy * sz - sx * sy * cz
    qw = cx * cy * cz + sx * sy * sz
    return np.array([
        [1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
        [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
        [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)]])


def animate(crouch=0.0, sit=0.0, back=0.0, side=0.0, light=0.0, look_pitch=0.0, speed=0.0, phase=0.0,
            clock=0.0, seed=0.0):
    """
    The body of Figure.Animate after the glides have settled: the arguments are the settled weights (_wCrouch ...),
    the smoothed speed, the stride phase and the look pitch (degrees, down positive).
    -> (hips local position, dict joint index -> local rotation 3x3, pack scale, the pose numbers)
    """
    wC, wSit, wBack, wSide, wL = Ease(crouch), Ease(sit), Ease(back), Ease(side), Ease(light)
    pitch = Clamp(look_pitch, -80.0, 80.0)
    up = (1 - wSit) * (1 - wBack) * (1 - wSide)
    lie = max(wBack, wSide)
    p = [0.0] * 21
    for i in range(21):
        v = StandP[i]
        v += (CrouchP[i] - v) * wC
        v += (SitP[i] - v) * wSit
        v += (BackP[i] - v) * wBack
        v += (SideP[i] - v) * wSide
        p[i] = v

    gait = Ease(Clamp((speed - 0.15) / 0.45, 0, 1)) * up
    run = Ease(Clamp((speed - 2.6) / 1.6, 0, 1))
    slow = Clamp(speed / 1.6, 0, 1)
    swing = Lerp(Lerp(14, 24, slow), 34, run)
    s, c = math.sin(phase), math.cos(phase)
    low = Lerp(1, 0.6, wC)
    A = swing * low
    K = Lerp(46, 82, run) * Lerp(1, 0.55, wC)
    B = Lerp(Lerp(10, 22, slow), 36, run) * low
    swL, swR = Ease(max(0, c)), Ease(max(0, -c))
    p[LLP] += gait * (A * s + 7 * run); p[LRP] += gait * (-A * s + 7 * run)
    p[LLK] += gait * (K * swL + 5); p[LRK] += gait * (K * swR + 5)
    p[ALP] += gait * -B * s; p[ARP] += gait * B * s
    p[ALE] += gait * (Lerp(6, 62, run) + 8 * max(0, -s)); p[ARE] += gait * (Lerp(6, 62, run) + 8 * max(0, s))
    p[SP] += gait * Lerp(3, 13, run)
    twist = -gait * Lerp(4, 9, run) * s

    still = (1 - gait) * up
    breath = math.sin(clock * 1.7 + seed)
    sway = math.sin(clock * 0.69 + seed * 2)
    p[SP] += 0.8 * breath * (still + wSit * (1 - lie)) + 0.7 * breath * wBack * (1 - wSide)
    p[ALR] += 0.5 * breath * still; p[ARR] += 0.5 * breath * still
    lean = -1.2 * sway * still
    p[BR] += lean; p[LLR] += lean; p[LRR] -= lean
    hipsX = 0.012 * sway * still
    headYaw = 5 * math.sin(clock * 0.31 + seed) * math.sin(clock * 0.17 + seed * 3) * still

    p[HP] += pitch * 0.6 * (1 - lie)
    p[SP] += pitch * 0.2 * up

    hold = wL * (1 - lie)
    p[ARP] = Lerp(p[ARP], 42 + 0.9 * p[SP], hold)
    p[ARR] = Lerp(p[ARR], 4, hold)
    p[ARE] = Lerp(p[ARE], 72, hold)

    dropL = Thigh * math.cos(p[LLP] * Deg) + Shin * math.cos((p[LLP] - p[LLK]) * Deg)
    dropR = Thigh * math.cos(p[LRP] * Deg) + Shin * math.cos((p[LRP] - p[LRK]) * Deg)
    onLegs = HipSocket + FootH + max(dropL, dropR)
    onLegs += max(0, StandHips - 0.004 - onLegs) * 0.55 * run * gait * (1 - wC)
    hipsY = Lerp(onLegs, p[HY], Clamp(p[HF], 0, 1))
    flat = Clamp(p[FLAT], 0, 1)

    rot = {
        HIPS: Turn(p[BP], 0, p[BR]),
        SPINE: Turn(p[SP], twist, 0),
        HEAD: Turn(p[HP], headYaw, p[HR]),
        SHL: Turn(-p[ALP], 0, -p[ALR]), ELL: Turn(-p[ALE], 0, 0),
        SHR: Turn(-p[ARP], 0, p[ARR]), ELR: Turn(-p[ARE], 0, 0),
        HIPL: Turn(-p[LLP], 0, -p[LLR]), KNL: Turn(p[LLK], 0, 0),
        HIPR: Turn(-p[LRP], 0, p[LRR]), KNR: Turn(p[LRK], 0, 0),
        ANL: Turn(flat * (p[LLP] - p[LLK]) - gait * 16 * swL * s, 0, 0),
        ANR: Turn(flat * (p[LRP] - p[LRK]) + gait * 16 * swR * s, 0, 0),
    }
    pack = 1.0 - wBack
    return np.array([hipsX, hipsY, p[HZ]]), rot, pack, p


def skin_matrices(rest, hips_pos, rot, pack_scale):
    """World matrix of every joint times the inverse of its rest (bind) matrix: 16 x (3x3 linear, 3 offset)."""
    rest = np.asarray(rest, float)
    Wl = [None] * 16      # linear part (rotation, scale)
    Wt = [None] * 16      # translation
    for i in range(16):
        R = rot.get(i, np.eye(3))
        if i == PACK:
            R = R * pack_scale
        pa = PARENT[i]
        if pa < 0:
            Wl[i] = R
            Wt[i] = np.asarray(hips_pos, float)
        else:
            local = rest[i] - rest[pa]
            Wt[i] = Wt[pa] + Wl[pa] @ local
            Wl[i] = Wl[pa] @ R
    M = [(Wl[i], Wt[i] - Wl[i] @ rest[i]) for i in range(16)]
    return M, Wt


def skin_part(part, M):
    """Linear-blend skinning of one part -> posed positions and normals."""
    P, N, b0, b1, w0 = part["P"], part["N"], part["b0"], part["b1"], part["w0"]
    L = np.stack([m[0] for m in M]); O = np.stack([m[1] for m in M])
    p0 = np.einsum("nij,nj->ni", L[b0], P) + O[b0]
    p1 = np.einsum("nij,nj->ni", L[b1], P) + O[b1]
    Pp = p0 * w0[:, None] + p1 * (1 - w0[:, None])
    n0 = np.einsum("nij,nj->ni", L[b0], N)
    n1 = np.einsum("nij,nj->ni", L[b1], N)
    Nn = n0 * w0[:, None] + n1 * (1 - w0[:, None])
    ln = np.linalg.norm(Nn, axis=1)
    Nn = np.where(ln[:, None] > 1e-9, Nn / np.maximum(ln, 1e-9)[:, None], N)
    return Pp, Nn


REST_POSE = dict(hips=np.array([0.0, StandHips, 0.0]), rot={}, pack=1.0)

# the posed previews: name -> arguments of animate()
POSES = {
    "stand": dict(),
    "walk": dict(speed=1.5, phase=0.95),
    "run": dict(speed=5.0, phase=0.95),
    "crouch": dict(crouch=1.0),
    "sit": dict(sit=1.0),
    "sleep_back": dict(back=1.0),
    "downed_side": dict(side=1.0),
    "light": dict(light=1.0),
    "look_up": dict(look_pitch=-60.0),
}
