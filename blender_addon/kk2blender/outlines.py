"""KKBP's outlines on alpha-blended clothes and hair, made to draw behind the surface again.

KKBP's Outlines hook gives each Part a Solidify shell with flipped normals and an outline copy of
each material, and relies on backface culling to hide the shell where it faces the camera. The copy
keeps its source's render method, so the outline of an alpha-blended cloth or hair material (KK's
main_alpha, main_hair_front, the studio *_alpha shaders) is BLENDED. EEVEE draws blended surfaces
without writing depth and in material order, so the shell's far side, which faces the camera through
the cloth, is drawn after the cloth and covers it in black. Dithered outlines write depth like
opaque ones, so the cloth in front of them hides them and only the rim around the silhouette shows,
as in the game.
"""

from __future__ import annotations

OUTLINE = "kkbp_outline"  # KKBP's tag on an outline material: the Id of the material it outlines


def is_outline(mat) -> bool:
    return mat is not None and mat.get(OUTLINE) is not None


def fix_render_method(materials) -> list:
    """Sets every BLENDED KKBP outline material in `materials` to DITHERED; returns their names."""
    fixed = []
    seen = set()
    for mat in materials:
        if mat is None or id(mat) in seen or not is_outline(mat):
            continue
        seen.add(id(mat))
        if getattr(mat, "surface_render_method", None) == "BLENDED":
            mat.surface_render_method = "DITHERED"
            fixed.append(mat.name)
    return fixed


def apply(ch, log):
    mats = [m for o in ch.parts() if o.type == "MESH" for m in o.data.materials]
    fixed = fix_render_method(mats)
    if fixed:
        log(f"{len(fixed)} outline materials of alpha-blended parts now draw dithered, behind the surface")
