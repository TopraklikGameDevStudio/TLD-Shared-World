#!/usr/bin/env python3
"""
validate.py - re-reads survivor.bin and checks it against what the mod expects.

Stand-alone on purpose: it has its own copy of the bone table and its own reader, so a mistake in the generator
cannot hide here. Exit code 0 = every check passed.

Checks
  1  header, version, the 16 bones (names, order, rest positions), no bytes left over
  2  per part: colour index 0..10, exactly one part per colour, index range, bone range, weights in 0..1,
     "one bone" written as bone1 = bone0 / weight0 = 1, unit normals, finite numbers
  3  budget: at most 6000 vertices and 10000 triangles
  4  winding: cross(b-a, c-a) agrees with the stored normals for at least 99.5 % of the triangles, and every
     closed shell has a positive volume (so "agrees with the normals" really means outward)
  5  every part is closed (open-edge count per part); an open edge would have to lie inside another part
  6  hidden clothes: with hat (2), cuff (3), mitts (5) and scarf (7) taken away every remaining part is still
     closed, nothing floats (all shells hang together by overlap), the neck reaches into the parka and the wrists
     into the sleeves; with them on, the hat covers the hair and the mitts cover the hands completely
  7  pack: everything on the Pack bone is pack, straps or bedroll, and with that bone scaled to 0 (or to the
     3.4 % that Figure.cs leaves before it switches the object off) it all lies inside the parka's back
"""
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
PATH = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "survivor.bin")

BONES = [
    ("Hips", (0, 0.94, 0)), ("Spine", (0, 1.00, 0)), ("Head", (0, 1.50, 0.01)), ("Pack", (0, 1.25, -0.145)),
    ("ShoulderL", (-0.235, 1.435, 0)), ("ElbowL", (-0.235, 1.145, 0)), ("HandL", (-0.235, 0.845, 0.03)),
    ("ShoulderR", (0.235, 1.435, 0)), ("ElbowR", (0.235, 1.145, 0)), ("HandR", (0.235, 0.845, 0.03)),
    ("HipL", (-0.095, 0.92, 0)), ("KneeL", (-0.095, 0.49, 0)), ("AnkleL", (-0.095, 0.09, 0)),
    ("HipR", (0.095, 0.92, 0)), ("KneeR", (0.095, 0.49, 0)), ("AnkleR", (0.095, 0.09, 0)),
]
B = {n: i for i, (n, _) in enumerate(BONES)}
COLOURS = ["Parka", "Trousers", "Hat", "Cuff", "Pack", "Mitts", "Boots", "Scarf", "Skin", "Hair", "Bedroll"]
(PARKA, TROUSERS, HAT, CUFF, PACK, MITTS, BOOTS, SCARF, SKIN, HAIR, BEDROLL) = range(11)
HIDEABLE = (HAT, CUFF, MITTS, SCARF)
MAX_VERTS, MAX_TRIS = 6000, 10000

results = []


def check(name, ok, detail=""):
    results.append(bool(ok))
    print("%s  %s%s" % ("PASS" if ok else "FAIL", name, ("  - " + detail) if detail else ""))
    return ok


# ---------------------------------------------------------------------------------------------
def read(path):
    d = open(path, "rb").read()
    o = 0
    magic = d[0:4]; o = 4
    (ver, nb) = struct.unpack_from("<HH", d, o); o += 4
    bones = []
    for _ in range(nb):
        ln = d[o]; o += 1
        name = d[o:o + ln].decode("ascii"); o += ln
        bones.append((name, struct.unpack_from("<3f", d, o))); o += 12
    (npart,) = struct.unpack_from("<H", d, o); o += 2
    parts = []
    for _ in range(npart):
        col = d[o]; o += 1
        nv, ni = struct.unpack_from("<II", d, o); o += 8
        P = np.zeros((nv, 3)); N = np.zeros((nv, 3)); b0 = np.zeros(nv, int); b1 = np.zeros(nv, int); w0 = np.zeros(nv)
        raw = np.frombuffer(d, np.dtype([("p", "<f4", 3), ("n", "<f4", 3), ("b0", "u1"), ("b1", "u1"), ("w", "<f4")]),
                            nv, o)
        o += nv * 30
        P[:] = raw["p"]; N[:] = raw["n"]; b0[:] = raw["b0"]; b1[:] = raw["b1"]; w0[:] = raw["w"]
        idx = np.frombuffer(d, "<u2", ni, o).astype(np.int64); o += ni * 2
        parts.append(dict(colour=col, P=P, N=N, b0=b0, b1=b1, w0=w0, idx=idx, ni=ni))
    return magic, ver, bones, parts, o, len(d)


def weld(P, *extra):
    """Vertices that are the same point (and follow the same joints) get one number: creases double vertices."""
    key = [np.round(P / 2e-6).astype(np.int64)]
    for e in extra:
        key.append(np.asarray(e).reshape(len(P), -1).astype(np.int64))
    key = np.concatenate(key, axis=1)
    _, inv = np.unique(key, axis=0, return_inverse=True)
    return inv.ravel()


def edge_report(W):
    """W: triangles in welded vertex numbers -> (open edges as vertex pairs, edges used more than twice)."""
    e = np.concatenate([W[:, [0, 1]], W[:, [1, 2]], W[:, [2, 0]]])
    e = e[e[:, 0] != e[:, 1]]
    code = e[:, 0] * (W.max() + 1) + e[:, 1]
    rcode = e[:, 1] * (W.max() + 1) + e[:, 0]
    uniq, cnt = np.unique(code, return_counts=True)
    count = dict(zip(uniq.tolist(), cnt.tolist()))
    open_e, multi = [], 0
    for k in range(len(e)):
        n, r = count[int(code[k])], count.get(int(rcode[k]), 0)
        if n > 1 or r > 1:
            multi += 1
        elif r != 1:
            open_e.append((int(e[k, 0]), int(e[k, 1])))
    return open_e, multi


def components(W):
    """Connected shells of a triangle list (welded numbers) -> label per triangle."""
    parent = np.arange(W.max() + 1)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for t in W:
        a, b, c = find(t[0]), find(t[1]), find(t[2])
        parent[b] = a; parent[c] = a
    roots = np.array([find(t[0]) for t in W])
    _, lab = np.unique(roots, return_inverse=True)
    return lab


def winding(Q, tri):
    """Winding number of the triangle soup 'tri' (n,3,3) about each point of Q: 1 inside a closed shell, 0 outside."""
    Q = np.atleast_2d(Q)
    out = np.zeros(len(Q))
    step = max(1, 400000 // max(1, len(tri)))
    for s in range(0, len(Q), step):
        q = Q[s:s + step, None, :]
        a = tri[None, :, 0, :] - q; b = tri[None, :, 1, :] - q; c = tri[None, :, 2, :] - q
        la = np.linalg.norm(a, axis=2); lb = np.linalg.norm(b, axis=2); lc = np.linalg.norm(c, axis=2)
        num = np.einsum("qti,qti->qt", a, np.cross(b, c))
        den = la * lb * lc + np.einsum("qti,qti->qt", a, b) * lc + np.einsum("qti,qti->qt", b, c) * la \
            + np.einsum("qti,qti->qt", c, a) * lb
        out[s:s + step] = (2.0 * np.arctan2(num, den)).sum(axis=1) / (4.0 * np.pi)
    return out


def tris_of(p, sel=None):
    T = p["idx"].reshape(-1, 3)
    if sel is not None:
        T = T[sel]
    return p["P"][T]


def samples(p, vmask=None):
    """Points on a part's surface to test: its vertices, the middles of its triangles and of their edges."""
    T = p["idx"].reshape(-1, 3)
    if vmask is None:
        vmask = np.ones(len(p["P"]), bool)
    tri = p["P"][T[vmask[T].all(axis=1)]]
    mids = [(tri[:, i] + tri[:, (i + 1) % 3]) / 2 for i in range(3)]
    return np.concatenate([p["P"][vmask], tri.mean(axis=1)] + mids)


# ---------------------------------------------------------------------------------------------
def main():
    magic, ver, bones, parts, used, size = read(PATH)
    print("file: %s (%d bytes)\n" % (PATH, size))

    # ---- 1 header and bones ----
    check("magic 'TLDF'", magic == b"TLDF", repr(magic))
    check("version 1", ver == 1, str(ver))
    check("no bytes left over", used == size, "%d of %d read" % (used, size))
    ok = len(bones) == 16
    bad = []
    if ok:
        for i, ((n, p), (en, ep)) in enumerate(zip(bones, BONES)):
            if n != en or np.abs(np.array(p) - np.array(ep, np.float32)).max() > 1e-6:
                bad.append("%d %s %s" % (i, n, p))
    check("16 bones equal the table (names, order, positions)", ok and not bad, "; ".join(bad))

    # ---- 2 parts ----
    cols = [p["colour"] for p in parts]
    check("colour indices 0..10", all(0 <= c <= 10 for c in cols), str(cols))
    check("exactly one part per colour used (at most 11)", len(set(cols)) == len(cols) and len(cols) <= 11, str(cols))
    by = {p["colour"]: p for p in parts}
    nv = sum(len(p["P"]) for p in parts)
    nt = sum(p["ni"] // 3 for p in parts)
    ok_idx = ok_w = ok_b = ok_n = ok_one = ok_fin = True
    agree = total = 0
    worst_n = 0.0
    for p in parts:
        n = len(p["P"])
        ok_idx &= p["ni"] % 3 == 0 and n > 0 and p["ni"] > 0 and int(p["idx"].max()) < n and n <= 65535
        ok_b &= bool((p["b0"] < 16).all() and (p["b1"] < 16).all())
        ok_w &= bool((p["w0"] >= 0).all() and (p["w0"] <= 1).all())
        same = p["b0"] == p["b1"]
        ok_one &= bool((p["w0"][same] == 1.0).all())
        ok_fin &= bool(np.isfinite(p["P"]).all() and np.isfinite(p["N"]).all())
        ln = np.linalg.norm(p["N"], axis=1)
        worst_n = max(worst_n, float(np.abs(ln - 1).max()))
        ok_n &= bool(np.abs(ln - 1).max() < 1e-3)
        T = p["idx"].reshape(-1, 3)
        a, b, c = p["P"][T[:, 0]], p["P"][T[:, 1]], p["P"][T[:, 2]]
        fn = np.cross(b - a, c - a)
        ns = p["N"][T[:, 0]] + p["N"][T[:, 1]] + p["N"][T[:, 2]]
        agree += int((np.einsum("ij,ij->i", fn, ns) > 0).sum()); total += len(T)
    check("indices in range, 3 per triangle", ok_idx)
    check("bone numbers < 16", ok_b)
    hands = sum(int(np.isin(p["b0"], (B["HandL"], B["HandR"])).sum() + np.isin(p["b1"], (B["HandL"], B["HandR"])).sum())
                for p in parts)
    check("HandL / HandR carry no vertices (attachment points only)", hands == 0, str(hands))
    check("weights in 0..1", ok_w)
    check("one bone only is written as bone1 = bone0, weight0 = 1", ok_one)
    check("all numbers finite", ok_fin)
    check("normals are unit length", ok_n, "largest error %.2e" % worst_n)
    allP = np.concatenate([p["P"] for p in parts])
    lo, hi = allP.min(0), allP.max(0)
    check("stands on y = 0, about 1.8 m tall, centred",
          abs(lo[1]) < 1e-4 and 1.75 < hi[1] < 1.90 and abs(lo[0] + hi[0]) < 1e-4,
          "x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))

    # ---- 3 budget ----
    check("budget: vertices <= %d" % MAX_VERTS, nv <= MAX_VERTS, str(nv))
    check("budget: triangles <= %d" % MAX_TRIS, nt <= MAX_TRIS, str(nt))

    # ---- 4 winding ----
    pct = 100.0 * agree / total
    check("winding agrees with the stored normals (>= 99.5 %)", pct >= 99.5, "%.2f %% of %d" % (pct, total))

    # ---- 5 closed ----
    print()
    all_closed = True
    hidden_ok = True
    neg_vol = 0
    shells = []                         # (colour, triangles as points (n,3,3), vertex points)
    for p in parts:
        T = p["idx"].reshape(-1, 3)
        w = weld(p["P"], p["b0"], p["b1"], np.round(p["w0"] * 1000))
        W = w[T]
        opens, multi = edge_report(W)
        lab = components(W)
        nsh = lab.max() + 1
        for s in range(nsh):
            tri = p["P"][T[lab == s]]
            vol = np.einsum("ij,ij->i", tri[:, 0], np.cross(tri[:, 1], tri[:, 2])).sum() / 6.0
            if vol <= 0:
                neg_vol += 1
            shells.append((p["colour"], tri, np.unique(tri.reshape(-1, 3), axis=0)))
        note = ""
        if opens:
            all_closed = False
            # an open border is only acceptable inside another part
            first = np.zeros(w.max() + 1, int); first[w] = np.arange(len(w))
            mid = np.array([(p["P"][first[a]] + p["P"][first[b]]) / 2 for a, b in opens])
            others = np.concatenate([tris_of(q) for q in parts if q is not p])
            inside = winding(mid, others) > 0.5
            hidden_ok &= bool(inside.all())
            note = "  (%d of them hidden inside another part)" % int(inside.sum())
        print("      colour %2d %-8s %5d vertices %5d triangles %2d shells  open edges: %d%s  edges used >2x: %d"
              % (p["colour"], COLOURS[p["colour"]], len(p["P"]), len(T), nsh, len(opens), note, multi))
        all_closed &= multi == 0
    check("every part is closed (no open edge anywhere)", all_closed)
    if not all_closed:
        check("open borders are hidden inside another part", hidden_ok)
    check("every closed shell is wound outward (positive volume)", neg_vol == 0, "%d shells, %d inside-out"
          % (len(shells), neg_vol))

    # ---- 6 with hat, cuff, mitts and scarf hidden ----
    print()
    bare = [p for p in parts if p["colour"] not in HIDEABLE]
    ok_bare = True
    for p in bare:
        T = p["idx"].reshape(-1, 3)
        w = weld(p["P"], p["b0"], p["b1"], np.round(p["w0"] * 1000))
        opens, multi = edge_report(w[T])
        ok_bare &= not opens and multi == 0
    check("hat, cuff, mitts, scarf hidden: every remaining part is still closed", ok_bare,
          "parts left: " + ", ".join(COLOURS[p["colour"]] for p in bare))

    def hang_together(shs):
        """All shells of a set connected by overlapping (a vertex of one inside the other)."""
        n = len(shs)
        box = [(s[2].min(0), s[2].max(0)) for s in shs]
        parent = list(range(n))

        def find(a):
            while parent[a] != a:
                parent[a] = parent[parent[a]]
                a = parent[a]
            return a
        for i in range(n):
            for j in range(i + 1, n):
                if find(i) == find(j):
                    continue
                if (box[i][0] > box[j][1]).any() or (box[j][0] > box[i][1]).any():
                    continue
                if (winding(shs[i][2], shs[j][1]) > 0.5).any() or (winding(shs[j][2], shs[i][1]) > 0.5).any():
                    parent[find(i)] = find(j)
        return len({find(i) for i in range(n)})
    groups = hang_together([s for s in shells if s[0] not in HIDEABLE])
    check("hidden clothes: nothing floats (all shells hang together)", groups == 1, "%d group(s)" % groups)
    groups = hang_together(shells)
    check("fully dressed: nothing floats", groups == 1, "%d group(s)" % groups)

    skin, parka = by.get(SKIN), by.get(PARKA)
    if skin is not None and parka is not None:
        ptri = tris_of(parka)
        neck = (skin["b0"] == B["Spine"]) & (skin["b1"] == B["Spine"])
        check("the neck's foot (skin on the Spine bone) lies inside the parka",
              neck.sum() >= 8 and bool((winding(skin["P"][neck], ptri) > 0.5).all()), "%d vertices" % neck.sum())
        hand = np.isin(skin["b0"], (B["ElbowL"], B["ElbowR"]))
        top = hand & (skin["P"][:, 1] > skin["P"][hand, 1].max() - 0.012)
        check("the wrists (top of the bare hands) lie inside the sleeves",
              top.sum() >= 8 and bool((winding(skin["P"][top], ptri) > 0.5).all()), "%d vertices" % top.sum())
        check("bare hands follow the Elbow bones only", bool((skin["b0"][hand] == skin["b1"][hand]).all()))
        if by.get(MITTS) is not None:
            q = samples(skin, hand)
            wn = winding(q, tris_of(by[MITTS]))
            check("the mitts cover the bare hands completely", bool((wn > 0.5).all()),
                  "%d of %d test points inside the mitts" % ((wn > 0.5).sum(), len(q)))
            m = by[MITTS]
            check("mitts follow the Elbow bones only",
                  bool(np.isin(m["b0"], (B["ElbowL"], B["ElbowR"])).all() and (m["b0"] == m["b1"]).all()))
    hair = by.get(HAIR)
    if hair is not None and by.get(HAT) is not None and by.get(CUFF) is not None:
        # hair cap = the big shell of the hair part; brows and eye dots stay visible, they are not hair to cover
        T = hair["idx"].reshape(-1, 3)
        w = weld(hair["P"], hair["b0"], hair["b1"], np.round(hair["w0"] * 1000))
        lab = components(w[T])
        big = np.bincount(lab).argmax()
        vm = np.zeros(len(hair["P"]), bool); vm[T[lab == big].ravel()] = True
        q = samples(hair, vm)
        cover = np.concatenate([tris_of(by[HAT]), tris_of(by[CUFF]), tris_of(skin)])
        wn = winding(q, cover)
        check("the hat (toque + cuff, over the head) covers the hair cap completely", bool((wn > 0.5).all()),
              "%d of %d test points covered" % ((wn > 0.5).sum(), len(q)))
        other = samples(hair, ~vm)
        wn2 = winding(other, np.concatenate([tris_of(by[HAT]), tris_of(by[CUFF])]))
        print("      (brows and eye dots: %d of %d test points lie under the hat; they are meant to show)"
              % ((wn2 > 0.5).sum(), len(other)))
    for c in (HAT, CUFF, HAIR):
        if by.get(c) is not None:
            check("%s follows the Head bone only" % COLOURS[c],
                  bool((by[c]["b0"] == B["Head"]).all() and (by[c]["b1"] == B["Head"]).all()))

    # ---- 7 the pack bone ----
    print()
    on_pack = {}
    for p in parts:
        m = (p["b0"] == B["Pack"]) | (p["b1"] == B["Pack"])
        if m.any():
            on_pack[p["colour"]] = (int(m.sum()), bool(m.all() and (p["b0"] == p["b1"]).all()))
    check("only pack (4) and bedroll (10) hang on the Pack bone, wholly and rigidly",
          set(on_pack) <= {PACK, BEDROLL} and all(v[1] for v in on_pack.values()), str(on_pack))
    if parka is not None and on_pack:
        ptri = tris_of(parka)
        origin = np.array(BONES[B["Pack"]][1], float)
        pts = np.concatenate([by[c]["P"] for c in on_pack])
        w0 = winding(origin[None], ptri)[0]
        check("Pack bone scaled to 0: the pack collapses to a point inside the parka", w0 > 0.5,
              "winding %.2f" % w0)
        small = origin + (pts - origin) * 0.034
        wn = winding(small, ptri)
        d = np.abs(pts - origin).max()
        check("Pack bone at 3.4 %: still wholly inside the parka", bool((wn > 0.5).all()),
              "%d of %d vertices inside; the pack reaches %.2f m from its bone" % ((wn > 0.5).sum(), len(pts), d))

    print()
    print("vertices %d / %d, triangles %d / %d, parts %d" % (nv, MAX_VERTS, nt, MAX_TRIS, len(parts)))
    n_bad = results.count(False)
    print("RESULT: %s (%d checks, %d failed)" % ("OK" if n_bad == 0 else "FAILED", len(results), n_bad))
    sys.exit(0 if n_bad == 0 else 1)


if __name__ == "__main__":
    main()
