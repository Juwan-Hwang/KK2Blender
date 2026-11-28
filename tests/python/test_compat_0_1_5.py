"""Card compatibility fixes (add-on 0.1.5): blend shape weights per renderer."""

import numpy as np

from kk2blender import shapes


def test_clothing_shapes_with_the_same_name_keep_their_own_weights():
    renderers = {
        "BodyTop/ct_top/o_top": {"blend_shapes": ["Key 1", "Key 2"]},
        "BodyTop/ct_bot/o_bot": {"blend_shapes": ["Key 1"]},
        "BodyTop/p_cf_head/cf_O_face": {"blend_shapes": ["eye_face.f00_def_op"]},
        "BodyTop/no_shapes": {"name": "x"},
    }
    data = {
        "shape:BodyTop/ct_top/o_top": np.array([[100.0, 0.0], [100.0, 50.0]]),
        "shape:BodyTop/ct_bot/o_bot": np.array([[30.0], [30.0]]),
        "shape:BodyTop/p_cf_head/cf_O_face": np.array([[0.0], [np.nan]]),
    }
    per = shapes.per_renderer(renderers, data.get)
    assert set(per) == {"BodyTop/ct_top/o_top", "BodyTop/ct_bot/o_bot", "BodyTop/p_cf_head/cf_O_face"}
    assert per["BodyTop/p_cf_head/cf_O_face"]["raw:eye_face.f00_def_op"].tolist() == [0.0, 0.0]  # NaN -> 0

    everywhere = shapes.by_name(per)
    assert everywhere["raw:Key 1"].tolist() == [1.0, 1.0]  # the top's moves most

    bottom = shapes.for_object(["BodyTop/ct_bot/o_bot"], per, everywhere)
    assert bottom["raw:Key 1"].tolist() == [0.3, 0.3]  # its own, not the top's
    assert bottom["raw:Key 2"].tolist() == [0.0, 0.5]  # names it lacks fall back
    top = shapes.for_object(["BodyTop/ct_top/o_top", "missing/path"], per, everywhere)
    assert top["raw:Key 1"].tolist() == [1.0, 1.0]


def test_a_width_mismatch_is_skipped():
    per = shapes.per_renderer({"a": {"blend_shapes": ["x", "y"]}}, lambda ch: np.zeros((3, 1)))
    assert per == {}
    assert shapes.per_renderer({"a": {"blend_shapes": ["x"]}}, lambda ch: None) == {}
