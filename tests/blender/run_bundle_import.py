"""Imports the sample bundle the plugin's C# writer makes (kk_plugin/tests, WritesASampleBundle) with the
real KKBP importer in Blender and checks what the Blender side relies on: the import succeeds, the
"raw:" shape keys arrive with their names, the bones keep the rest pose of skeleton.json, and the alternate
mesh stays its own object.

    K2T_SAMPLE_DIR=/tmp/k2t blender -b --factory-startup --python-exit-code 1 --python tests/blender/run_bundle_import.py

KKBP must be installed as an extension; the script enables it.
"""

import json
import os
import sys
import traceback
from pathlib import Path

import addon_utils
import bpy
from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "blender_addon"))
from kk2blender import kkbp_link  # noqa: E402

for mod in addon_utils.modules():
    if mod.__name__.rpartition(".")[2] == "kkbp":
        addon_utils.enable(mod.__name__, default_set=True)
assert kkbp_link.available(), "KKBP isn't installed"


def bundle_dir():
    root = Path(os.environ.get("K2T_SAMPLE_DIR", "/tmp/k2t")) / "bundle"
    found = sorted(root.glob("*_Sample Chara"))
    assert found, f"no sample bundle under {root}; run the C# tests first"
    return found[-1]


def main():
    folder = bundle_dir()
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    ch, result = kkbp_link.import_character(folder)
    print("KKBP steps:", [(s.name, s.status) for s in result.steps if s.status != "ran"])

    keys = set()
    for obj in ch.parts():
        if obj.type == "MESH" and obj.data.shape_keys:
            keys |= {k.name for k in obj.data.shape_keys.key_blocks}
    for name in ("raw:eye_face.f00_def_op", "raw:eye_face.f00_def_cl", "raw:sk_open", "Eye Default"):
        assert name in keys, f"shape key {name} is missing; have {sorted(keys)}"

    # Each bone's head is where skeleton.json puts it (glTF +Y up -> Blender +Z up).
    skeleton = json.loads((folder / "skeleton.json").read_text())["bones"]
    by_id = {b["id"]: b for b in skeleton}
    world = {}

    def w(bid):
        if bid not in world:
            b = by_id[bid]
            local = Matrix.Translation(b["translation"]) @ Quaternion((b["rotation"][3], *b["rotation"][:3])).to_matrix().to_4x4()
            world[bid] = local if b["parent"] is None else w(b["parent"]) @ local
        return world[bid]

    arm = ch.armature
    to_blender = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
    checked = 0
    for bone in arm.data.bones:
        if bone.name in by_id:
            want = (to_blender @ w(bone.name)).translation
            assert (bone.head_local - want).length < 1e-3, f"{bone.name}: {bone.head_local} != {want}"
            checked += 1
    assert checked >= 3, "no bones were checked"
    # Every vertex says which game mesh and vertex it came from, through KKBP's joins and welds.
    mapping = json.loads((folder / "kk2blender.json").read_text())
    ordinal = {m["ordinal"]: m for m in mapping["meshes"].values()}
    for obj in ch.parts():
        if obj.type != "MESH":
            continue
        mesh_attr, vertex_attr = obj.data.attributes.get("_KK_MESH"), obj.data.attributes.get("_KK_VERTEX")
        assert mesh_attr is not None and vertex_attr is not None, f"{obj.name} lost the source attributes"
        for m, v in zip(mesh_attr.data, vertex_attr.data):
            src = ordinal[round(m.value)]
            assert 0 <= round(v.value) < src["vertices"]
    alts = [o for o in ch.parts() if o.type == "MESH" and "o_top_b" in o.name]
    assert alts, "the alternate mesh isn't its own object"
    print(f"OK: {checked} bones, {len(keys)} shape keys")


try:
    main()
except Exception:
    traceback.print_exc()
    sys.exit(1)
