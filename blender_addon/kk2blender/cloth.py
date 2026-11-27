"""Unity Cloth: clothes the game simulates per vertex rather than with bones.

The bake records each cloth mesh's vertex positions every frame ("cloth:<path>", in the renderer's
own space). Its bundle mesh says it is cloth (extensions.kk.cloth) and every Blender vertex knows
its game mesh and vertex (the _KK_MESH and _KK_VERTEX attributes). The cloth's vertices are split
into their own object, and a point cache (.pc2) written from the bake replaces their positions
after the armature, so they move exactly as the game simulated them.
"""

from __future__ import annotations

import struct
from pathlib import Path

import bpy
import numpy as np

from . import pose, space

MESH_ATTR = "_KK_MESH"
VERTEX_ATTR = "_KK_VERTEX"


def write_pc2(path: Path, frames: np.ndarray, start: float = 1.0, rate: float = 1.0):
    """frames: (samples, points, 3) float32."""
    samples, points, _ = frames.shape
    with open(path, "wb") as f:
        f.write(b"POINTCACHE2\0")
        f.write(struct.pack("<iiffi", 1, points, start, rate, samples))
        f.write(np.ascontiguousarray(frames, dtype="<f4").tobytes())


def _attr(mesh, name):
    a = mesh.attributes.get(name)
    if a is None or a.domain != "POINT":
        return None
    out = np.empty(len(mesh.vertices), dtype=np.float32)
    a.data.foreach_get("value", out)
    return out.round().astype(int)


def _split(obj, ordinal):
    """Moves the vertices of game mesh `ordinal` into their own object; returns it (or obj itself
    when it holds nothing else)."""
    meshes = _attr(obj.data, MESH_ATTR)
    if meshes is None or not (meshes == ordinal).any():
        return None
    if (meshes == ordinal).all():
        return obj
    view_layer = bpy.context.view_layer
    for o in view_layer.objects:
        o.select_set(False)
    view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.object.mode_set(mode="OBJECT")
    sel = (meshes == ordinal)
    obj.data.vertices.foreach_set("select", sel)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    new = [o for o in bpy.context.selected_objects if o != obj]
    return new[0] if new else None


def apply(bake, export_folder: Path, frame_start: int):
    """Cloth caches for every cloth mesh of one character (bake: character.CharacterBake)."""
    meshes = bake.mapped("meshes")
    tracks = bake.tracks
    cloth_meshes = []
    for p in bake.bundle.passes:
        for m in p.meshes:
            ext = (getattr(m, "extensions", None) or {}).get("kk", {})
            info = meshes.get(m.id, {})
            path = info.get("path")
            if path and (ext.get("cloth") or ("cloth:" + path) in tracks):
                cloth_meshes.append((m.id, info))
    if not cloth_meshes:
        return 0
    arm = bake.ch.armature
    cache_dir = export_folder / "cache"
    cache_dir.mkdir(exist_ok=True)
    done = 0
    for mesh_id, info in cloth_meshes:
        ch = "cloth:" + info["path"]
        bone = "bone:" + info["path"]
        if ch not in tracks or bone not in tracks:
            bake.log(f"cloth {mesh_id}: no baked cloth for it")
            continue
        verts = pose.fill_gaps(tracks.full(ch)).reshape(bake.n, -1, 3).astype(np.float64)
        place = space.unity_to_blender(space.rows_to_matrices(pose.fill_gaps(tracks.full(bone))))
        # Game vertex positions in the armature's space: renderer matrix (root-relative) @ vertex.
        homo = np.concatenate([verts @ space.UNITY_TO_BLENDER.T, np.ones(verts.shape[:2] + (1,))], axis=2)
        arm_space = np.einsum("fij,fvj->fvi", place, homo)[..., :3]
        for obj in [o for o in bake.ch.parts() if o.type == "MESH"]:
            target = _split(obj, int(info.get("ordinal", -1)))
            if target is None:
                continue
            index = _attr(target.data, VERTEX_ATTR)
            if index is None or index.max() >= verts.shape[1]:
                bake.log(f"cloth {mesh_id}: vertex map doesn't fit the bake")
                continue
            # Object space: the object hangs under the armature, fixed relative to it.
            to_obj = np.array(target.matrix_world.inverted() @ arm.matrix_world)
            pts = arm_space[:, index, :] @ to_obj[:3, :3].T + to_obj[:3, 3]
            cache = cache_dir / f"{bake.ch.key}_{mesh_id}.pc2"
            write_pc2(cache, pts.astype(np.float32), float(frame_start))
            mod = target.modifiers.new("KK2Blender cloth", "MESH_CACHE")
            mod.cache_format = "PC2"
            mod.filepath = str(cache)
            mod.frame_start = float(frame_start)
            mod.deform_mode = "OVERWRITE"
            mod.forward_axis, mod.up_axis = "POS_Y", "POS_Z"
            # Right after the armature, before outlines and the rest.
            arm_mod = next((i for i, m in enumerate(target.modifiers) if m.type == "ARMATURE"), None)
            if arm_mod is not None:
                with bpy.context.temp_override(object=target):
                    bpy.ops.object.modifier_move_to_index(modifier=mod.name, index=arm_mod + 1)
            done += 1
            break
    if done:
        bake.log(f"cloth caches on {done} meshes")
    return done
