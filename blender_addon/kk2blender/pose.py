"""Turns the game's baked bone matrices into Blender pose channels. Pure numpy.

For a KKBP bone, the bake gives M_t, the game bone's full matrix (with scale) in the character
root's space at every frame. KKBP built the mesh at its capture pose, with every bone's scale
already in the vertices and the bone rest poses unscaled (KKBP ADR 0008): the capture matrix was
R_rest @ S, where S is the bone's game_scale along its own axes. So the game moves a vertex by
M_t @ inv(R_rest @ S). Blender moves it by P @ inv(L), where L is the bone's rest matrix in the
armature (matrix_local) and P its posed matrix. They agree when

    P = B(M_t @ inv(S)) @ inv(B(R_rest)) @ L

with B the space conversion. That holds whatever axes and heads KKBP gave the Blender bones (it
re-aims tails and rolls, and nudges some heads), because only the product with inv(L) matters.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from . import space


@dataclass
class RestBone:
    """A KKBP skeleton bone (skeleton.json): rest pose relative to its parent, in glTF space."""

    id: str
    name: str
    parent: str | None
    translation: tuple
    rotation: tuple  # x, y, z, w
    game_scale: tuple = (1.0, 1.0, 1.0)


def rest_matrices(bones: list[RestBone]) -> dict[str, np.ndarray]:
    """Each bone's unscaled rest matrix in the character root's space, in Blender's axes."""
    by_id = {b.id: b for b in bones}
    world_gltf: dict[str, np.ndarray] = {}

    def world(bid: str) -> np.ndarray:
        if bid in world_gltf:
            return world_gltf[bid]
        b = by_id[bid]
        local = space.trs(b.translation, b.rotation)
        m = local if b.parent is None or b.parent not in by_id else world(b.parent) @ local
        world_gltf[bid] = m
        return m

    for b in bones:
        world(b.id)
    return {bid: space.gltf_to_blender(m) for bid, m in world_gltf.items()}


def safe_inv(m: np.ndarray) -> np.ndarray:
    """Inverse of one or many 4x4 matrices. A bone scaled to zero in the game (a hidden part, a
    flattened bust) has no inverse; those take the pseudo-inverse, so its children collapse with it
    the way they do in the game instead of the whole import failing."""
    try:
        return np.linalg.inv(m)
    except np.linalg.LinAlgError:
        return np.linalg.pinv(m)


def posed_matrices(baked_unity_rows: np.ndarray, rest_blender: np.ndarray, matrix_local: np.ndarray,
                   game_scale=(1.0, 1.0, 1.0)) -> np.ndarray:
    """P for every frame: (N, 12) Unity rows -> (N, 4, 4) armature-space posed matrices."""
    m = space.rows_to_matrices(baked_unity_rows)
    inv_s = np.diag([1.0 / max(float(s), 1e-9) for s in game_scale] + [1.0])
    m = m @ inv_s
    return space.unity_to_blender(m) @ safe_inv(rest_blender) @ matrix_local


def basis_matrices(posed: dict[str, np.ndarray], parents: dict[str, str | None],
                   matrix_local: dict[str, np.ndarray], frames: int) -> dict[str, np.ndarray]:
    """Blender's matrix_basis for each bone in `posed`, from armature-space posed matrices.

    Blender poses a bone as P = P_parent @ inv(L_parent) @ L @ basis (full scale inheritance), so
    basis = inv(L) @ L_parent @ inv(P_parent) @ P. A parent without baked data is taken to follow its
    own parent at rest (basis = identity), which is what Blender does with an unkeyed bone.
    """
    resolved: dict[str, np.ndarray] = {}

    def pose_of(name: str) -> np.ndarray | None:
        if name in resolved:
            return resolved[name]
        if name in posed:
            resolved[name] = posed[name]
            return posed[name]
        parent = parents.get(name)
        if parent is None:
            resolved[name] = np.broadcast_to(matrix_local[name], (frames, 4, 4))
            return resolved[name]
        pp = pose_of(parent)
        resolved[name] = pp @ safe_inv(matrix_local[parent]) @ matrix_local[name]
        return resolved[name]

    out = {}
    for name, p in posed.items():
        parent = parents.get(name)
        lin = safe_inv(matrix_local[name])
        if parent is None:
            out[name] = lin @ p
        else:
            out[name] = lin @ matrix_local[parent] @ safe_inv(pose_of(parent)) @ p
    return out


def fill_gaps(rows: np.ndarray) -> np.ndarray:
    """Holds the nearest real record over NaN frames (an object missing for a while), so a bone
    doesn't snap to garbage; frames before the first record take the first one."""
    rows = rows.copy()
    valid = ~np.isnan(rows).any(axis=1)
    if not valid.any():
        return rows
    idx = np.where(valid, np.arange(len(rows)), -1)
    np.maximum.accumulate(idx, out=idx)
    first = np.argmax(valid)
    idx[idx < 0] = first
    return rows[idx]
