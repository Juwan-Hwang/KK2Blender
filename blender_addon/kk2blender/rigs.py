"""Pose-rig handles: plain Studio shapes that scene authors hang at the ends of a folder chain to see
and grab the folders a pose rig is built from. They aren't part of the picture, so Import All hides
them (they're kept, carrying the rig's motion, and can be unhidden in the outliner)."""

from __future__ import annotations

import numpy as np

RIG_SHAPE_BUNDLE = "studio/item_shape.unity3d"
# A handle is a shape left at its own size or shrunk, the same on every axis. A shape the author
# stretched or enlarged (a ball, a pillar) is a prop, even at the end of a rig's folder chain.
HANDLE_MAX_SCALE = 1.0 + 1e-3
HANDLE_MAX_ANISOTROPY = 1.05


def handle_sized(rows) -> bool:
    """Whether a baked item transform (frames, >=12: 3x4 row-major) keeps a handle's size: uniform
    scale no larger than the shape's own, on its first frame."""
    rows = np.nan_to_num(np.asarray(rows, dtype=np.float64))
    if rows.ndim != 2 or rows.shape[0] == 0 or rows.shape[1] < 12:
        return True
    basis = rows[0, :12].reshape(3, 4)[:, :3]
    scale = np.linalg.norm(basis, axis=0)
    if scale.max() <= 0:
        return True
    return bool(scale.max() <= HANDLE_MAX_SCALE and scale.max() / max(scale.min(), 1e-9) <= HANDLE_MAX_ANISOTROPY)


def rig_markers(folder) -> set:
    """Keys of plain Studio shapes (spheres, cubes, ...) used as handles: hung at the end of a folder
    chain, with nothing hung under them, in a folder tree the animation moves (a pose rig), at the
    shape's own size or smaller."""
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
        if root(item.get("parent")) not in moving_roots:
            continue
        name = item.get("track")
        if tracks is not None and name in tracks and not handle_sized(tracks.full(name)):
            continue
        out.add(item.get("key"))
    return out
