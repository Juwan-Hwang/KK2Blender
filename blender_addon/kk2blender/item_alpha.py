"""A Studio item's Alpha (its slider, or the Timeline's item alpha), as the game draws it.

Studio sets the item's Alpha as `_alpha` on its alpha renderers only (ItemComponent.rendAlpha); at 0 they
draw nothing, so a fade panel or a hidden helper plane in front of the camera is invisible in the game.
The props' glb carry their materials at full alpha, so Blender has to apply the item's Alpha itself.

Which materials follow it: exporters from 0.1.6 mark them in the glb (material extras
"kk2b_item_alpha"); for older exports, the alpha-blended materials (glTF alphaMode BLEND), which is what
Studio's alpha renderers use. The Alpha per frame: the item track's 14th float (0.1.6+), else the item's
"alpha" in scene.json (the value at export).

Pure functions here; staging.build_items applies them to the Blender objects.
"""

from __future__ import annotations

import json
import re
import struct
from pathlib import Path

import numpy as np

# Below this the game's alpha renderers show nothing worth drawing (8-bit alpha rounds to 0).
INVISIBLE = 1.0 / 255.0
NODE_NAME = "KK2Blender item alpha"


def glb_materials(path) -> list[dict]:
    """The glTF materials of a .glb file (its JSON chunk only)."""
    data = Path(path).read_bytes()
    if len(data) < 20 or data[:4] != b"glTF":
        return []
    length = struct.unpack_from("<I", data, 12)[0]
    try:
        doc = json.loads(data[20:20 + length].decode("utf-8"))
    except ValueError:
        return []
    return list(doc.get("materials") or [])


def marks_item_alpha(exporter_version: str | None) -> bool:
    """Whether an export's glb mark the item-alpha materials themselves (exporter 0.1.6+)."""
    if not exporter_version:
        return False
    nums = [int(x) for x in re.findall(r"\d+", str(exporter_version))[:3]]
    nums += [0] * (3 - len(nums))
    return tuple(nums) >= (0, 1, 6)


def driven_material_names(materials: list[dict], marked: bool) -> set[str]:
    """Names of the glTF materials that follow the item's Alpha."""
    out = set()
    for m in materials:
        extras = m.get("extras") or {}
        if marked:
            if extras.get("kk2b_item_alpha"):
                out.add(m.get("name", ""))
        elif m.get("alphaMode") == "BLEND":
            out.add(m.get("name", ""))
    return out


def base_name(blender_name: str) -> str:
    """A Blender datablock name without the ".001" suffix it gets on a clash."""
    return re.sub(r"\.\d{3}$", "", blender_name)


def alpha_per_frame(track: np.ndarray, scene_alpha) -> np.ndarray:
    """(frames,) item Alpha: the track's 14th column when it has one, else the scene.json value (1 when
    missing). NaN frames (the item didn't exist) count as fully shown."""
    n = len(track)
    if track.ndim == 2 and track.shape[1] >= 14:
        a = np.asarray(track[:, 13], dtype=np.float64)
    else:
        try:
            v = float(scene_alpha)
        except (TypeError, ValueError):
            v = 1.0
        a = np.full(n, v if np.isfinite(v) else 1.0)
    a = np.where(np.isfinite(a), a, 1.0)
    return np.clip(a, 0.0, 1.0)


def plan(alpha: np.ndarray, material_names: list[str], driven: set[str]):
    """What to do for one item.

    Returns (hide, fade): hide is a (frames,) bool array of frames on which the whole item is invisible
    (every material it draws follows the Alpha and the Alpha is ~0), or None; fade is the alpha array to
    multiply into the driven materials, or None when it is 1 throughout or nothing is driven."""
    names = [base_name(n) for n in material_names]
    used = [n for n in names if n]
    if not used or not any(n in driven for n in used):
        return None, None
    fade = None if np.all(alpha >= 1.0 - 1e-6) else alpha
    hide = None
    if all(n in driven for n in used):
        h = alpha <= INVISIBLE
        if h.any():
            hide = h
    return hide, fade
