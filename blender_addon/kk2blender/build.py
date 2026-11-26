"""Import All: builds the Blender scene from an export folder."""

from __future__ import annotations

import time
from dataclasses import dataclass, field

import bpy

from . import character, kkbp_link, lighting, render_settings, scenefile, staging

FRAME_START = 1


@dataclass
class Report:
    lines: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    def info(self, text):
        print("KK2Blender:", text)
        self.lines.append(text)

    def warn(self, text):
        print("KK2Blender warning:", text)
        self.warnings.append(text)


def _clear_startup_scene():
    """Removes Blender's startup cube, camera and light when the file holds nothing else, so the
    imported scene starts clean (the same rule KKBP uses)."""
    defaults = {"Cube": "MESH", "Camera": "CAMERA", "Light": "LIGHT"}
    objs = list(bpy.data.objects)
    if len(objs) != 3 or any(defaults.get(o.name) != o.type for o in objs):
        return
    stores = {"MESH": bpy.data.meshes, "CAMERA": bpy.data.cameras, "LIGHT": bpy.data.lights}
    for o in objs:
        data, kind = o.data, o.type
        bpy.data.objects.remove(o)
        if data is not None and data.users == 0:
            stores[kind].remove(data)


def import_scene(path, lighting_preset="soft", render_preset="final", import_characters=True,
                 map_from_library=True, light_factor=1.0) -> Report:
    report = Report()
    started = time.time()
    folder = scenefile.open_folder(path)
    for note in folder.notes:
        report.warn(note)
    _clear_startup_scene()
    scene = bpy.context.scene
    scene.render.fps = folder.fps
    scene.render.fps_base = 1.0
    scene.frame_start = FRAME_START
    scene.frame_end = FRAME_START + folder.frame_count - 1
    scene.frame_set(FRAME_START)
    scene["kk2b_folder"] = str(folder.path)

    root = staging._collection(f"KK2Blender {folder.path.name}")
    stage = bpy.data.collections.new("Stage")
    root.children.link(stage)

    camera = staging.build_camera(folder.scene_tracks, FRAME_START, stage)
    studio_lights = staging.build_lights(folder.scene_tracks, FRAME_START, stage, factor=light_factor)
    staging.build_items(folder, FRAME_START, stage)
    if map_from_library:
        staging.link_map(folder, stage, report.info)

    if import_characters and folder.scene.get("characters"):
        if not kkbp_link.available():
            report.warn("KKBP isn't enabled, so characters were skipped. Install KKBP (Blender 5.2+) and import again.")
        else:
            for ch_info in folder.scene.get("characters", []):
                bundle_path = folder.bundles.get(ch_info["id"])
                if bundle_path is None:
                    continue
                t0 = time.time()
                try:
                    ch, result = kkbp_link.import_character(bundle_path)
                    for w in result.warnings:
                        report.warn(f"{ch_info['name']}: KKBP: {w}")
                    tracks = folder.character_tracks(ch_info)
                    character.apply(ch, ch.bundle(), tracks, FRAME_START,
                                    lambda text, n=ch_info["name"]: report.info(f"{n}: {text}"))
                    report.info(f"{ch_info['name']}: done in {time.time() - t0:.1f} s")
                except Exception as e:  # one character failing shouldn't lose the rest of the scene
                    report.warn(f"{ch_info['name']}: {e}")

    aspect = 16 / 9
    screen = folder.scene.get("screen")
    if screen and screen[1]:
        aspect = screen[0] / screen[1]
    lighting.apply(lighting_preset, camera, studio_lights)
    render_settings.apply(render_preset, aspect)
    scene.frame_set(FRAME_START)
    report.info(f"scene built in {time.time() - started:.1f} s: {folder.frame_count} frames at {folder.fps} fps")
    return report
