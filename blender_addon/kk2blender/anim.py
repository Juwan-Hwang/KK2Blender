"""Writing baked values as F-curves, fast: one keyframe_points.add and foreach_set per curve.

Blender 4.4 introduced slotted Actions and 5.0 removed the old Action.fcurves, so curves are made
through the Action's channelbag for the ID's slot.
"""

from __future__ import annotations

import bpy
import numpy as np

_INTERP = {}


def _interp_value(name: str) -> int:
    if name not in _INTERP:
        _INTERP[name] = bpy.types.Keyframe.bl_rna.properties["interpolation"].enum_items[name].value
    return _INTERP[name]


class Curves:
    """The F-curves of one ID (object, shape key datablock, camera, light) in one new Action."""

    def __init__(self, id_data, action_name: str):
        """Uses the ID's current Action when it has one (so an object's transform and its bones share
        one), else makes one named `action_name`."""
        self.id = id_data
        ad = id_data.animation_data or id_data.animation_data_create()
        action = ad.action
        if action is None:
            action = bpy.data.actions.new(action_name)
            ad.action = action
        self.action = action
        self.channelbag = None
        slots = getattr(action, "slots", None)
        if slots is not None:
            slot = ad.action_slot
            if slot is None:
                slot = slots.new(id_type=id_data.id_type, name=id_data.name)
                ad.action_slot = slot
            from bpy_extras import anim_utils
            self.channelbag = anim_utils.action_ensure_channelbag_for_slot(action, slot)
        self._groups = {}

    @property
    def fcurves(self):
        return self.channelbag.fcurves if self.channelbag is not None else self.action.fcurves

    def _group(self, name):
        if not name:
            return None
        if name not in self._groups:
            groups = self.channelbag.groups if self.channelbag is not None else self.action.groups
            self._groups[name] = groups.new(name)
        return self._groups[name]

    def curve(self, data_path: str, index: int, frames: np.ndarray, values: np.ndarray, group: str = "",
              interpolation: str = "LINEAR", simplify: bool = True, eps: float = 1e-6):
        """Keys `values` at `frames`. A curve that never changes gets one key; otherwise keys where the
        value changes, and the frames around each change, so a linear curve still holds steps."""
        values = np.asarray(values, dtype=np.float64)
        frames = np.asarray(frames, dtype=np.float64)
        keep = np.isfinite(values)
        frames, values = frames[keep], values[keep]
        if len(values) == 0:
            return None
        if simplify:
            frames, values = _reduce(frames, values, eps, interpolation == "CONSTANT")
        fc = self.fcurves.new(data_path, index=index)
        g = self._group(group)
        if g is not None:
            fc.group = g
        kp = fc.keyframe_points
        kp.add(len(values))
        co = np.empty(len(values) * 2, dtype=np.float32)
        co[0::2] = frames
        co[1::2] = values
        kp.foreach_set("co", co)
        kp.foreach_set("interpolation", [_interp_value(interpolation)] * len(values))
        fc.update()
        return fc


def _reduce(frames: np.ndarray, values: np.ndarray, eps: float, constant: bool):
    """Drops keys that a linear (or constant) curve through their neighbours reproduces."""
    n = len(values)
    if n <= 2:
        return frames, values
    if np.all(np.abs(values - values[0]) <= eps):
        return frames[:1], values[:1]
    keep = np.ones(n, dtype=bool)
    if constant:
        keep[1:] = np.abs(np.diff(values)) > eps
    else:
        # Interior points on the straight line between their neighbours are redundant (evenly spaced frames).
        mid = (values[:-2] + values[2:]) * 0.5
        keep[1:-1] = np.abs(values[1:-1] - mid) > eps
    keep[0] = keep[-1] = True
    return frames[keep], values[keep]
