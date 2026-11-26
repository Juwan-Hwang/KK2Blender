"""End-to-end Import All with the real KKBP importer, no game needed.

KKBP's own test bundle builder (importer/tests/bundle_builder.py in its source) writes a small but
valid Export Bundle. This script writes a KK2Blender export folder around it, as the Studio plugin
would, with bones that move, an expression, an outfit piece that comes off, and runs Import All.

    KKBP_SRC=/path/to/kkbp-source blender -b --factory-startup --python-exit-code 1 \
        --python tests/blender/run_kkbp_integration.py

KKBP must be installed as an extension (bl_ext.<repo>.kkbp); the script enables it.
"""

import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

import addon_utils
import bpy
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "blender_addon"))
sys.path.insert(0, str(ROOT / "tests" / "python"))
sys.path.insert(0, str(Path(os.environ["KKBP_SRC"]) / "importer" / "tests"))

from bundle_builder import BundleBuilder  # noqa: E402  (KKBP's test tool)
from conftest import write_k2t  # noqa: E402
from kk2blender import build, kkbp_link, pose, space  # noqa: E402

for mod in addon_utils.modules():
    if mod.__name__.rpartition(".")[2] == "kkbp":
        addon_utils.enable(mod.__name__, default_set=True)
assert kkbp_link.available(), "KKBP isn't installed"

FRAMES = 6
S = np.diag([-1.0, 1, 1, 1])


def gltf_world(bones):
    by = {b["id"]: b for b in bones}
    out = {}

    def w(bid):
        if bid in out:
            return out[bid]
        b = by[bid]
        local = space.trs(b["translation"], b["rotation"])
        out[bid] = local if b["parent"] is None else w(b["parent"]) @ local
        return out[bid]

    for b in bones:
        w(b["id"])
    return out


def rot(axis, deg):
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    m = np.eye(4)
    i, j = {"x": (1, 2), "y": (2, 0), "z": (0, 1)}[axis]
    m[i, i], m[i, j], m[j, i], m[j, j] = c, -s, s, c
    return m


def make_export(tmp: Path):
    builder = BundleBuilder("KK", "Test Chara")
    bundle = builder.build(tmp / "meshes")
    bones = builder.bones
    by = {b["id"]: b for b in bones}

    # The game's rest in Unity space, root-relative, and each bone's local rest.
    world_unity = {k: S @ v @ S for k, v in gltf_world(bones).items()}
    local_unity = {b["id"]: (world_unity[b["id"]] if b["parent"] is None
                             else np.linalg.inv(world_unity[b["parent"]]) @ world_unity[b["id"]]) for b in bones}

    def path(bid):
        names = []
        b = by[bid]
        while b is not None:
            names.append(b["name"])
            if b["name"] == "cf_j_hips":
                names.append("helper_between")  # a transform KKBP leaves out
            b = by.get(b["parent"]) if b["parent"] else None
        return "BodyTop/" + "/".join(reversed(names))

    anim = {"cf_j_hips": ("y", 8.0), "cf_j_neck": ("x", -5.0), "cf_J_hairB_00": ("x", 12.0)}
    worlds = {}
    channels = []
    for f in range(FRAMES):
        for b in bones:
            local = local_unity[b["id"]]
            if b["id"] in anim:
                axis, deg = anim[b["id"]]
                local = local @ rot(axis, deg * f)
            worlds[(b["id"], f)] = local if b["parent"] is None else worlds[(b["parent"], f)] @ local
    for b in bones:
        seq = np.array([worlds[(b["id"], f)][:3, :].reshape(12) for f in range(FRAMES)])
        channels.append(("bone:" + path(b["id"]), "matrix", 0, seq, False))
    channels.append(("root", "matrix", 0, np.tile(np.eye(4)[:3, :].reshape(12), (FRAMES, 1)), True))

    # The eye set "笑い" (KKBP's "Eye Happy") opens over the clip; the shirt comes off at frame 3.
    eyes = np.array([[f / (FRAMES - 1), 1 - f / (FRAMES - 1)] for f in range(FRAMES)])
    channels.append(("face:eyes", "patterns", 0, eyes, False))
    channels.append(("face:mouth", "patterns", 0, np.tile([1.0, 0.0], (FRAMES, 1)), True))
    status = np.zeros((FRAMES, 12))
    channels.append(("status", "status", 0, status, True))
    shirt = np.array([[1.0 if f < 3 else 0.0] for f in range(FRAMES)])
    channels.append(("vis:BodyTop/ct_clothesTop/o_top_shirt", "bool", 0, shirt, False))
    channels.append(("vis:BodyTop/ct_clothesTop/o_top_shirt_alt", "bool", 0, np.zeros((FRAMES, 1)), True))
    channels.append(("vis:BodyTop/p_cf_body_00/o_body_a", "bool", 0, np.ones((FRAMES, 1)), True))

    (tmp / "tracks").mkdir()
    header = {
        "subject": "character",
        "renderers": {
            "BodyTop/ct_clothesTop/o_top_shirt": {"name": "o_top_shirt", "owner": "clothes:0"},
            "BodyTop/ct_clothesTop/o_top_shirt_alt": {"name": "o_top_shirt_alt", "owner": "clothes:0"},
            "BodyTop/p_cf_body_00/o_body_a": {"name": "o_body_a", "owner": "body"},
        },
        "eyesets": [{"id": 0, "name": "笑い", "pattern": 0, "gag": 0}],
    }
    write_k2t(tmp / "tracks" / "c00.k2t", FRAMES, channels, header)
    cam = [list(np.eye(4)[:3, :].reshape(12) + np.array([0, 0, 0, 0, 0, 0, 0, 1.0, 0, 0, 0, -3])) +
           [30.0, 0.1, 100, 0, 5] for _ in range(FRAMES)]
    write_k2t(tmp / "tracks" / "scene.k2t", FRAMES, [("camera", "camera", 0, np.array(cam), True)], {"lights": {}})
    write_k2t(tmp / "tracks" / "time.k2t", FRAMES, [("time", "seconds", 0, np.arange(FRAMES)[:, None] / 30.0, False)])
    scene = {
        "format": "kk2blender-scene", "version": 1, "complete": True, "screen": [1920, 1080],
        "characters": [{"id": "c00", "key": 1, "name": "Test Chara", "card": "cards/c00.png",
                        "tracks": "tracks/c00.k2t", "coordinate": 0}],
        "items": [], "map": None,
        "kkbp": {"cards": "cards", "output": "meshes"},
        "bake": {"fps": 30, "frame_count": FRAMES, "tracks": {"scene": "tracks/scene.k2t", "time": "tracks/time.k2t"}},
    }
    (tmp / "scene.json").write_text(json.dumps(scene, ensure_ascii=False), encoding="utf-8")
    return bundle, worlds, world_unity, builder


def main():
    bpy.ops.wm.read_homefile(use_empty=True)
    tmp = Path(tempfile.mkdtemp())
    bundle, worlds, rest_unity, builder = make_export(tmp)
    report = build.import_scene(tmp, "soft", "preview", import_characters=True)
    print("\n".join(report.lines))
    for w in report.warnings:
        print("warning:", w)
    assert any("animated" in line and "bones" in line for line in report.lines), report.lines

    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    scene = bpy.context.scene
    worst = 0.0
    checked = 0
    for f in range(FRAMES):
        scene.frame_set(1 + f)
        bpy.context.view_layer.update()
        for b in builder.bones:
            pb = arm.pose.bones.get(b["id"])
            if pb is None:
                continue
            skin = np.array(arm.matrix_world @ pb.matrix @ pb.bone.matrix_local.inverted())
            game = space.unity_to_blender(worlds[(b["id"], f)]) @ np.linalg.inv(space.unity_to_blender(rest_unity[b["id"]]))
            worst = max(worst, float(np.abs(skin - game).max()))
            checked += 1
    print(f"checked {checked} bone-frames, worst skinning error {worst:.2e}")
    assert checked > 0 and worst < 1e-4, worst

    # Expressions keyed.
    face = next(o for o in bpy.data.objects if o.type == "MESH" and o.data.shape_keys
                and "Eye Happy" in o.data.shape_keys.key_blocks)
    kb = face.data.shape_keys.key_blocks
    scene.frame_set(1)
    v0 = kb["Eye Happy"].value
    scene.frame_set(FRAMES)
    v1 = kb["Eye Happy"].value
    print("Eye Happy", v0, v1, "Mouth Default", kb["Mouth Default"].value)
    assert v0 < 0.01 and v1 > 0.99, (v0, v1)
    assert kb["Mouth Default"].value > 0.99

    # The shirt comes off at frame 3 (Blender frame 4).
    shirt = [o for o in bpy.data.objects if o.type == "MESH"
             and any(m and m.get("kkbp_id") == "ct_top_shirt" for m in o.data.materials)]
    assert shirt, [o.name for o in bpy.data.objects]
    scene.frame_set(2)
    assert not shirt[0].hide_render
    scene.frame_set(5)
    assert shirt[0].hide_render
    print("PASS kkbp integration")


try:
    main()
except Exception:
    traceback.print_exc()
    sys.exit(1)
