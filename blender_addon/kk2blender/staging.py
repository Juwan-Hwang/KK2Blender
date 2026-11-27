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


RIG_SHAPE_BUNDLE = "studio/item_shape.unity3d"


def rig_markers(folder) -> set:
    """Keys of plain Studio shapes (spheres, cubes, ...) used as handles: hung at the end of a folder
    chain, with nothing hung under them, in a folder tree the animation moves (a pose rig). Scene authors add them to see and grab the folders
    a pose rig is built from; they aren't part of the picture."""
    scene = folder.scene
    tracks = folder.scene_tracks
    folders = {f["key"]: f for f in scene.get("folders", []) if "key" in f}
    parents = {i.get("parent") for i in scene.get("items", [])} | {f.get("parent") for f in folders.values()}
    moving = {}

    def animated(key):
        if key not in moving:
            f = folders.get(key)
            name = f and f.get("track")
            ok = False
            if tracks is not None and name in tracks:
                data = np.nan_to_num(tracks.full(name)[:, :12])
                ok = bool(len(data) > 1 and np.ptp(data, axis=0).max() > 1e-5)
            moving[key] = ok
        return moving[key]

    def root(key):
        seen = set()
        while key in folders and folders[key].get("parent") in folders and key not in seen:
            seen.add(key)
            key = folders[key].get("parent")
        return key

    moving_roots = {root(k) for k in folders if animated(k)}
    out = set()
    for item in scene.get("items", []):
        asset = item.get("asset") or {}
        if asset.get("bundle", "").replace("\\", "/").lower() != RIG_SHAPE_BUNDLE:
            continue
        if item.get("key") in parents or item.get("parent") not in folders:
            continue
        if root(item.get("parent")) in moving_roots:
            out.add(item.get("key"))
    return out


def build_items(folder, frame_start, collection, hide_rig_markers=True):
    """Props as empties for now, keyed with their baked transform and visibility, named after the
    item and carrying its game asset (bundle and file) for the prop library to fill in."""
    tracks = folder.scene_tracks
    made = []
    markers = rig_markers(folder) if hide_rig_markers else set()
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
        model = item.get("model")
        if model and (folder.path / model).exists():
            from . import toon
            parts = _attach_model(obj, folder.path / model, collection)
            for part in parts:
                if part.type == "MESH":
                    for m in part.data.materials:
                        toon.apply(m)
            try:
                _apply_item_skins(folder, item, name, parts, frame_start)
            except Exception as e:  # an animated prop's motion is a nicety; never lose the scene over it
                print(f"KK2Blender: {item.get('name')}: skinned motion not applied: {e}")
        frames = _frames(len(data), frame_start)
        m = space.unity_to_blender(space.rows_to_matrices(data[:, :12]))
        curves = anim.Curves(obj, f"{obj.name} (KK2Blender)")
        _key_transform(curves, frames, m)
        if item.get("key") in markers:
            # A rig handle: kept (it carries the rig's motion) but hidden; unhide it in the outliner.
            obj["kk2b_rig_marker"] = True
            for o in [obj] + list(obj.children_recursive):
                o.hide_render = True
                o.hide_viewport = True
            made.append(obj)
            continue
        hidden = (np.nan_to_num(data[:, 12]) < 0.5).astype(float)
        if hidden.any():
            curves.curve("hide_render", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
            curves.curve("hide_viewport", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
        made.append(obj)
    return made


def _point_attr(mesh, *names):
    for n in names:
        a = mesh.attributes.get(n)
        if a is not None and a.domain == "POINT":
            vals = np.zeros(len(mesh.vertices), dtype=np.float64)
            a.data.foreach_get("value", vals)
            return np.rint(vals).astype(np.int64)
    return None


def _apply_item_skins(folder, item, track, parts, frame_start):
    """Animated props (their own skeleton or Animator): the bake carries every skinned vertex every
    frame ("<item>:skin:<n>"); played back as a point cache on the prop's matching mesh."""
    tracks = folder.scene_tracks
    if tracks is None:
        return
    from . import cloth
    cache_dir = folder.path / "blender_cache"
    for part in parts:
        if part.type != "MESH":
            continue
        me = part.data
        ordinal = _point_attr(me, "_KK_MESH", "KK_MESH")
        index = _point_attr(me, "_KK_VERTEX", "KK_VERTEX")
        if ordinal is None or index is None or len(ordinal) == 0:
            continue
        name = f"{track}:skin:{int(ordinal[0])}"
        if name not in tracks:
            continue
        data = pose.fill_gaps(tracks.full(name))
        pts = data.reshape(len(data), -1, 3)
        if index.max() >= pts.shape[1]:
            continue
        g = pts[:, index, :]
        # glTF (Y up) to Blender (Z up), the way the glTF importer converts the mesh itself.
        b = np.stack([g[..., 0], -g[..., 2], g[..., 1]], axis=-1).astype(np.float32)
        cache_dir.mkdir(exist_ok=True)
        path = cache_dir / f"{bpy.path.clean_name(part.name)}_{item.get('key')}.pc2"
        cloth.write_pc2(path, b, float(frame_start))
        mod = part.modifiers.new("KK2Blender motion", "MESH_CACHE")
        mod.cache_format = "PC2"
        mod.filepath = str(path)
        mod.frame_start = float(frame_start)


def _attach_model(empty, path, collection):
    """Imports a prop's .glb (written in the item's own space) and parents it under the item's empty,
    so it follows the item's baked transform."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        collection.objects.link(o)
        if o.parent is None:
            basis = o.matrix_basis.copy()
            o.parent = empty
            o.matrix_parent_inverse.identity()
            o.matrix_basis = basis
    if new:
        empty.empty_display_size = 0.02
    return new


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


def _matrix_rows_to_blender(rows) -> Matrix:
    return Matrix(space.unity_to_blender(space.rows_to_matrices(np.asarray(rows, dtype=np.float64)[None]))[0].tolist())


def apply_depth_of_field(camera, folder, frame_start):
    """Studio focuses its depth of field on the point its camera orbits; the camera keeps that
    distance per frame. The f-stop is mapped from Studio's aperture to look about as soft."""
    dof = (folder.scene.get("effects") or {}).get("dof") or {}
    tracks = folder.scene_tracks
    if camera is None or not dof.get("enable") or tracks is None or "camera_target" not in tracks:
        return
    data = pose.fill_gaps(tracks.full("camera_target"))
    cam = camera.data
    cam.dof.use_dof = True
    aperture = float(dof.get("aperture", 0.5))
    cam.dof.aperture_fstop = float(np.clip(2.8 / max(aperture, 0.05), 0.7, 32.0))
    distance = np.maximum(data[:, 3], 0.05)
    cam.dof.focus_distance = float(distance[0])
    anim.Curves(cam, "Studio Camera lens (KK2Blender)").curve(
        "dof.focus_distance", 0, _frames(len(data), frame_start), distance, group="Lens", eps=1e-4)


def build_studio_cameras(folder, frame_start, collection, lens):
    """The camera objects placed in Studio, and its saved camera views, as Blender cameras; the
    render camera stays the baked Studio view."""
    made = []
    tracks = folder.scene_tracks
    for info in folder.scene.get("studio_cameras", []):
        name = info.get("track")
        if tracks is None or name not in tracks:
            continue
        data = pose.fill_gaps(tracks.full(name))
        cd = bpy.data.cameras.new(info.get("name") or name)
        cd.sensor_fit, cd.sensor_height, cd.lens = "VERTICAL", SENSOR_HEIGHT, lens
        obj = bpy.data.objects.new(cd.name, cd)
        collection.objects.link(obj)
        m = space.unity_to_blender(space.rows_to_matrices(data[:, :12])) @ UNITY_LOOK
        curves = anim.Curves(obj, f"{obj.name} (KK2Blender)")
        frames = _frames(len(data), frame_start)
        _key_transform(curves, frames, m)
        hidden = (np.nan_to_num(data[:, 12]) < 0.5).astype(float)
        if hidden.any():
            curves.curve("hide_viewport", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
        made.append(obj)
    views = folder.scene.get("saved_views", [])
    if views:
        col = bpy.data.collections.new("Saved views")
        collection.children.link(col)
        for v in views:
            cd = bpy.data.cameras.new(f"Studio view {v['index'] + 1}")
            cd.sensor_fit, cd.sensor_height = "VERTICAL", SENSOR_HEIGHT
            cd.lens = float((SENSOR_HEIGHT / 2.0) / math.tan(math.radians(np.clip(v.get("fov", 23.0), 1, 179)) / 2.0))
            cd.dof.focus_distance = float(v.get("distance", 10.0))
            obj = bpy.data.objects.new(cd.name, cd)
            col.objects.link(obj)
            obj.matrix_world = _matrix_rows_to_blender(v["matrix"]) @ Matrix(UNITY_LOOK.tolist())
            made.append(obj)
    return made


def _lightmap_material(mat):
    """A map material with a baked lightmap: Unity draws it as albedo × lightmap, unlit. Here the
    same product goes to emission and the surface takes no other light. The lightmap holds Unity's
    RGBM (colour × alpha × 5, in gamma space), decoded in the node tree."""
    if mat is None or not mat.use_nodes:
        return
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        return
    # The glTF importer puts the occlusion image (our lightmap, on the second UV set) into its
    # settings group; find the image node that isn't the base colour's.
    base_link = next((l for l in links if l.to_socket == bsdf.inputs["Base Color"]), None)
    base_image = base_link.from_node if base_link is not None else None
    lightmap = next((n for n in nodes if n.type == "TEX_IMAGE" and n != base_image), None)
    if lightmap is None:
        return
    for l in list(lightmap.outputs[0].links) + list(lightmap.outputs[1].links):
        links.remove(l)
    if lightmap.image is not None:
        lightmap.image.colorspace_settings.name = "Non-Color"
    # rgb * a * 5, then to linear (gamma 2.2).
    mul_a = nodes.new("ShaderNodeVectorMath")
    mul_a.operation = "SCALE"
    links.new(lightmap.outputs["Color"], mul_a.inputs[0])
    scale = nodes.new("ShaderNodeMath")
    scale.operation = "MULTIPLY"
    scale.inputs[1].default_value = 5.0
    links.new(lightmap.outputs["Alpha"], scale.inputs[0])
    links.new(scale.outputs[0], mul_a.inputs["Scale"])
    gamma = nodes.new("ShaderNodeGamma")
    gamma.inputs["Gamma"].default_value = 2.2
    links.new(mul_a.outputs[0], gamma.inputs["Color"])
    lit = nodes.new("ShaderNodeMix")
    lit.data_type = "RGBA"
    lit.blend_type = "MULTIPLY"
    lit.inputs[0].default_value = 1.0
    if base_link is not None:
        links.new(base_link.from_socket, lit.inputs[6])
    else:
        lit.inputs[6].default_value = bsdf.inputs["Base Color"].default_value
    links.new(gamma.outputs[0], lit.inputs[7])
    emission = bsdf.inputs.get("Emission Color") or bsdf.inputs.get("Emission")
    links.new(lit.outputs[2], emission)
    bsdf.inputs["Emission Strength"].default_value = 1.0
    if base_link is not None:
        links.remove(base_link)
    bsdf.inputs["Base Color"].default_value = (0.0, 0.0, 0.0, 1.0)
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.0
    bsdf.inputs["Roughness"].default_value = 1.0


def build_map(folder, frame_start, collection, log):
    """The map exported from Studio: each part's .glb under one empty that follows the baked map
    root. Returns the empty, or None when the export has no map meshes."""
    info = folder.scene.get("map") or {}
    parts = info.get("parts") or []
    if not parts:
        return None
    root = bpy.data.objects.new(f"Map {info.get('name', '')}".strip(), None)
    root.empty_display_size = 0.5
    collection.objects.link(root)
    tracks = folder.scene_tracks
    if tracks is not None and "map" in tracks:
        data = pose.fill_gaps(tracks.full("map"))
        curves = anim.Curves(root, f"{root.name} (KK2Blender)")
        _key_transform(curves, _frames(len(data), frame_start), space.unity_to_blender(space.rows_to_matrices(data[:, :12])))
    col = bpy.data.collections.new(root.name)
    collection.children.link(col)
    for part in parts:
        path = folder.path / part["file"]
        if not path.exists():
            continue
        holder = bpy.data.objects.new(part.get("name") or path.stem, None)
        holder.empty_display_size = 0.1
        col.objects.link(holder)
        holder.parent = root
        holder.matrix_parent_inverse.identity()
        holder.matrix_basis = _matrix_rows_to_blender(part["matrix"])
        new = _attach_model(holder, path, col)
        for o in new:
            if o.type == "MESH":
                for m in o.data.materials:
                    if m is not None and m.get("kk2b_lightmap"):
                        _lightmap_material(m)
    log(f"map '{info.get('name')}': {len(parts)} parts")
    return root
