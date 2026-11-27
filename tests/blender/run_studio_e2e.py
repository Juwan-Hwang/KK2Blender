"""End to end on our own Studio-side bundle: the sample bundle the C# writer makes (kk_plugin/tests)
plus a bake written around it the way the plugin does, through Import All with the real KKBP
importer. Checks that bones (matched by the bundle's kk2blender.json, not by name) follow the bake
exactly, and that the game's raw blend shapes are driven.

    K2T_SAMPLE_DIR=/tmp/k2t blender -b --python-exit-code 1 --python tests/blender/run_studio_e2e.py
"""

import json
import os
import shutil
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
from conftest import write_k2t  # noqa: E402
from kk2blender import build, kkbp_link, space  # noqa: E402

for mod in addon_utils.modules():
    if mod.__name__.rpartition(".")[2] == "kkbp":
        addon_utils.enable(mod.__name__, default_set=True)
assert kkbp_link.available(), "KKBP isn't installed"

FRAMES = 4
S = np.diag([-1.0, 1, 1, 1])


def rot_y(deg):
    a = np.radians(deg)
    m = np.eye(4)
    m[0, 0], m[0, 2], m[2, 0], m[2, 2] = np.cos(a), np.sin(a), -np.sin(a), np.cos(a)
    return m


def main():
    src = sorted((Path(os.environ.get("K2T_SAMPLE_DIR", "/tmp/k2t")) / "bundle").glob("*_Sample Chara"))[-1]
    tmp = Path(tempfile.mkdtemp())
    shutil.copytree(src, tmp / "meshes" / src.name, ignore=shutil.ignore_patterns("kkbp_cache"))
    bundle = tmp / "meshes" / src.name
    skel = json.loads((bundle / "skeleton.json").read_text(encoding="utf-8"))["bones"]
    kmap = json.loads((bundle / "kk2blender.json").read_text(encoding="utf-8"))
    by = {b["id"]: b for b in skel}

    world_g = {}

    def wg(bid):
        if bid not in world_g:
            b = by[bid]
            local = space.trs(b["translation"], b["rotation"])
            world_g[bid] = local if b["parent"] is None else wg(b["parent"]) @ local
        return world_g[bid]

    rest_u = {bid: S @ wg(bid) @ S for bid in by}
    # Animate every bone's local rotation a little, in the game's (Unity) space.
    worlds = {}
    for f in range(FRAMES):
        for b in skel:
            parent = b["parent"]
            local = rest_u[b["id"]] if parent is None else np.linalg.inv(rest_u[parent]) @ rest_u[b["id"]]
            local = local @ rot_y(6.0 * f)
            worlds[(b["id"], f)] = local if parent is None else worlds[(parent, f)] @ local
    channels = []
    for bid, info in kmap["bones"].items():
        if bid not in by:
            continue
        seq = np.array([worlds[(bid, f)][:3, :].reshape(12) for f in range(FRAMES)])
        channels.append(("bone:" + info["path"], "matrix", 0, seq, False))
    channels.append(("root", "matrix", 0, np.tile(np.eye(4)[:3, :].reshape(12), (FRAMES, 1)), True))
    renderers = {}
    face = next(m for m in kmap["meshes"].values() if m.get("blend_shapes"))
    n = len(face["blend_shapes"])
    weights = np.array([[100.0 * f / (FRAMES - 1)] + [0.0] * (n - 1) for f in range(FRAMES)])
    channels.append(("shape:" + face["path"], "weights", 0, weights, False))
    renderers[face["path"]] = {"name": face["name"], "owner": "head", "blend_shapes": face["blend_shapes"]}
    for m in kmap["meshes"].values():
        renderers.setdefault(m["path"], {"name": m["name"], "owner": "body"})
        channels.append(("vis:" + m["path"], "bool", 0, np.ones((FRAMES, 1)), True))
    channels.append(("status", "status", 0, np.zeros((FRAMES, 12)), True))
    (tmp / "tracks").mkdir()
    write_k2t(tmp / "tracks" / "c00.k2t", FRAMES, channels, {"renderers": renderers, "eyesets": []})
    cam = [list(np.eye(4)[:3, :].reshape(12)) + [30.0, 0.1, 100, 0, 5]] * FRAMES
    write_k2t(tmp / "tracks" / "scene.k2t", FRAMES, [("camera", "camera", 0, np.array(cam), True)], {"lights": {}})
    write_k2t(tmp / "tracks" / "time.k2t", FRAMES, [("time", "seconds", 0, np.zeros((FRAMES, 1)), False)])
    name = json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))["character_name"]
    scene = {"format": "kk2blender-scene", "version": 1, "complete": True, "screen": [1920, 1080],
             "characters": [{"id": "c00", "name": name, "tracks": "tracks/c00.k2t", "coordinate": 0}],
             "items": [], "map": None, "kkbp": {"output": "meshes"},
             "bake": {"fps": 30, "frame_count": FRAMES,
                      "tracks": {"scene": "tracks/scene.k2t", "time": "tracks/time.k2t"}}}
    (tmp / "scene.json").write_text(json.dumps(scene, ensure_ascii=False), encoding="utf-8")

    bpy.ops.wm.read_homefile(use_empty=True)
    report = build.import_scene(tmp, "soft", "preview")
    print("\n".join(report.lines + report.warnings))
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    sc = bpy.context.scene
    worst, checked = 0.0, 0
    for f in range(FRAMES):
        sc.frame_set(1 + f)
        bpy.context.view_layer.update()
        for pb in arm.pose.bones:
            if pb.name not in by:
                continue
            skin = np.array(arm.matrix_world @ pb.matrix @ pb.bone.matrix_local.inverted())
            gs = by[pb.name].get("game_scale", [1, 1, 1])
            game = space.unity_to_blender(worlds[(pb.name, f)] @ np.diag(list(1 / np.array(gs)) + [1])) \
                @ np.linalg.inv(space.unity_to_blender(rest_u[pb.name]))
            worst = max(worst, float(np.abs(skin - game).max()))
            checked += 1
    print(f"checked {checked} bone-frames, worst skinning error {worst:.2e}")
    assert checked > 0 and worst < 1e-4, worst
    key = f"raw:{face['blend_shapes'][0]}"
    obj = next(o for o in bpy.data.objects if o.type == "MESH" and o.data.shape_keys and key in o.data.shape_keys.key_blocks)
    sc.frame_set(FRAMES)
    assert obj.data.shape_keys.key_blocks[key].value > 0.99
    print("PASS studio e2e")


try:
    main()
except Exception:
    traceback.print_exc()
    sys.exit(1)
