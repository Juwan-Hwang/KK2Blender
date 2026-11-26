"""Finds which baked transform each KKBP bone is. Pure Python.

The bake names transforms by their path below the character root ("BodyTop/p_cf_body_bone/...",
with "#2" for a second sibling of the same path). A KKBP bone has its game name and a parent, but
KKBP leaves helper transforms out, so its parent chain is a subsequence of the real path. A bone
matches the baked path whose leaf is its name and whose ancestors contain its KKBP ancestors'
names in order, nearest first.
"""

from __future__ import annotations

from dataclasses import dataclass


def _segments(path: str) -> list[str]:
    return [s.split("#", 1)[0] for s in path.split("/") if s]


def _is_subsequence(needle: list[str], hay: list[str]) -> bool:
    it = iter(hay)
    return all(any(h == n for h in it) for n in needle)


@dataclass
class Match:
    path: str
    ambiguous: bool = False


def match_bones(bones, paths, allowed=None) -> dict[str, Match]:
    """
    bones: iterable of (id, name, parent_id).
    paths: baked transform paths.
    allowed: optional {bone_id: set of paths} narrowing a bone's candidates (for example to the
    paths present while that bone's outfit is worn); ignored when it would leave none.
    Returns {bone_id: Match} for bones that were found.
    """
    bones = list(bones)
    by_id = {b[0]: b for b in bones}
    by_leaf: dict[str, list[str]] = {}
    for p in paths:
        segs = _segments(p)
        if segs:
            by_leaf.setdefault(segs[-1], []).append(p)

    def ancestors(bid: str) -> list[str]:
        names = []
        seen = set()
        parent = by_id[bid][2]
        while parent is not None and parent in by_id and parent not in seen:
            seen.add(parent)
            names.append(by_id[parent][1])
            parent = by_id[parent][2]
        return names

    out: dict[str, Match] = {}
    for bid, name, _ in bones:
        candidates = by_leaf.get(name, [])
        if allowed and bid in allowed:
            narrowed = [c for c in candidates if c in allowed[bid]]
            if narrowed:
                candidates = narrowed
        if not candidates:
            continue
        if len(candidates) == 1:
            out[bid] = Match(candidates[0])
            continue
        want = ancestors(bid)
        fitting = [c for c in candidates if _is_subsequence(want, list(reversed(_segments(c)[:-1])))]
        pool = fitting or candidates
        # Prefer the first-seen copy ("#" free), then the shortest path.
        pool.sort(key=lambda c: ("#" in c, len(_segments(c)), c))
        out[bid] = Match(pool[0], ambiguous=len(pool) > 1 and _segments(pool[0]) == _segments(pool[1]))
    return out
