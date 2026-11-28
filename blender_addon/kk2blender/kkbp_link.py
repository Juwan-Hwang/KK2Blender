"""Finding and calling KKBP, which this add-on builds on. KKBP is a Blender extension, so its module
name depends on the repository it was installed from; it is found among the enabled add-ons."""

from __future__ import annotations

import sys

import bpy


class KkbpMissing(Exception):
    pass


def api():
    """KKBP's public API module, or raise KkbpMissing."""
    for name in bpy.context.preferences.addons.keys():
        if name.rpartition(".")[2] == "kkbp":
            module = sys.modules.get(name)
            if module is not None and hasattr(module, "api"):
                return module.api
    raise KkbpMissing("KKBP isn't enabled. Install the KKBP add-on (Blender 5.2+) and enable it.")


def available() -> bool:
    try:
        api()
        return True
    except KkbpMissing:
        return False


def pillow_ready() -> bool:
    """KKBP needs Pillow before it imports anything. KK2Blender ships Pillow wheels that Blender installs
    with the add-on; KKBP can also download it from its sidebar tab."""
    import importlib
    for name in ("bl_ext.user_default.kkbp.pillow", "kkbp.pillow"):
        try:
            return bool(importlib.import_module(name).available())
        except Exception:
            continue
    return True  # unknown layout: let the import report what's wrong


# Import Options KK2Blender needs: the deform armature is what the bake drives, so no Rigify; clothing
# is separated per material so each piece can be shown and hidden as the game did.
IMPORT_OPTIONS = {
    "use_rigify": False,
    "separation_type": "by_material",
}


def import_character(bundle_path, extra_options: dict | None = None):
    """Imports one bundle; returns (kkbp Character, ImportResult)."""
    kkbp = api()
    options = dict(IMPORT_OPTIONS)
    options.update(extra_options or {})
    result = kkbp.import_bundle(str(bundle_path), options)
    if not result.ok:
        raise RuntimeError(f"KKBP couldn't import {bundle_path}: step {result.failed_step}: {result.error}")
    for ch in kkbp.Character.all(bpy.context.scene):
        if ch.key == result.character_key:
            return ch, result
    raise RuntimeError(f"KKBP imported {bundle_path} but its Character wasn't found")
