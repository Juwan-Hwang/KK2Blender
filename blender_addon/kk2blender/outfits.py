"""Outfit changes during the animation: the body's textures follow the outfit worn.

Each outfit hides the skin under its clothes with its own alpha mask on the body material. KKBP
stores every outfit's body textures and swaps them when one Outfit Collection is shown (its ADR
0055), which can't be keyframed. Here the outfit worn on each frame is keyed on the character's
armature ("kk2b_coordinate"), and a frame-change handler asks KKBP to put that outfit's textures in.
"""

from __future__ import annotations

import sys

import bpy
from bpy.app.handlers import persistent

PROP = "kk2b_coordinate"
KEY = "kkbp_key"
_shown: dict[str, int] = {}


def _kkbp_outfit_textures():
    for name, module in list(sys.modules.items()):
        if name.endswith(".outfit_textures") and hasattr(module, "show") and hasattr(module, "TEXTURES"):
            return module
    return None


def sync(scene=None):
    module = _kkbp_outfit_textures()
    if module is None:
        return
    for obj in (scene or bpy.context.scene).objects:
        if obj.type != "ARMATURE" or PROP not in obj or KEY not in obj:
            continue
        outfit = int(round(obj[PROP]))
        key = obj[KEY]
        if _shown.get(key) == outfit or outfit < 0:
            continue
        try:
            module.show(key, outfit)
            _shown[key] = outfit
        except Exception as e:  # a texture swap must never stop playback or a render
            print("KK2Blender: outfit textures:", e)


@persistent
def _on_frame(scene, *_):
    sync(scene)


@persistent
def _on_load(*_):
    _shown.clear()


def register():
    if _on_frame not in bpy.app.handlers.frame_change_post:
        bpy.app.handlers.frame_change_post.append(_on_frame)
    if _on_load not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_on_load)


def unregister():
    for handlers, fn in ((bpy.app.handlers.frame_change_post, _on_frame), (bpy.app.handlers.load_post, _on_load)):
        if fn in handlers:
            handlers.remove(fn)
