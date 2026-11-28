"""Which parts of a Blender object to hide on which frames, from the game's per-mesh visibility.

KKBP joins every Body Pass mesh into one Body object and keeps clothes as one object per material,
so one object can hold meshes the game draws and meshes it doesn't (a mod face's unused eye whites
and eyelines, teeth while the mouth is closed). An object is hidden while none of its materials'
meshes show; a material is hidden while its own meshes don't show but the object does.
"""

from __future__ import annotations

import numpy as np


def object_and_parts(material_ids, material_vis: dict, frames: int):
    """material_ids: the bundle Ids of an object's materials. material_vis: {Id: (frames,) bool shown}.
    Returns (shown, {Id: (frames,) bool hidden while the object shows}); shown is None when no
    material of the object has a known visibility."""
    known = {i: np.asarray(material_vis[i], dtype=bool) for i in material_ids if i in material_vis}
    if not known:
        return None, {}
    shown = np.logical_or.reduce(list(known.values())) if len(known) > 1 else next(iter(known.values())).copy()
    parts = {}
    for i, vis in known.items():
        hidden = shown & ~vis
        if hidden.any():
            parts[i] = hidden
    return shown, parts
