"""Fixes from the real exports of 2026-11-27 (add-on 0.1.4)."""

import sys
import types

import numpy as np

from kk2blender import outlines, rigs, visibility


class Mat(dict):
    def __init__(self, name, method, **props):
        super().__init__(props)
        self.name = name
        self.surface_render_method = method


def test_blended_outlines_draw_dithered():
    cloth = Mat("cloth", "BLENDED", kkbp_id="cloth")
    outline = Mat("Outline cloth", "BLENDED", kkbp_outline="cloth")
    shared = Mat("Outline cloth.001", "BLENDED", kkbp_outline="cloth")
    body_outline = Mat("Outline cf_m_body", "DITHERED", kkbp_outline="cf_m_body")
    fixed = outlines.fix_render_method([cloth, outline, shared, outline, body_outline, None])
    assert fixed == ["Outline cloth", "Outline cloth.001"]
    assert cloth.surface_render_method == "BLENDED"  # the cloth itself keeps its blending
    assert outline.surface_render_method == shared.surface_render_method == "DITHERED"
    assert body_outline.surface_render_method == "DITHERED"


def test_parts_of_a_joined_object_hide_on_their_own():
    n = 4
    vis = {
        "face": np.array([1, 1, 1, 1], bool),
        "eye_white_unused": np.zeros(n, bool),  # a mod face's: the game never draws it
        "teeth": np.array([0, 1, 1, 0], bool),
    }
    shown, parts = visibility.object_and_parts(["face", "eye_white_unused", "teeth", "unknown"], vis, n)
    assert shown.all()
    assert parts["eye_white_unused"].all()
    assert parts["teeth"].tolist() == [True, False, False, True]
    assert "face" not in parts


def test_an_object_hidden_as_a_whole_has_no_hidden_parts():
    vis = {"cloth": np.array([0, 0, 1], bool)}
    shown, parts = visibility.object_and_parts(["cloth"], vis, 3)
    assert shown.tolist() == [False, False, True]
    assert parts == {}
    assert visibility.object_and_parts(["x"], vis, 3) == (None, {})


def _item_rows(scale):
    rows = np.zeros((3, 13))
    rows[:, 0], rows[:, 5], rows[:, 10] = scale
    rows[:, 12] = 1
    return rows


def test_handles_keep_the_shapes_size():
    assert rigs.handle_sized(_item_rows((1, 1, 1)))
    assert rigs.handle_sized(_item_rows((0.2, 0.2, 0.2)))
    assert not rigs.handle_sized(_item_rows((1.8, 1.8, 3.77)))  # a stretched sphere is a prop
    assert not rigs.handle_sized(_item_rows((2, 2, 2)))


class _Tracks(dict):
    def full(self, name):
        return self[name]


def test_rig_markers_skip_stretched_shapes():
    moving = np.zeros((3, 13))
    moving[:, 0] = moving[:, 5] = moving[:, 10] = 1
    moving[:, 3] = [0, 1, 2]
    tracks = _Tracks(f_root=moving, f_joint=moving, handle=_item_rows((1, 1, 1)), prop=_item_rows((1.8, 1.8, 3.8)))
    shape = {"bundle": "studio/item_shape.unity3d", "file": "item_sphere"}
    folder = types.SimpleNamespace(scene_tracks=tracks, scene={
        "folders": [{"key": 1, "track": "f_root"}, {"key": 2, "parent": 1, "track": "f_joint"}],
        "items": [{"key": 10, "parent": 2, "track": "handle", "asset": shape},
                  {"key": 11, "parent": 2, "track": "prop", "asset": shape}],
    })
    assert rigs.rig_markers(folder) == {10}


def test_point_caches_fall_back_when_the_folder_cant_be_written(tmp_path, monkeypatch):
    sys.modules.setdefault("bpy", types.ModuleType("bpy"))
    from kk2blender import cloth
    blocked = tmp_path / "blocked"
    blocked.write_text("a file where the cache folder should be")
    monkeypatch.setattr("tempfile.gettempdir", lambda: str(tmp_path / "tmp"))
    frames = np.zeros((2, 3, 3), np.float32)
    path = cloth.write_cache(blocked, "prop_1.pc2", frames, 1.0)
    assert path == tmp_path / "tmp" / "kk2blender_cache" / "prop_1.pc2"
    assert path.read_bytes().startswith(b"POINTCACHE2\0")
    ok = cloth.write_cache(tmp_path / "cache", "prop_1.pc2", frames, 1.0)
    assert ok == tmp_path / "cache" / "prop_1.pc2"
