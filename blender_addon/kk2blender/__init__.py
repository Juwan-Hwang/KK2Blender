"""KK2Blender: import Koikatsu CharaStudio scenes exported by the KK2Blender Studio plugin.

Only the UI module touches bpy at import time, so the pure modules (k2t, space, pose, matching,
face, scenefile) can be tested without Blender.
"""


def register():
    from . import outfits, ui
    ui.register()
    outfits.register()


def unregister():
    from . import outfits, ui
    outfits.unregister()
    ui.unregister()
