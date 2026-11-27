"""Checks the KKBP Export Bundle the plugin's C# writer makes (kk_plugin/tests, WritesASampleBundle)
with KKBP's own bpy-free bundle reader, and the .glb files against the contract's rules."""

import json
import os
import struct
import sys
import types
from pathlib import Path

import numpy as np
import pytest

SAMPLES = Path(os.environ.get("K2T_SAMPLE_DIR", "/tmp/k2t")) / "bundle"
KKBP_SRC = Path(os.environ.get("KKBP_SRC", "/workspace/work/kkbp"))


def _bundle_dir():
    found = sorted(SAMPLES.glob("*_Sample Chara")) if SAMPLES.is_dir() else []
    if not found:
        pytest.skip("run the C# tests first (dotnet test kk_plugin/tests) to write the sample bundle")
    return found[-1]


def _kkbp_bundle():
    pkg_dir = KKBP_SRC / "importer" / "kkbp"
    if not (pkg_dir / "bundle" / "reader.py").is_file():
        pytest.skip(f"KKBP's importer source isn't at {KKBP_SRC} (set KKBP_SRC)")
    if "kkbp" not in sys.modules:
        # KKBP's package __init__ needs bpy; its bundle package doesn't, so load only that.
        pkg = types.ModuleType("kkbp")
        pkg.__path__ = [str(pkg_dir)]
        sys.modules["kkbp"] = pkg
    import kkbp.bundle as bundle  # noqa: E402
    assert "bpy" not in sys.modules
    return bundle


def _glb(path):
    data = path.read_bytes()
    magic, version, total = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2 and total == len(data)
    jlen, jtype = struct.unpack_from("<II", data, 12)
    gltf = json.loads(data[20:20 + jlen])
    blen, _ = struct.unpack_from("<II", data, 20 + jlen)
    binary = data[28 + jlen:28 + jlen + blen]
    return gltf, binary


_TYPES = {5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "u1"}
_WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def _accessor(gltf, binary, i):
    a = gltf["accessors"][i]
    v = gltf["bufferViews"][a["bufferView"]]
    dt = np.dtype(_TYPES[a["componentType"]])
    n = a["count"] * _WIDTH[a["type"]]
    arr = np.frombuffer(binary, dtype=dt, count=n, offset=v["byteOffset"] + a.get("byteOffset", 0))
    return arr.reshape(a["count"], _WIDTH[a["type"]])


def test_kkbp_reader_accepts_the_sample_bundle():
    bundle = _kkbp_bundle()
    b = bundle.open_bundle(_bundle_dir())
    assert b.game == "KK"
    assert b.manifest.schema_version == "1.3"
    assert [p.kind for p in b.passes] == ["body", "outfit"]
    raw = [e for e in b.expressions.records if e.target.startswith("raw:")]
    assert raw and all(e.category == "other" and e.name is None and e.game_name == e.target[4:] for e in raw)
    assert b.skeleton.by_role("body_root") is not None
    assert b.passes[1].body_textures[0].texture.raw == "_AlphaMask"
    assert any(m.alternate for m in b.passes[1].meshes)
    assert not b.warnings


def test_glb_files_follow_the_contract():
    folder = _bundle_dir()
    skeleton = {bone["id"]: bone for bone in json.loads((folder / "skeleton.json").read_text())["bones"]}
    for glb in [folder / "body.glb", folder / "outfits" / "00" / "outfit.glb"]:
        gltf, binary = _glb(glb)
        for kind in ("nodes", "meshes", "materials"):
            for item in gltf.get(kind, []):
                assert item["extras"]["id"] == item["name"]
        names = {n["name"] for n in gltf["nodes"]}
        assert set(skeleton) >= {n["name"] for n in gltf["nodes"] if "mesh" not in n}
        for n in gltf["nodes"]:
            if "mesh" in n:
                assert "skin" in n and not any(k in n for k in ("translation", "rotation", "scale", "matrix"))
            elif n["name"] in skeleton:
                bone = skeleton[n["name"]]
                assert np.allclose(n["translation"], bone["translation"]) and np.allclose(n["rotation"], bone["rotation"])
        for mesh in gltf["meshes"]:
            for prim in mesh["primitives"]:
                w = _accessor(gltf, binary, prim["attributes"]["WEIGHTS_0"])
                assert np.allclose(w.sum(axis=1), 1.0, atol=1e-6)
                normals = _accessor(gltf, binary, prim["attributes"]["NORMAL"])
                assert np.allclose(np.linalg.norm(normals, axis=1), 1.0, atol=1e-5)
                assert len(prim.get("targets", [])) == len(mesh["extras"].get("targetNames", []))
        assert names


def test_raw_targets_keep_the_game_order_and_the_map_names_paths():
    folder = _bundle_dir()
    gltf, _ = _glb(folder / "body.glb")
    face = next(m for m in gltf["meshes"] if m["name"] == "cf_O_face")
    raw = [t for t in face["extras"]["targetNames"] if t.startswith("raw:")]
    assert raw == ["raw:eye_face.f00_def_op", "raw:eye_face.f00_def_cl"]
    mapping = json.loads((folder / "kk2blender.json").read_text())
    assert mapping["meshes"]["cf_O_face"]["path"] == "BodyTop/p_cf_body_00/cf_O_face"
    assert mapping["meshes"]["cf_O_face"]["blend_shapes"] == ["eye_face.f00_def_op", "eye_face.f00_def_cl"]
    assert mapping["bones"]["cf_j_hips"]["path"] == "BodyTop/cf_n_height/cf_j_hips"
    outfit = json.loads((folder / "outfits" / "00" / "outfit.json").read_text())
    cloth = next(m for m in outfit["meshes"] if m["extensions"]["kk"].get("cloth"))
    assert cloth["extensions"]["kk"]["source_vertices"] == list(range(8))
