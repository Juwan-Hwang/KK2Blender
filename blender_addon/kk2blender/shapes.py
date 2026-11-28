"""The game's blend shape weights per renderer, for the "raw:" shape keys of the meshes that came from it.

KK's own blend shape names carry their mesh ("eye_face.f00_def_op"), so one name means one shape
wherever it is. Clothing mods (ClothingBlendShape, BlendShapeCreator) don't: two outfit meshes can
both have a "Key 1" set to different weights. So an object's raw keys take the weights of its own
renderers first and the name-wide weights only for names its renderers don't have.
"""

from __future__ import annotations

import numpy as np


def per_renderer(renderers: dict, channel) -> dict[str, dict[str, np.ndarray]]:
    """{renderer path: {"raw:<name>": (frames,) weight 0..1}} from the bake's "shape:<path>" channels.
    renderers: the track header's "renderers" ({path: {"blend_shapes": [...]}}); channel(name)
    returns the (frames, width) data of a channel or None."""
    out: dict[str, dict[str, np.ndarray]] = {}
    for path, info in renderers.items():
        names = info.get("blend_shapes")
        if not names:
            continue
        data = channel("shape:" + path)
        if data is None or data.shape[1] != len(names):
            continue
        out[path] = {"raw:" + name: np.nan_to_num(data[:, i]) / 100.0 for i, name in enumerate(names)}
    return out


def by_name(per_path: dict[str, dict[str, np.ndarray]]) -> dict[str, np.ndarray]:
    """One weight curve per raw key name: of all renderers with that name, the one that moves most
    (a shape on a renderer that isn't drawn yet, an outfit not worn, mustn't win)."""
    out: dict[str, np.ndarray] = {}
    for weights in per_path.values():
        for key, w in weights.items():
            if key not in out or np.abs(w).max() > np.abs(out[key]).max():
                out[key] = w
    return out


def for_object(paths, per_path: dict[str, dict[str, np.ndarray]], fallback: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
    """The raw key weights for an object made from the renderers at `paths`: theirs first (the most
    moving one when several of its renderers share a name), then `fallback` for the rest."""
    own = by_name({p: per_path[p] for p in paths if p in per_path})
    return {**fallback, **own}
