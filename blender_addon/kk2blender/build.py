"""Import All: builds the Blender scene from an export folder."""

from __future__ import annotations

import time
from dataclasses import dataclass, field

import bpy

from . import character, effects, kkbp_link, lighting, render_settings, scenefile, staging

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
    staging.apply_depth_of_field(camera, folder, FRAME_START)
    if camera is not None:
        staging.build_studio_cameras(folder, FRAME_START, stage, camera.data.lens)
    map_root = staging.build_map(folder, FRAME_START, stage, report.info)
    owners = ("studio_light", "camera", "map") if map_root is not None else ("studio_light", "camera")
    studio_lights = staging.build_lights(folder.scene_tracks, FRAME_START, stage, which=owners, factor=light_factor)
    staging.build_items(folder, FRAME_START, stage)
    if map_root is None and map_from_library:
        staging.link_map(folder, stage, report.info)

    if import_characters and folder.scene.get("characters"):
        if not kkbp_link.available():
            report.warn("KKBP isn't enabled, so characters were skipped. Install KKBP (Blender 5.2+) and import again.")
        elif not kkbp_link.pillow_ready():
            report.warn("KKBP needs Pillow, so characters were skipped. Click Download Pillow in the KK2Blender or KKBP sidebar tab, then import again.")
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
                                    lambda text, n=ch_info["name"]: report.info(f"{n}: {text}"), folder.path)
                    report.info(f"{ch_info['name']}: done in {time.time() - t0:.1f} s")
                except Exception as e:  # one character failing shouldn't lose the rest of the scene
                    import traceback
                    traceback.print_exc()  # the system console keeps the details
                    report.warn(f"{ch_info['name']}: {type(e).__name__}: {e}")

    aspect = 16 / 9
    screen = folder.scene.get("screen")
    if screen and screen[1]:
        aspect = screen[0] / screen[1]
    lighting.apply(lighting_preset, camera, studio_lights)
    render_settings.apply(render_preset, aspect)
    effects.apply(folder.scene.get("effects"), extra_bloom=lighting.PRESETS[lighting_preset].bloom)
    effects.apply_ambient_occlusion(folder.scene.get("effects"))
    scene.frame_set(FRAME_START)
    report.info(f"scene built in {time.time() - started:.1f} s: {folder.frame_count} frames at {folder.fps} fps")
    return report
