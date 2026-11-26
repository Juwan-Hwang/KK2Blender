"""Camera, Studio lights, props and the map: everything in the scene that isn't a character."""

from __future__ import annotations

import math

import bpy
import numpy as np
from mathutils import Matrix

from . import anim, pose, space

# A Unity camera or light points along its local +Z with +Y up; a Blender one along -Z with +Y up.
# After the space conversion the Unity axes sit at Blender local (-X, +Z, -Y) for (+X, +Y, +Z),
# so this fixed rotation lines a Blender camera or light up with them.
UNITY_LOOK = np.array([[-1.0, 0, 0, 0], [0, 0, 1.0, 0], [0, 1.0, 0, 0], [0, 0, 0, 1.0]])

SENSOR_HEIGHT = 24.0


def _frames(n, start):
    return np.arange(n, dtype=np.float64) + start


def _key_transform(curves, frames, m, eps=1e-6):
    loc, quat, scale = space.decompose(m)
    curves.id.rotation_mode = "QUATERNION"
    for i in range(3):
        curves.curve("location", i, frames, loc[:, i], group="Object Transforms", eps=eps)
    for i in range(4):
        curves.curve("rotation_quaternion", i, frames, quat[:, i], group="Object Transforms", eps=eps)
    for i in range(3):
        curves.curve("scale", i, frames, scale[:, i], group="Object Transforms", eps=1e-5)


def _collection(name: str) -> bpy.types.Collection:
    col = bpy.data.collections.get(name)
    if col is None:
        col = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(col)
    return col


def build_camera(tracks, frame_start, collection) -> bpy.types.Object | None:
    if tracks is None or "camera" not in tracks:
        return None
    data = pose.fill_gaps(tracks.full("camera"))
    n = len(data)
    cam_data = bpy.data.cameras.new("Studio Camera")
    cam_data.sensor_fit = "VERTICAL"
    cam_data.sensor_height = SENSOR_HEIGHT
    obj = bpy.data.objects.new("Studio Camera", cam_data)
    collection.objects.link(obj)
    frames = _frames(n, frame_start)

    m = space.unity_to_blender(space.rows_to_matrices(data[:, :12])) @ UNITY_LOOK
    _key_transform(anim.Curves(obj, "Studio Camera (KK2Blender)"), frames, m)

    fov = np.radians(np.clip(data[:, 12], 1e-3, 179.0))
    lens = (SENSOR_HEIGHT / 2.0) / np.tan(fov / 2.0)
    cam_data.lens = float(lens[0])
    cam_data.clip_start = float(max(np.nanmin(data[:, 13]), 1e-4))
    cam_data.clip_end = float(np.nanmax(data[:, 14]))
    curves = anim.Curves(cam_data, "Studio Camera lens (KK2Blender)")
    curves.curve("lens", 0, frames, lens, group="Lens", eps=1e-4)
    if np.nanmax(data[:, 15]) > 0.5:
        cam_data.type = "ORTHO"
        cam_data.ortho_scale = float(data[0, 16] * 2.0)
    bpy.context.scene.camera = obj
    return obj


def light_power(kind: str, intensity: float, range_: float, factor: float) -> float:
    """Unity intensity -> Blender: a sun's strength (W/m²) or a point/spot's watts. KK's toon shaders
    react to light direction more than amount, so these only aim for a sensible exposure."""
    if kind == "directional":
        return intensity * 2.5 * factor
    return intensity * max(range_, 0.1) ** 2 * 40.0 * factor


def build_lights(tracks, frame_start, collection, which=("studio_light", "camera"), factor=1.0):
    """Unity lights as Blender lights. `which` picks owners: studio_light (lights added in Studio),
    camera (Studio's character light), map (the map's own), other."""
    if tracks is None:
        return []
    info = tracks.header.get("lights", {})
    made = []
    for name, meta in info.items():
        if meta.get("owner") not in which or name not in tracks:
            continue
        data = pose.fill_gaps(tracks.full(name))
        n = len(data)
        kind = meta.get("type", "point")
        btype = {"directional": "SUN", "spot": "SPOT", "point": "POINT"}.get(kind, "POINT")
        ld = bpy.data.lights.new(f"{meta.get('name', name)}", btype)
        obj = bpy.data.objects.new(ld.name, ld)
        obj["kk2b_owner"] = meta.get("owner", "")
        collection.objects.link(obj)
        frames = _frames(n, frame_start)
        m = space.unity_to_blender(space.rows_to_matrices(data[:, :12])) @ UNITY_LOOK
        _key_transform(anim.Curves(obj, f"{ld.name} (KK2Blender)"), frames, m)
        ld.color = tuple(float(c) for c in data[0, 12:15])
        power = np.array([light_power(kind, i, r, factor) for i, r in zip(data[:, 15], data[:, 16])])
        power = power * (np.nan_to_num(data[:, 18]) > 0.5)
        lc = anim.Curves(ld, f"{ld.name} light (KK2Blender)")
        if btype == "SUN":
            ld.energy = float(power[0])
            lc.curve("energy", 0, frames, power, group="Light")
            ld.angle = math.radians(2.0)
        else:
            ld.energy = float(power[0])
            lc.curve("energy", 0, frames, power, group="Light")
            ld.shadow_soft_size = 0.05
            if btype == "SPOT":
                ld.spot_size = math.radians(float(np.clip(data[0, 17], 1, 179)))
                ld.spot_blend = 0.15
        for i in range(3):
            lc.curve("color", i, frames, data[:, 12 + i], group="Light")
        ld.use_shadow = bool(np.nan_to_num(data[0, 19]) > 0)
        made.append(obj)
    return made


def build_items(folder, frame_start, collection):
    """Props as empties for now, keyed with their baked transform and visibility, named after the
    item and carrying its game asset (bundle and file) for the prop library to fill in."""
    tracks = folder.scene_tracks
    made = []
    for item in folder.scene.get("items", []):
        name = item.get("track")
        if tracks is None or name not in tracks:
            continue
        data = pose.fill_gaps(tracks.full(name))
        obj = bpy.data.objects.new(item.get("name") or name, None)
        obj.empty_display_type = "CUBE"
        obj.empty_display_size = 0.1
        obj["kk2b_item"] = f"{item.get('group')}/{item.get('category')}/{item.get('no')}"
        asset = item.get("asset") or {}
        if asset:
            obj["kk2b_bundle"] = asset.get("bundle", "")
            obj["kk2b_file"] = asset.get("file", "")
        collection.objects.link(obj)
        frames = _frames(len(data), frame_start)
        m = space.unity_to_blender(space.rows_to_matrices(data[:, :12]))
        curves = anim.Curves(obj, f"{obj.name} (KK2Blender)")
        _key_transform(curves, frames, m)
        hidden = (np.nan_to_num(data[:, 12]) < 0.5).astype(float)
        if hidden.any():
            curves.curve("hide_render", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
            curves.curve("hide_viewport", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
        made.append(obj)
    return made


def link_map(folder, collection, log):
    """Links the scene's map from a kkbp-maps library, when one is installed and has it."""
    info = folder.scene.get("map")
    if not info:
        return None
    name = info.get("name")
    libs = bpy.context.preferences.filepaths.asset_libraries
    candidates = []
    for lib in libs:
        from pathlib import Path
        root = Path(bpy.path.abspath(lib.path))
        if root.is_dir():
            candidates += [p for p in root.rglob("*.blend") if name and name.lower() in p.stem.lower()]
    if not candidates:
        log(f"map '{name}' (no {info.get('no')}): no kkbp-maps library entry found; the scene has no map")
        return None
    path = str(candidates[0])
    with bpy.data.libraries.load(path, link=True) as (src, dst):
        dst.collections = [c for c in src.collections if name.lower() in c.lower()][:1] or src.collections[:1]
    linked = None
    for col in dst.collections:
        inst = bpy.data.objects.new(f"Map {name}", None)
        inst.instance_type = "COLLECTION"
        inst.instance_collection = col
        collection.objects.link(inst)
        tracks = folder.scene_tracks
        if tracks is not None and "map" in tracks:
            m = space.unity_to_blender(space.rows_to_matrices(pose.fill_gaps(tracks.full("map"))[:1, :12]))[0]
            inst.matrix_world = Matrix(m.tolist())
        linked = inst
    log(f"map '{name}' linked from {path}")
    return linked
