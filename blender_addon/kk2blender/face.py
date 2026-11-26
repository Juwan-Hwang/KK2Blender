"""Drives KKBP's expression shape keys from the baked face state. Pure numpy.

KKBP bakes each game pattern as a finished shape key (its ADR 0003): mouth and eyebrow patterns as
an open key and, where it differs, a ", Closed" key; eye sets (what Studio's eye menu picks) as one
key each, named after the eye set. The bake records, per frame, the weight the game's face
controllers put on each pattern's open and closed shapes ("face:mouth" etc.), and the eye set in use
("status"). Weights are 0..1; a pattern's open and closed weights add up to its share of the face.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

CLOSED_SUFFIX = ", Closed"
STATUS = ["coordinate", "blush", "tears_level", "skin_gloss", "nipple_stand", "eyes_open_max",
          "mouth_open_max", "eyeset", "mouth_pattern", "eyebrow_pattern", "eye_look", "neck_look"]


@dataclass
class Expression:
    mesh: str
    target: str
    category: str
    name: str | None
    game_name: str | None


def pattern_keys(records: list[Expression], mesh: str, category: str) -> list[tuple[str, str | None]]:
    """For one mesh and category ("mouth" or "eyebrow"): (open key, closed key or None) by pattern
    index, from the order KKBP wrote them in: each pattern's open key, then its closed key if kept.
    Keys after the game's patterns (KKBP's recipes, such as chewing) aren't patterns; the caller
    cuts the list to the pattern count."""
    out: list[tuple[str, str | None]] = []
    for r in records:
        if r.mesh != mesh or r.category != category:
            continue
        if r.name and r.name.endswith(CLOSED_SUFFIX) and out and out[-1][1] is None \
                and out[-1][0] == r.name[: -len(CLOSED_SUFFIX)]:
            out[-1] = (out[-1][0], r.target)
        else:
            out.append((r.target, None))
    return out


def pattern_weights(channel: np.ndarray, keys: list[tuple[str, str | None]]) -> dict[str, np.ndarray]:
    """channel: (frames, 2 * patterns) open/closed weights. A pattern without a closed key had a
    closed shape equal to its open one, so both weights go to the open key."""
    patterns = channel.shape[1] // 2
    out: dict[str, np.ndarray] = {}
    for p, (open_key, closed_key) in enumerate(keys[:patterns]):
        w_open = np.nan_to_num(channel[:, 2 * p])
        w_closed = np.nan_to_num(channel[:, 2 * p + 1])
        if closed_key is None:
            out[open_key] = out.get(open_key, 0) + w_open + w_closed
        else:
            out[open_key] = out.get(open_key, 0) + w_open
            out[closed_key] = out.get(closed_key, 0) + w_closed
    return out


def eye_weights(channel: np.ndarray, eyeset_track: np.ndarray, eyesets: list[dict],
                records: list[Expression], mesh: str, closed_eyeset: int = 1) -> dict[str, np.ndarray]:
    """Eye keys for one mesh. An eye set's key gets the open weight of its eye pattern while it is
    the eye set in use; while another eye set shares a pattern (blending in or out), the plain one
    (no gag eyes) with that pattern takes it. KKBP has no closed eye keys, so all closed weight (a
    blink, eyes-open slider) goes to the "closed" eye set's key."""
    key_of = {}
    for r in records:
        if r.mesh == mesh and r.category in ("eye", "gag") and r.game_name:
            key_of.setdefault(r.game_name, r.target)
    by_pattern: dict[int, list[dict]] = {}
    for e in eyesets:
        by_pattern.setdefault(int(e["pattern"]), []).append(e)
    frames = channel.shape[0]
    current = np.nan_to_num(eyeset_track, nan=-1).astype(int)
    out: dict[str, np.ndarray] = {}

    def add(eyeset: dict, w: np.ndarray):
        key = key_of.get(eyeset["name"])
        if key is None:
            return
        out[key] = out.get(key, np.zeros(frames)) + w

    patterns = channel.shape[1] // 2
    closed_total = np.zeros(frames)
    for p in range(patterns):
        w_open = np.nan_to_num(channel[:, 2 * p])
        closed_total += np.nan_to_num(channel[:, 2 * p + 1])
        sets = by_pattern.get(p, [])
        if not sets or not w_open.any():
            continue
        plain = min((e for e in sets if int(e.get("gag", 0)) == 0), key=lambda e: e["id"], default=sets[0])
        for e in sets:
            in_use = current == int(e["id"])
            add(e, np.where(in_use, w_open, 0.0))
        in_use_any = np.isin(current, [int(e["id"]) for e in sets])
        add(plain, np.where(in_use_any, 0.0, w_open))
    closed = next((e for e in eyesets if int(e["id"]) == closed_eyeset), None)
    if closed is not None:
        add(closed, closed_total)
    return out


def tear_weights(status: np.ndarray, records: list[Expression], mesh: str) -> dict[str, np.ndarray]:
    """KKBP's tear keys, in order, are tear levels 1, 2, 3: level n turns on key n alone."""
    tears = [r.target for r in records if r.mesh == mesh and r.category == "tears"]
    if not tears:
        return {}
    level = np.nan_to_num(status[:, STATUS.index("tears_level")]).round().astype(int)
    return {key: (level == i + 1).astype(float) for i, key in enumerate(tears)}
