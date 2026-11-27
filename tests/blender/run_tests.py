"""Tests that need Blender. Run headless:

    blender -b --factory-startup --python-exit-code 1 --python tests/blender/run_tests.py

They build small scenes by hand (no game, no KKBP) and check the add-on's Blender side: Actions and
F-curves on Blender 5's slotted Actions, the pose a bake produces, camera, lights, presets.
"""

import json
import math
import sys
import tempfile
import traceback
from pathlib import Path
from types import SimpleNamespace

import bpy
import numpy as np
from mathutils import Matrix

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "blender_addon"))
sys.path.insert(0, str(ROOT / "tests" / "python"))

import kk2blender  # noqa: E402
from kk2blender import anim, build, character, lighting, pose, render_settings, space  # noqa: E402
from conftest import write_glb_lightmapped_quad, write_glb_triangle, write_k2t  # noqa: E402

kk2blender.register()


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def translate(x, y, z):
    m = np.eye(4)
    m[:3, 3] = (x, y, z)
    return m


def rot_z(deg):
    a = math.radians(deg)
    m = np.eye(4)
    m[:2, :2] = [[math.cos(a), -math.sin(a)], [math.sin(a), math.cos(a)]]
    return m


def rows(m):
    return m[:3, :].reshape(12)


def test_curves_on_slotted_actions():
    reset()
    obj = bpy.data.objects.new("o", None)
    bpy.context.scene.collection.objects.link(obj)
    c = anim.Curves(obj, "test")
    frames = np.arange(1, 11, dtype=float)
    fc = c.curve("location", 0, frames, frames * 2.0, group="G")
    assert len(fc.keyframe_points) == 2, len(fc.keyframe_points)  # a straight line keeps its ends
    fc2 = c.curve("hide_render", 0, frames, (frames > 5).astype(float), interpolation="CONSTANT")
    assert [k.co[0] for k in fc2.keyframe_points] == [1.0, 6.0, 10.0], [k.co[0] for k in fc2.keyframe_points]
    bpy.context.scene.frame_set(4)
    assert abs(obj.location.x - 8.0) < 1e-5, obj.location.x
    bpy.context.scene.frame_set(7)
    assert obj.hide_render
    # A second Curves on the same object reuses its Action.
    c2 = anim.Curves(obj, "other")
    assert c2.action == c.action


def make_armature(ml):
    arm_data = bpy.data.armatures.new("arm")
    arm = bpy.data.objects.new("arm", arm_data)
    bpy.context.scene.collection.objects.link(arm)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    for name, (m, parent) in ml.items():
        eb = arm_data.edit_bones.new(name)
        eb.head = (0, 0, 0)
        eb.tail = (0, 0.1, 0)
        eb.matrix = Matrix(m.tolist())
        if parent:
            eb.parent = arm_data.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")
    return arm


def test_bones_follow_the_bake():
    """A two-bone chain: after apply_bones, Blender's skinning matrices equal the game's."""
    reset()
    rest_unity = {"hips": translate(0, 1.0, 0), "spine": translate(0, 0.25, 0)}
    s = np.diag([-1.0, 1, 1, 1])
    skel = []
    for name, parent in (("hips", None), ("spine", "hips")):
        g = s @ rest_unity[name] @ s
        skel.append(SimpleNamespace(id=name, name=name, parent=parent, translation=tuple(g[:3, 3]),
                                    rotation=(0.0, 0.0, 0.0, 1.0), game_scale=(1.0, 1.0, 1.0)))
    rest = pose.rest_matrices([pose.RestBone(b.id, b.name, b.parent, b.translation, b.rotation) for b in skel])
    # Blender bones with their own axes (as KKBP re-aims them).
    tilt = np.array(Matrix.Rotation(0.7, 4, "X"))
    arm = make_armature({"hips": (rest["hips"] @ tilt, None), "spine": (rest["spine"] @ tilt, "hips")})

    frames = 6
    tmp = Path(tempfile.mkdtemp())
    world = {}
    channels = []
    for name, parent in (("hips", None), ("spine", "hips")):
        seq = []
        for f in range(frames):
            local = rest_unity[name] @ rot_z(10 * f)
            m = local if parent is None else world[(parent, f)] @ local
            world[(name, f)] = m
            seq.append(rows(m))
        channels.append((f"bone:BodyTop/{'/'.join(['hips'] if name == 'hips' else ['hips', 'helper', 'spine'])}",
                         "matrix", 0, np.array(seq), False))
    channels.append(("root", "matrix", 0, np.tile(rows(np.eye(4)), (frames, 1)), True))
    write_k2t(tmp / "c.k2t", frames, channels, {"renderers": {}})
    from kk2blender import k2t
    tracks = k2t.read(tmp / "c.k2t")

    ch = SimpleNamespace(armature=arm, parts=lambda roles=None: [arm])
    bundle = SimpleNamespace(skeleton=SimpleNamespace(bones=skel), expressions=SimpleNamespace(records=()),
                             passes=(), materials=())
    log = []
    bake = character.CharacterBake(ch, bundle, tracks, 1, log.append)
    bake.apply_bones()
    bake.apply_root()
    assert any("animated 2 bones" in line for line in log), log

    scene = bpy.context.scene
    for f in range(frames):
        scene.frame_set(1 + f)
        bpy.context.view_layer.update()
        for name in ("hips", "spine"):
            pb = arm.pose.bones[name]
            skin = np.array(arm.matrix_world @ pb.matrix @ pb.bone.matrix_local.inverted())
            game = space.unity_to_blender(world[(name, f)]) @ np.linalg.inv(rest[name])
            assert np.allclose(skin, game, atol=1e-4), (name, f, skin, game)


def write_scene_folder(tmp: Path, frames=5):
    tracks = tmp / "tracks"
    tracks.mkdir(parents=True)
    cam = []
    for f in range(frames):
        m = translate(0, 1.2, -3 + 0.1 * f)  # in front of the origin, looking down Unity +Z
        cam.append(list(rows(m)) + [23.0 + f, 0.1, 100.0, 0.0, 5.0])
    light = []
    for f in range(frames):
        light.append(list(rows(np.eye(4))) + [1, 0.9, 0.8, 1.0, 10.0, 30.0, 1.0, 1.0])
    item = [list(rows(translate(1, 0, 0))) + [1.0 if f < 3 else 0.0] for f in range(frames)]
    write_k2t(tracks / "scene.k2t", frames, [
        ("camera", "camera", 0, np.array(cam), False),
        ("light:0", "light", 0, np.array(light), True),
        ("item:5", "matrix_vis", 0, np.array(item), False),
    ], {"lights": {"light:0": {"name": "Chara light", "type": "directional", "owner": "camera"}}})
    write_k2t(tracks / "time.k2t", frames, [("time", "seconds", 0, np.arange(frames)[:, None] / 30.0, False)])
    (tmp / "props").mkdir()
    write_glb_triangle(tmp / "props" / "item_5.glb", (2.0, 0.0, 0.0))
    scene = {"format": "kk2blender-scene", "version": 1, "complete": True, "screen": [1920, 1080],
             "characters": [], "items": [{"key": 5, "name": "Chair", "track": "item:5", "group": 0,
                                         "category": 1, "no": 2, "model": "props/item_5.glb"}],
             "map": None,
             "bake": {"fps": 30, "frame_count": frames,
                      "tracks": {"scene": "tracks/scene.k2t", "time": "tracks/time.k2t"}}}
    (tmp / "scene.json").write_text(json.dumps(scene))


def test_import_all_without_characters():
    reset()
    tmp = Path(tempfile.mkdtemp())
    write_scene_folder(tmp)
    report = build.import_scene(tmp, "soft", "preview", import_characters=False)
    scene = bpy.context.scene
    assert scene.frame_start == 1 and scene.frame_end == 5, (scene.frame_start, scene.frame_end)
    assert scene.render.fps == 30
    cam = scene.camera
    assert cam is not None and cam.data.sensor_fit == "VERTICAL"
    scene.frame_set(1)
    # Unity (0, 1.2, -3) is Blender (0, 3, 1.2); the camera looks down Unity +Z = Blender -Y.
    assert np.allclose(cam.matrix_world.translation, (0, 3, 1.2), atol=1e-4), cam.matrix_world.translation
    forward = cam.matrix_world.to_3x3() @ Matrix.Identity(3).col[2] * -1
    assert np.allclose(forward, (0, -1, 0), atol=1e-4), forward
    up = cam.matrix_world.to_3x3().col[1]
    assert np.allclose(up, (0, 0, 1), atol=1e-4), up
    fov = 2 * math.atan(cam.data.sensor_height / 2 / cam.data.lens)
    assert abs(math.degrees(fov) - 23.0) < 1e-3, math.degrees(fov)
    chair = bpy.data.objects["Chair"]
    # The item sits at Unity (1, 0, 0) = Blender (-1, 0, 0); its mesh node at glTF (2, 0, 0) inside it.
    tri = next(o for o in chair.children if o.type == "MESH")
    first = tri.matrix_world @ tri.data.vertices[0].co
    assert np.allclose(first, (1, 0, 0), atol=1e-4), first
    scene.frame_set(4)
    assert chair.hide_render
    assert bpy.data.objects.get(lighting.RIG_NAME) is not None
    assert bpy.data.objects.get(lighting.RIG_NAME).parent == cam
    assert scene.render.resolution_x == 1280 and scene.render.resolution_y == 720
    assert scene.view_settings.view_transform == "Standard"
    assert report.lines


def test_presets_switch():
    reset()
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    bpy.context.scene.collection.objects.link(cam)
    for pid in lighting.PRESETS:
        lighting.apply(pid, cam)
    for pid in render_settings.PRESETS:
        render_settings.apply(pid)
    suns = [o for o in bpy.data.objects if o.type == "LIGHT"]
    assert len(suns) == len(lighting.PRESETS["daylight"].suns), [o.name for o in suns]


def test_key_light_comes_from_upper_left():
    reset()
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    bpy.context.scene.collection.objects.link(cam)
    lighting.apply("soft", cam)
    bpy.context.view_layer.update()
    key = bpy.data.objects["KK2Blender Key"]
    d = key.matrix_world.to_3x3() @ Matrix.Identity(3).col[2] * -1  # direction the light travels
    # Camera at the origin looking down -Z: light from the upper left travels right (+X) and down (-Y).
    assert d.x > 0.2 and d.y < -0.2 and d.z < 0, d


def test_map_cameras_and_effects():
    reset()
    tmp = Path(tempfile.mkdtemp())
    frames = 3
    (tmp / "tracks").mkdir()
    (tmp / "map").mkdir()
    write_glb_lightmapped_quad(tmp / "map" / "part_000.glb")
    cam = [list(rows(translate(0, 1.2, -3))) + [30.0, 0.1, 100.0, 0.0, 5.0]] * frames
    target = [[0, 1.2, 0, 3.0 + f] for f in range(frames)]
    scam = [list(rows(translate(2, 1, 0))) + [1.0, 0.0]] * frames
    maprow = [list(rows(np.eye(4))) + [1.0]] * frames
    mlight = [list(rows(np.eye(4))) + [1, 1, 1, 1.0, 5.0, 30.0, 1.0, 1.0]] * frames
    write_k2t(tmp / "tracks" / "scene.k2t", frames, [
        ("camera", "camera", 0, np.array(cam), True),
        ("camera_target", "target", 0, np.array(target, dtype=float), False),
        ("studiocam:7", "matrix_vis_active", 0, np.array(scam), True),
        ("map", "matrix_vis", 0, np.array(maprow), True),
        ("light:0", "light", 0, np.array(mlight), True),
    ], {"lights": {"light:0": {"name": "Map sun", "type": "directional", "owner": "map"}}})
    write_k2t(tmp / "tracks" / "time.k2t", frames, [("time", "seconds", 0, np.zeros((frames, 1)), False)])
    scene = {"format": "kk2blender-scene", "version": 1, "complete": True, "screen": [1920, 1080],
             "characters": [], "items": [],
             "studio_cameras": [{"key": 7, "name": "Cam A", "track": "studiocam:7"}],
             "saved_views": [{"index": 0, "matrix": list(rows(translate(0, 2, -5))), "fov": 40.0, "distance": 5.0}],
             "map": {"no": 1, "name": "Classroom", "parts": [{"name": "floor", "file": "map/part_000.glb",
                                                               "matrix": list(rows(np.eye(4)))}]},
             "effects": {"dof": {"enable": True, "aperture": 0.5}, "bloom": {"enable": True, "intensity": 0.8,
                         "threshold": 0.9, "blur": 0.5}, "vignette": {"enable": True},
                         "fog": {"enable": True, "color": [0.5, 0.5, 0.6, 1], "height": 3, "start": 2}},
             "bake": {"fps": 30, "frame_count": frames,
                      "tracks": {"scene": "tracks/scene.k2t", "time": "tracks/time.k2t"}}}
    (tmp / "scene.json").write_text(json.dumps(scene))
    report = build.import_scene(tmp, "studio", "preview", import_characters=False)
    s = bpy.context.scene
    cam_obj = s.camera
    assert cam_obj.data.dof.use_dof
    s.frame_set(3)
    assert abs(cam_obj.data.dof.focus_distance - 5.0) < 1e-4, cam_obj.data.dof.focus_distance
    assert bpy.data.objects.get("Cam A") is not None and bpy.data.objects["Cam A"].type == "CAMERA"
    view = bpy.data.objects.get("Studio view 1")
    assert view is not None
    assert np.allclose(view.matrix_world.translation, (0, 5, 2), atol=1e-4), view.matrix_world.translation
    floor = next(o for o in bpy.data.objects if o.type == "MESH" and o.data.materials and
                 o.data.materials[0].get("kk2b_lightmap"))
    mat = floor.data.materials[0]
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    assert (bsdf.inputs.get("Emission Color") or bsdf.inputs["Emission"]).is_linked
    assert tuple(bsdf.inputs["Base Color"].default_value)[:3] == (0.0, 0.0, 0.0)
    assert any(o.type == "LIGHT" and o.get("kk2b_owner") == "map" for o in bpy.data.objects)
    assert s.compositing_node_group is not None
    assert any(n.bl_idname == "CompositorNodeGlare" for n in s.compositing_node_group.nodes)
    assert any("map" in line for line in report.lines), report.lines


TESTS = [v for k, v in sorted(globals().items()) if k.startswith("test_")]

failed = 0
for t in TESTS:
    try:
        t()
        print("PASS", t.__name__)
    except Exception:
        failed += 1
        print("FAIL", t.__name__)
        traceback.print_exc()
print(f"{len(TESTS) - failed} passed, {failed} failed")
sys.exit(1 if failed else 0)
