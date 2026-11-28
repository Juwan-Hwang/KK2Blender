"""Studio item Alpha (add-on 0.1.6): an item at Alpha 0 draws nothing in the game."""

import json
import struct

import numpy as np

from kk2blender import item_alpha


def _glb(path, materials):
    j = json.dumps({"asset": {"version": "2.0"}, "materials": materials}).encode()
    j += b" " * ((4 - len(j) % 4) % 4)
    path.write_bytes(struct.pack("<III", 0x46546C67, 2, 20 + len(j)) + struct.pack("<II", len(j), 0x4E4F534A) + j)


def test_old_exports_fade_their_blended_materials(tmp_path):
    # Export kk2b_20261129_001415: the Studio plane "p_koi_stu_kihon14_00" at Alpha 0 in front of the camera.
    p = tmp_path / "item_1007.glb"
    _glb(p, [{"name": "m_koi_stu_kihon01_02 (Instance)", "alphaMode": "BLEND"},
             {"name": "solid", "alphaMode": "OPAQUE"}])
    driven = item_alpha.driven_material_names(item_alpha.glb_materials(p), item_alpha.marks_item_alpha("0.1.5"))
    assert driven == {"m_koi_stu_kihon01_02 (Instance)"}
    track = np.zeros((4, 13))
    alpha = item_alpha.alpha_per_frame(track, 0)
    assert alpha.tolist() == [0, 0, 0, 0]
    hide, fade = item_alpha.plan(alpha, ["m_koi_stu_kihon01_02 (Instance).001"], driven)
    assert hide.all() and fade is not None
    # The same item with an opaque part as well: the part still draws, so only the blended one fades.
    hide, fade = item_alpha.plan(alpha, ["m_koi_stu_kihon01_02 (Instance)", "solid"], driven)
    assert hide is None and fade is not None


def test_new_exports_follow_the_marks_and_the_per_frame_alpha(tmp_path):
    p = tmp_path / "item.glb"
    _glb(p, [{"name": "fade", "alphaMode": "BLEND", "extras": {"kk2b_item_alpha": True}},
             {"name": "glass", "alphaMode": "BLEND"}])
    assert item_alpha.marks_item_alpha("0.1.6") and item_alpha.marks_item_alpha("1.0")
    driven = item_alpha.driven_material_names(item_alpha.glb_materials(p), True)
    assert driven == {"fade"}
    track = np.zeros((3, 14))
    track[:, 13] = [1.0, 0.5, np.nan]
    alpha = item_alpha.alpha_per_frame(track, 0.0)  # the track wins over scene.json
    assert alpha.tolist() == [1.0, 0.5, 1.0]
    hide, fade = item_alpha.plan(alpha, ["fade"], driven)
    assert hide is None and fade.tolist() == [1.0, 0.5, 1.0]
    track[:, 13] = [1.0, 0.0, 1.0]
    hide, _ = item_alpha.plan(item_alpha.alpha_per_frame(track, 1), ["fade"], driven)
    assert hide.tolist() == [False, True, False]


def test_items_at_full_alpha_or_without_alpha_materials_are_left_alone(tmp_path):
    full = item_alpha.alpha_per_frame(np.zeros((2, 13)), 1)
    assert item_alpha.plan(full, ["fade"], {"fade"}) == (None, None)
    assert item_alpha.plan(np.zeros(2), ["solid"], {"fade"}) == (None, None)
    assert item_alpha.plan(np.zeros(2), [], {"fade"}) == (None, None)
    assert item_alpha.alpha_per_frame(np.zeros((2, 13)), None).tolist() == [1.0, 1.0]
