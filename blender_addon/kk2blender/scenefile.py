"""Reads an export folder: scene.json and where each character's KKBP bundle is. Pure Python."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from . import k2t

FORMAT = "kk2blender-scene"
SUPPORTED_VERSION = 1


class ExportFolderError(Exception):
    pass


@dataclass
class ExportFolder:
    path: Path
    scene: dict
    time: k2t.TrackFile | None
    scene_tracks: k2t.TrackFile | None
    bundles: dict[str, Path] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)

    @property
    def fps(self) -> int:
        return int(self.scene["bake"]["fps"])

    @property
    def frame_count(self) -> int:
        return int(self.scene["bake"]["frame_count"])

    def character_tracks(self, character: dict) -> k2t.TrackFile:
        return k2t.read(self.path / character["tracks"])


def open_folder(path) -> ExportFolder:
    path = Path(path)
    if path.is_file():
        path = path.parent
    scene_path = path / "scene.json"
    if not scene_path.exists():
        raise ExportFolderError(f"{path} has no scene.json: choose a folder written by KK2Blender's Export All")
    scene = json.loads(scene_path.read_text(encoding="utf-8"))
    if scene.get("format") != FORMAT:
        raise ExportFolderError(f"{scene_path} is not a KK2Blender scene")
    if int(scene.get("version", 0)) > SUPPORTED_VERSION:
        raise ExportFolderError(f"{scene_path} is format version {scene['version']}; this add-on reads up to "
                                f"{SUPPORTED_VERSION}. Update the add-on.")
    if not scene.get("complete"):
        raise ExportFolderError(f"{scene_path} is from an export that didn't finish")
    tracks = scene.get("bake", {}).get("tracks", {})
    folder = ExportFolder(
        path, scene,
        k2t.read(path / tracks["time"]) if "time" in tracks else None,
        k2t.read(path / tracks["scene"]) if "scene" in tracks else None)
    folder.bundles = find_bundles(folder)
    return folder


def _manifest(bundle: Path) -> dict | None:
    try:
        return json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def find_bundles(folder: ExportFolder) -> dict[str, Path]:
    """{character id: KKBP bundle folder}. KKBP's batch export writes one bundle per card, in card
    order (c00.png, c01.png, …), into folders named "<yyyyMMddHHmmss>_<name>"; its result file lists
    them. Without the result, bundles are taken in folder-name order and checked by character name."""
    chars = folder.scene.get("characters", [])
    info = folder.scene.get("kkbp") or {}
    out_dir = folder.path / info.get("output", "meshes")
    found: dict[str, Path] = {}
    if not chars:
        return found

    result_path = info.get("result")
    if result_path:
        try:
            result = json.loads(Path(result_path).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            result = None
        if result and not result.get("failures") and len(result.get("bundles", [])) == len(chars):
            for ch, b in zip(chars, result["bundles"]):
                p = Path(b)
                if not p.exists():
                    p = out_dir / p.name  # the folder moved with the export (another machine)
                if p.exists():
                    found[ch["id"]] = p
            if len(found) == len(chars):
                return found
            found.clear()

    if not out_dir.is_dir():
        folder.notes.append(f"No character meshes yet: {out_dir} is missing. Start the main game once so KKBP exports them.")
        return found
    bundles = sorted((d for d in out_dir.iterdir() if (d / "manifest.json").exists()), key=lambda d: d.name)
    remaining = list(bundles)
    for ch in chars:
        for b in remaining:
            m = _manifest(b) or {}
            if _same_name(m.get("character_name"), ch.get("name")):
                found[ch["id"]] = b
                remaining.remove(b)
                break
        else:
            folder.notes.append(f"No KKBP bundle found for {ch.get('name')} ({ch['id']}).")
    return found


def _same_name(a, b) -> bool:
    norm = lambda s: " ".join((s or "").split()).casefold()
    return norm(a) == norm(b)
