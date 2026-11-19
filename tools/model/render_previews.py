#!/usr/bin/env python3
"""
render_previews.py - reads survivor.bin, poses it with the joint maths of Figure.cs (pose.py, linear-blend skinning
in numpy) and renders the previews with Cycles (CPU) into preview/.

  python3 render_previews.py            all previews and preview/sheet.png
  python3 render_previews.py NAME ...   only these (see VIEWS), no sheet
  options: --size=700 --samples=24 --debug (back faces magenta instead of culled) --out=DIR
"""
import math
import os
import sys

import numpy as np
import bpy

import pose

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "survivor.bin")

# preview palette: set 0 of Figure.cs, plus the two new colours
PALETTE = [(.13, .17, .23), (.40, .37, .31), (.47, .16, .13), (.36, .12, .10), (.25, .26, .17), (.20, .14, .10),
           (.11, .09, .08), (.44, .43, .40), (.74, .58, .47), (.16, .11, .08), (.44, .43, .40)]
SET1 = [(.16, .23, .16), (.34, .34, .36), (.60, .46, .17), (.46, .35, .13), (.31, .22, .15), (.13, .13, .14),
        (.16, .11, .08), (.33, .20, .17), (.70, .55, .45), (.16, .11, .08), (.44, .43, .40)]
SET2 = [(.31, .14, .12), (.20, .22, .27), (.56, .56, .52), (.42, .42, .40), (.19, .19, .21), (.30, .24, .16),
        (.10, .10, .10), (.19, .25, .31), (.77, .61, .50), (.16, .11, .08), (.44, .43, .40)]
ALT = list(PALETTE)
ALT[0] = (.45, .33, .20); ALT[1] = (.15, .17, .22); ALT[2] = (.20, .30, .25); ALT[3] = (.15, .23, .19)
BARE = (2, 3, 5, 7)

# name -> pose ("rest" or a key of pose.POSES or a dict for pose.animate), camera azimuth in degrees (0 = from the
# front, 90 = from the figure's right side, 180 = from behind), elevation, and extras
VIEWS = [
    ("rest_front", dict(pose="rest", az=0, el=4)),
    ("rest_back", dict(pose="rest", az=180, el=4)),
    ("rest_side", dict(pose="rest", az=90, el=4)),
    ("rest_threequarter", dict(pose="rest", az=35, el=10)),
    ("pose_stand", dict(pose="stand", az=25, el=8)),
    ("pose_walk", dict(pose="walk", az=65, el=6)),
    ("pose_run", dict(pose="run", az=70, el=6)),
    ("pose_crouch", dict(pose="crouch", az=50, el=10, dist=4.2)),
    ("pose_sit", dict(pose="sit", az=55, el=14, dist=4.2)),
    ("pose_sleep_back", dict(pose="sleep_back", az=62, el=32, dist=5.0)),
    ("pose_downed_side", dict(pose="downed_side", az=40, el=34, dist=5.0)),
    ("pose_light", dict(pose="light", az=40, el=8)),
    ("pose_look_up", dict(pose="look_up", az=60, el=6)),
    ("bare_front", dict(pose="stand", az=0, el=4, hide=BARE)),
    ("bare_threequarter", dict(pose="stand", az=35, el=10, hide=BARE)),
    ("alt_colours", dict(pose="stand", az=-30, el=8, palette=ALT)),
]
EXTRA = {
    "rest_back34": dict(pose="rest", az=145, el=12),
    "crouch_front": dict(pose="crouch", az=10, el=10),
    "crouch_back": dict(pose="crouch", az=150, el=14),
    "crouch_side": dict(pose="crouch", az=90, el=4),
    "sit_front": dict(pose="sit", az=15, el=16),
    "sit_back": dict(pose="sit", az=160, el=16),
    "sit_side": dict(pose="sit", az=90, el=6),
    "run_front": dict(pose="run", az=15, el=6),
    "run_back": dict(pose="run", az=160, el=10),
    "run2": dict(pose=dict(speed=5.0, phase=4.1), az=80, el=6),
    "walk_front": dict(pose="walk", az=15, el=6),
    "walk2": dict(pose=dict(speed=1.5, phase=3.6), az=100, el=6),
    "light_side": dict(pose="light", az=-90, el=4),
    "light_front": dict(pose="light", az=0, el=6),
    "look_up_front": dict(pose="look_up", az=20, el=6),
    "look_down": dict(pose=dict(look_pitch=70.0), az=50, el=6),
    "look_up_max": dict(pose=dict(look_pitch=-80.0, crouch=1.0), az=70, el=6),
    "sleep_top": dict(pose="sleep_back", az=10, el=70),
    "sleep_low": dict(pose="sleep_back", az=100, el=8),
    "sleep_packres": dict(pose="sleep_back", az=100, el=30, pack=0.034),
    "downed_top": dict(pose="downed_side", az=0, el=75),
    "downed_back": dict(pose="downed_side", az=150, el=30),
    "bare_back": dict(pose="stand", az=160, el=10, hide=BARE),
    "bare_side": dict(pose="stand", az=90, el=4, hide=BARE),
    "bare_lookup": dict(pose="look_up", az=50, el=6, hide=BARE),
    "bare_lookdown": dict(pose=dict(look_pitch=70.0), az=50, el=6, hide=BARE),
    "set1": dict(pose="stand", az=25, el=8, palette=SET1),
    "set2": dict(pose="stand", az=25, el=8, palette=SET2),
    "head_front": dict(pose="rest", az=0, el=2, target=(0, 1.62, 0), dist=1.6),
    "head_34": dict(pose="rest", az=40, el=8, target=(0, 1.62, 0), dist=1.6),
    "head_side": dict(pose="rest", az=90, el=2, target=(0, 1.62, 0), dist=1.6),
    "head_back": dict(pose="rest", az=180, el=10, target=(0, 1.58, 0), dist=1.8),
    "bare_head_front": dict(pose="rest", az=0, el=2, target=(0, 1.62, 0), dist=1.6, hide=BARE),
    "bare_head_34": dict(pose="rest", az=40, el=8, target=(0, 1.62, 0), dist=1.6, hide=BARE),
    "bare_head_side": dict(pose="rest", az=90, el=2, target=(0, 1.62, 0), dist=1.6, hide=BARE),
    "bare_head_back": dict(pose="rest", az=160, el=10, target=(0, 1.60, 0), dist=1.6, hide=BARE),
    "hand_bare": dict(pose="stand", az=70, el=5, target=(0.25, 0.90, 0), dist=1.6, hide=BARE),
    "hand_mitt": dict(pose="stand", az=70, el=5, target=(0.25, 0.90, 0), dist=1.6),
    "feet": dict(pose="walk", az=60, el=12, target=(0, 0.25, 0), dist=2.2),
    "under": dict(pose="rest", az=20, el=-35, target=(0, 0.9, 0), dist=3.5, no_ground=True),
    "top": dict(pose="rest", az=0, el=80),
    "knee": dict(pose="crouch", az=70, el=5, target=(0, 0.45, 0.1), dist=2.2),
    "cu_light": dict(pose="light", az=-55, el=8, target=(0.15, 1.35, 0.1), dist=2.0),
    "cu_light_back": dict(pose="light", az=-150, el=12, target=(0.15, 1.35, 0.0), dist=2.0),
    "cu_run": dict(pose="run", az=60, el=8, target=(0, 1.3, 0.1), dist=2.2),
    "cu_run_back": dict(pose="run", az=200, el=10, target=(0, 1.3, 0.1), dist=2.2),
    "cu_bare_up": dict(pose="look_up", az=50, el=4, target=(0, 1.58, 0), dist=1.5, hide=BARE),
    "cu_bare_down": dict(pose=dict(look_pitch=70.0), az=50, el=4, target=(0, 1.58, 0.05), dist=1.5, hide=BARE),
    "cu_up": dict(pose="look_up", az=50, el=4, target=(0, 1.58, 0), dist=1.5),
    "cu_down": dict(pose=dict(look_pitch=70.0), az=50, el=4, target=(0, 1.58, 0.05), dist=1.5),
    "cu_bare_upmax": dict(pose=dict(look_pitch=-80.0, crouch=1.0), az=60, el=4, target=(0, 1.02, 0.25), dist=1.5, hide=BARE),
    "cu_side_tilt": dict(pose="downed_side", az=-20, el=50, target=(-0.9, 0.2, 0.35), dist=1.8),
    "cu_shoulder": dict(pose="stand", az=20, el=12, target=(0.12, 1.38, 0), dist=1.6),
    "cu_shoulder_bare": dict(pose="rest", az=20, el=12, target=(0.12, 1.38, 0), dist=1.6, hide=BARE),
    "cu_boot_sit": dict(pose="sit", az=70, el=10, target=(0, 0.2, 0.35), dist=1.8),
    "cu_boot_crouch": dict(pose="crouch", az=110, el=8, target=(0, 0.2, 0.0), dist=1.8),
    "cu_waist_crouch": dict(pose="crouch", az=140, el=12, target=(0, 0.75, -0.2), dist=2.0),
    "cu_waist_front": dict(pose="crouch", az=25, el=14, target=(0, 0.6, 0.0), dist=2.0),
    "cu_sit": dict(pose="sit", az=35, el=14, target=(0, 0.45, 0.0), dist=2.4),
    "cu_walk_legs": dict(pose="walk", az=35, el=8, target=(0, 0.6, 0.0), dist=2.4),
    "cu_elbow": dict(pose="run", az=100, el=6, target=(0.25, 1.2, 0.1), dist=1.5),
    "mix_crouchwalk": dict(pose=dict(crouch=1.0, speed=1.2, phase=1.2), az=60, el=8, dist=4.4),
    "mix_crouchwalk2": dict(pose=dict(crouch=1.0, speed=1.2, phase=4.3), az=-70, el=8, dist=4.4),
    "mix_halfsit": dict(pose=dict(sit=0.5), az=60, el=10, dist=4.4),
    "mix_halfback": dict(pose=dict(back=0.5), az=60, el=20, dist=5.0),
    "mix_halfside": dict(pose=dict(side=0.5), az=60, el=20, dist=5.0),
    "mix_runlight": dict(pose=dict(speed=5.0, phase=2.2, light=1.0), az=40, el=8),
    "mix_runlookdown": dict(pose=dict(speed=5.0, phase=5.4, look_pitch=60), az=-60, el=8),
    "mix_crouchlight": dict(pose=dict(crouch=1.0, light=1.0, look_pitch=-40), az=30, el=10, dist=4.4),
    "mix_sitlook": dict(pose=dict(sit=1.0, look_pitch=-70), az=-50, el=12, dist=4.4),
    "mix_walkback": dict(pose=dict(speed=1.5, phase=5.0), az=150, el=10),
    "mix_run3": dict(pose=dict(speed=5.0, phase=1.57), az=90, el=4),
    "mix_run4": dict(pose=dict(speed=5.0, phase=0.0), az=90, el=4),
    "cu_fold1": dict(pose=dict(crouch=1.0, speed=1.2, phase=1.2), az=60, el=8, target=(0.1, 0.5, 0.0), dist=1.4),
    "cu_fold2": dict(pose=dict(crouch=1.0, speed=1.2, phase=4.3), az=-70, el=8, target=(-0.1, 0.5, 0.0), dist=1.4),
    "cu_fold3": dict(pose="crouch", az=60, el=8, target=(0.1, 0.5, -0.1), dist=1.4),
    "cu_fold4": dict(pose="crouch", az=20, el=30, target=(0.0, 0.55, -0.05), dist=1.4),
    "cu_boot1": dict(pose="crouch", az=110, el=8, target=(0.1, 0.12, -0.12), dist=1.0),
    "cu_boot2": dict(pose="crouch", az=160, el=10, target=(0.1, 0.12, -0.12), dist=1.0),
    "cu_boot3": dict(pose="crouch", az=60, el=10, target=(0.1, 0.12, -0.12), dist=1.0),
    "cu_boot4": dict(pose="sit", az=90, el=10, target=(0.1, 0.15, 0.45), dist=1.0),
}


def u2b(v):
    """
    Unity (x, y, z) -> Blender (-x, -z, y). Unity's space is left-handed and Blender's right-handed, so a picture that
    shows what the game shows (the figure's right hand on ITS right) needs this mirror; the triangles are turned
    round to match (see render_view), so front faces stay front faces.
    """
    v = np.asarray(v, float)
    return np.stack([-v[..., 0], -v[..., 2], v[..., 1]], -1)


def setup(size, samples):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    sc.cycles.max_bounces = 3
    sc.cycles.use_adaptive_sampling = False
    try:
        sc.cycles.use_denoising = True
        sc.cycles.denoiser = "OPENIMAGEDENOISE"
    except Exception:
        try:
            sc.cycles.use_denoising = False
        except Exception:
            pass
    sc.render.threads_mode = "FIXED"
    sc.render.threads = os.cpu_count() or 2
    sc.render.resolution_x = size
    sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"
    sc.view_settings.view_transform = "Standard"
    try:
        sc.view_settings.look = "None"
    except Exception:
        pass

    world = bpy.data.worlds.new("w")
    sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.80, 0.87, 0.96, 1.0)
    bg.inputs[1].default_value = 0.85

    # snow
    me = bpy.data.meshes.new("ground")
    s = 60.0
    me.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
    ground = bpy.data.objects.new("ground", me)
    sc.collection.objects.link(ground)
    gm = bpy.data.materials.new("snow")
    gm.use_nodes = True
    nt = gm.node_tree
    nt.nodes.clear()
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    d = nt.nodes.new("ShaderNodeBsdfDiffuse")
    d.inputs["Color"].default_value = (0.80, 0.82, 0.85, 1.0)
    nt.links.new(d.outputs[0], o.inputs[0])
    me.materials.append(gm)

    sun_d = bpy.data.lights.new("sun", "SUN")
    sun_d.energy = 3.0
    sun_d.angle = math.radians(5.0)
    sun = bpy.data.objects.new("sun", sun_d)
    sc.collection.objects.link(sun)

    cam_d = bpy.data.cameras.new("cam")
    cam_d.lens = 80.0
    cam_d.sensor_width = 36.0
    cam = bpy.data.objects.new("cam", cam_d)
    sc.collection.objects.link(cam)
    sc.camera = cam
    return sc, cam, sun, ground


def look_at(obj, eye, target):
    from mathutils import Vector
    eye = Vector(eye); target = Vector(target)
    obj.location = eye
    d = (target - eye).normalized()
    obj.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def material(colour, debug):
    m = bpy.data.materials.new("c")
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    d = nt.nodes.new("ShaderNodeBsdfDiffuse")
    d.inputs["Color"].default_value = (colour[0], colour[1], colour[2], 1.0)
    d.inputs["Roughness"].default_value = 1.0
    g = nt.nodes.new("ShaderNodeNewGeometry")
    mix = nt.nodes.new("ShaderNodeMixShader")
    if debug:
        b = nt.nodes.new("ShaderNodeEmission")
        b.inputs["Color"].default_value = (1.0, 0.0, 1.0, 1.0)
        b.inputs["Strength"].default_value = 2.0
    else:
        b = nt.nodes.new("ShaderNodeBsdfTransparent")     # back faces are culled in the game
    nt.links.new(g.outputs["Backfacing"], mix.inputs[0])
    nt.links.new(d.outputs[0], mix.inputs[1])
    nt.links.new(b.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], o.inputs[0])
    return m


def posed(model, spec, pack_override=None):
    rest = [b[1] for b in model["bones"]]
    if spec == "rest":
        hips, rot, pack = pose.REST_POSE["hips"], {}, 1.0
    else:
        args = pose.POSES[spec] if isinstance(spec, str) else spec
        hips, rot, pack, _ = pose.animate(**args)
    if pack_override is not None:
        pack = pack_override
    M, joints = pose.skin_matrices(rest, hips, rot, pack)
    return [pose.skin_part(p, M) for p in model["parts"]], joints


def render_view(sc, cam, sun, ground, model, name, v, out_dir, debug):
    # throw the last figure away
    for ob in [o for o in bpy.data.objects if o.name.startswith("part")]:
        me = ob.data
        bpy.data.objects.remove(ob, do_unlink=True)
        bpy.data.meshes.remove(me)
    for m in [m for m in bpy.data.materials if m.name.startswith("c")]:
        bpy.data.materials.remove(m)

    pal = v.get("palette", PALETTE)
    hide = set(v.get("hide", ()))
    skinned, joints = posed(model, v["pose"], v.get("pack"))
    allp = []
    for part, (P, N) in zip(model["parts"], skinned):
        if part["colour"] in hide:
            continue
        me = bpy.data.meshes.new("part%d" % part["colour"])
        me.from_pydata(u2b(P).tolist(), [], part["T"][:, [0, 2, 1]].tolist())
        me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
        me.normals_split_custom_set_from_vertices(u2b(N).tolist())
        me.materials.append(material(pal[part["colour"]], debug))
        ob = bpy.data.objects.new("part%d" % part["colour"], me)
        sc.collection.objects.link(ob)
        allp.append(P)
    allp = np.concatenate(allp)
    allp = allp[allp[:, 1] > -0.02]
    lo, hi = allp.min(0), allp.max(0)
    target = np.array(v["target"]) if "target" in v else 0.5 * (lo + hi)
    if "target" not in v and v["pose"] in ("rest", "stand", "light", "look_up", "walk", "run"):
        target = np.array([0.0, 0.93, 0.0])
    dist = v.get("dist", 5.6)
    az, el = math.radians(v["az"]), math.radians(v["el"])
    eye = target + dist * np.array([math.sin(az) * math.cos(el), math.sin(el), math.cos(az) * math.cos(el)])
    look_at(cam, u2b(eye).tolist(), u2b(target).tolist())
    # the key light comes from the viewer's upper left
    la = az + math.radians(38)
    le = math.radians(48)
    ldir = np.array([math.sin(la) * math.cos(le), math.sin(le), math.cos(la) * math.cos(le)])
    look_at(sun, u2b(target + ldir * 10).tolist(), u2b(target).tolist())
    ground.hide_render = bool(v.get("no_ground", False))
    sc.render.filepath = os.path.join(out_dir, name + ".png")
    bpy.ops.render.render(write_still=True)


def sheet(out_dir, names, tile=420, cols=4):
    from PIL import Image, ImageDraw
    rows = (len(names) + cols - 1) // cols
    W, H = cols * tile, rows * tile
    im = Image.new("RGB", (W, H), (232, 238, 245))
    dr = ImageDraw.Draw(im)
    for i, n in enumerate(names):
        t = Image.open(os.path.join(out_dir, n + ".png")).convert("RGB").resize((tile, tile), Image.LANCZOS)
        x, y = (i % cols) * tile, (i // cols) * tile
        im.paste(t, (x, y))
        dr.text((x + 8, y + 6), n, fill=(30, 40, 60))
    im.save(os.path.join(out_dir, "sheet.png"))


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    opts = dict(a[2:].split("=", 1) if "=" in a else (a[2:], "1") for a in sys.argv[1:] if a.startswith("--"))
    size = int(opts.get("size", 700)); samples = int(opts.get("samples", 24))
    debug = "debug" in opts
    out_dir = opts.get("out", os.path.join(HERE, "preview"))
    os.makedirs(out_dir, exist_ok=True)
    model = pose.read_tldf(BIN)
    allv = dict(VIEWS); allv.update(EXTRA)
    names = argv if argv else [n for n, _ in VIEWS]
    sc, cam, sun, ground = setup(size, samples)
    for n in names:
        render_view(sc, cam, sun, ground, model, n, allv[n], out_dir, debug)
        print("rendered", n, flush=True)
    if not argv:
        sheet(out_dir, names)
        print("sheet.png")


if __name__ == "__main__":
    main()
