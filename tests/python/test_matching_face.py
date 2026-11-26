import json

import numpy as np

from kk2blender import face, matching, scenefile


def test_unique_leaf_matches():
    m = matching.match_bones([("cf_j_hips", "cf_j_hips", None)], ["BodyTop/p_cf_body_bone/cf_j_root/cf_j_hips"])
    assert m["cf_j_hips"].path.endswith("cf_j_hips")


def test_duplicates_resolved_by_ancestors_with_helpers_skipped():
    paths = [
        "a/hair_front/helper/cf_j_bone",
        "a/acc_ribbon/helper/cf_j_bone",
    ]
    bones = [("ribbon", "acc_ribbon", None), ("bone2", "cf_j_bone", "ribbon")]
    m = matching.match_bones(bones, paths)
    assert m["bone2"].path == "a/acc_ribbon/helper/cf_j_bone"
    assert not m["bone2"].ambiguous


def test_allowed_narrows_to_the_worn_outfit():
    paths = ["x/o_top/b", "x/o_top/b#2"]
    m = matching.match_bones([("b", "b", None)], paths, {"b": {"x/o_top/b#2"}})
    assert m["b"].path == "x/o_top/b#2"


def test_no_candidate_is_left_out():
    assert matching.match_bones([("z", "z", None)], ["a/b"]) == {}


def rec(mesh, target, category, name=None, game_name=None):
    return face.Expression(mesh, target, category, name, game_name)


def test_pattern_keys_pair_open_and_closed():
    records = [
        rec("face", "Mouth Default", "mouth", "Mouth Default"),
        rec("face", "Mouth Default, Closed", "mouth", "Mouth Default, Closed"),
        rec("face", "Mouth Smiling", "mouth", "Mouth Smiling"),
        rec("face", "mouth 2", "mouth", None, "mouth 2"),
        rec("teeth", "Mouth Default", "mouth", "Mouth Default"),
    ]
    keys = face.pattern_keys(records, "face", "mouth")
    assert keys == [("Mouth Default", "Mouth Default, Closed"), ("Mouth Smiling", None), ("mouth 2", None)]


def test_pattern_weights_fold_missing_closed_into_open():
    ch = np.array([[0.6, 0.4, 0.0, 0.0], [0.0, 0.0, 0.3, 0.7]])
    w = face.pattern_weights(ch, [("A", "A, Closed"), ("B", None)])
    np.testing.assert_allclose(w["A"], [0.6, 0.0])
    np.testing.assert_allclose(w["A, Closed"], [0.4, 0.0])
    np.testing.assert_allclose(w["B"], [0.0, 1.0])


def test_eye_weights_follow_the_eyeset_in_use():
    eyesets = [{"id": 0, "name": "Default", "pattern": 0, "gag": 0},
               {"id": 1, "name": "Closed", "pattern": 1, "gag": 0},
               {"id": 30, "name": "Spiral", "pattern": 0, "gag": 2}]
    records = [rec("face", "Eye Default", "eye", "Eye Default", "Default"),
               rec("face", "Eye Closed", "eye", "Eye Closed", "Closed"),
               rec("face", "Gag Spiral", "gag", "Gag Spiral", "Spiral")]
    # Two frames: default eyes half blinking, then the spiral gag eyes fully open.
    ch = np.array([[0.5, 0.5, 0, 0], [1.0, 0.0, 0, 0]])
    eyeset = np.array([0, 30])
    w = face.eye_weights(ch, eyeset, eyesets, records, "face")
    np.testing.assert_allclose(w["Eye Default"], [0.5, 0.0])
    np.testing.assert_allclose(w["Gag Spiral"], [0.0, 1.0])
    np.testing.assert_allclose(w["Eye Closed"], [0.5, 0.0])


def test_tears():
    status = np.zeros((3, len(face.STATUS)))
    status[:, face.STATUS.index("tears_level")] = [0, 2, 1]
    records = [rec("face", "Tears 1", "tears"), rec("face", "Tears 2", "tears")]
    w = face.tear_weights(status, records, "face")
    np.testing.assert_array_equal(w["Tears 1"], [0, 0, 1])
    np.testing.assert_array_equal(w["Tears 2"], [0, 1, 0])


def _scene(tmp_path, chars, kkbp=None):
    (tmp_path / "tracks").mkdir()
    from conftest import write_k2t
    write_k2t(tmp_path / "tracks" / "time.k2t", 1, [("time", "s", 0, np.zeros((1, 1)), False)])
    write_k2t(tmp_path / "tracks" / "scene.k2t", 1, [])
    scene = {"format": "kk2blender-scene", "version": 1, "complete": True, "characters": chars,
             "bake": {"fps": 30, "frame_count": 1, "tracks": {"time": "tracks/time.k2t", "scene": "tracks/scene.k2t"}}}
    if kkbp:
        scene["kkbp"] = kkbp
    (tmp_path / "scene.json").write_text(json.dumps(scene))


def _bundle(root, folder, name):
    d = root / "meshes" / folder
    d.mkdir(parents=True)
    (d / "manifest.json").write_text(json.dumps({"character_name": name}))
    return d


def test_bundles_by_name_in_folder_order(tmp_path):
    _scene(tmp_path, [{"id": "c00", "name": "Ann B"}, {"id": "c01", "name": "Ann B"}, {"id": "c02", "name": "Cy"}],
           {"output": "meshes"})
    a1 = _bundle(tmp_path, "20261127010101_Ann_B", "Ann B")
    a2 = _bundle(tmp_path, "20261127010102_Ann_B", "Ann  B")
    c = _bundle(tmp_path, "20261127010103_Cy", "Cy")
    f = scenefile.open_folder(tmp_path)
    assert f.bundles == {"c00": a1, "c01": a2, "c02": c}


def test_bundles_from_kkbp_result(tmp_path):
    a = _bundle(tmp_path, "x_A", "Zed")
    result = tmp_path / "result.json"
    result.write_text(json.dumps({"state": "succeeded", "bundles": [str(a)], "failures": []}))
    _scene(tmp_path, [{"id": "c00", "name": "A"}], {"output": "meshes", "result": str(result)})
    assert scenefile.open_folder(tmp_path).bundles == {"c00": a}


def test_missing_meshes_are_a_note(tmp_path):
    _scene(tmp_path, [{"id": "c00", "name": "A"}], {"output": "meshes"})
    f = scenefile.open_folder(tmp_path)
    assert f.bundles == {} and f.notes


def test_incomplete_export_is_refused(tmp_path):
    (tmp_path / "scene.json").write_text(json.dumps({"format": "kk2blender-scene", "version": 1}))
    import pytest
    with pytest.raises(scenefile.ExportFolderError):
        scenefile.open_folder(tmp_path)
