import os
from pathlib import Path

import numpy as np
import pytest

from conftest import write_k2t
from kk2blender import k2t


def test_reads_constant_late_and_full(tmp_path):
    p = tmp_path / "a.k2t"
    write_k2t(p, 4, [
        ("still", "x", 0, np.tile([[1.0, 2.0]], (4, 1)), True),
        ("late", "x", 2, np.array([[5.0], [6.0]]), False),
    ], header={"subject": "test"})
    t = k2t.read(p)
    assert t.frame_count == 4
    assert t.header["subject"] == "test"
    np.testing.assert_array_equal(t.full("still"), np.tile([[1.0, 2.0]], (4, 1)))
    late = t.full("late")
    assert np.isnan(late[:2]).all()
    np.testing.assert_array_equal(late[2:, 0], [5.0, 6.0])
    np.testing.assert_array_equal(t.present("late"), [False, False, True, True])
    assert t.names("la") == ["late"]


def test_rejects_other_files(tmp_path):
    p = tmp_path / "b.k2t"
    p.write_bytes(b"PNG....")
    with pytest.raises(k2t.TrackFileError):
        k2t.read(p)


def test_rejects_truncated_data(tmp_path):
    p = tmp_path / "c.k2t"
    write_k2t(p, 3, [("a", "x", 0, np.ones((3, 2)), False)])
    p.write_bytes(p.read_bytes()[:-4])
    with pytest.raises(k2t.TrackFileError):
        k2t.read(p)


def test_reads_the_plugins_own_file():
    """Cross-check with the C# writer: CI runs the C# tests with K2T_SAMPLE_DIR first."""
    d = os.environ.get("K2T_SAMPLE_DIR")
    if not d or not (Path(d) / "sample.k2t").exists():
        pytest.skip("no sample from the C# tests")
    t = k2t.read(Path(d) / "sample.k2t")
    assert t.frame_count == 3
    assert t.header["text"] == 'ü "quoted"'
    m = t.full("bone:a/b")
    np.testing.assert_array_equal(m[:, 3], [0, 1, 2])
    assert t.channels["still"].constant
    np.testing.assert_array_equal(t.full("still")[:, 0], [1, 1, 1])
    late = t.full("late")
    assert np.isnan(late[0]).all()
    np.testing.assert_array_equal(late[1:], [[1, -1], [2, -2]])


def test_plugin_glb_is_valid():
    """The C# glTF writer's sample: a valid GLB 2.0 with one mesh, one material, one image."""
    import json
    import struct
    d = os.environ.get("K2T_SAMPLE_DIR")
    if not d or not (Path(d) / "sample.glb").exists():
        pytest.skip("no sample from the C# tests")
    data = (Path(d) / "sample.glb").read_bytes()
    magic, version, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2 and length == len(data)
    jlen, jtype = struct.unpack_from("<II", data, 12)
    assert jtype == 0x4E4F534A
    gltf = json.loads(data[20:20 + jlen])
    assert gltf["asset"]["version"] == "2.0"
    assert len(gltf["meshes"]) == 1 and len(gltf["materials"]) == 1 and len(gltf["images"]) == 1
    blen, btype = struct.unpack_from("<II", data, 20 + jlen)
    assert btype == 0x004E4942 and blen == gltf["buffers"][0]["byteLength"]
    for view in gltf["bufferViews"]:
        assert view["byteOffset"] + view["byteLength"] <= blen
