"""Headless rendering for KK2Blender scenes.

    # Build the scene from an export folder and render it:
    blender -b --python render/render.py -- --import /share/exports/kk2b_20261127_010203 --preset final

    # Render a saved .blend:
    blender -b scene.blend --python render/render.py -- --preset final --out /scratch/shot01

Frames go to <out>/frame_NNNNN.png. Frames already there are skipped, so a stopped render picks up
where it left off. With ffmpeg on PATH, the frames are joined into <out>/<name>.mp4 at the end.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

import bpy

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "blender_addon"))


def parse(argv):
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser(prog="render.py")
    p.add_argument("--import", dest="import_folder", help="KK2Blender export folder to build the scene from")
    p.add_argument("--lighting", default="soft", help="lighting preset (studio, soft, cinematic, daylight)")
    p.add_argument("--preset", default="final", help="render preset (preview, final, final_4k, cycles)")
    p.add_argument("--out", help="output folder (default: <export>/render or next to the .blend)")
    p.add_argument("--frames", help="frame range like 1-120 (default: the scene's)")
    p.add_argument("--save-blend", action="store_true", help="save the built scene as scene.blend in the output folder")
    p.add_argument("--no-video", action="store_true", help="don't join the frames with ffmpeg")
    p.add_argument("--crf", type=int, default=16, help="ffmpeg quality (lower is better)")
    p.add_argument("--percent", type=int, help="resolution percentage, for quick tests")
    p.add_argument("--samples", type=int, help="override the preset's samples")
    return p.parse_args(argv)


def main():
    args = parse(sys.argv)
    from kk2blender import build, render_settings

    if args.import_folder:
        report = build.import_scene(args.import_folder, args.lighting, args.preset)
        for w in report.warnings:
            print("warning:", w)
        out = Path(args.out or Path(args.import_folder) / "render")
    else:
        if not bpy.data.filepath:
            sys.exit("render.py: give a .blend to render or --import an export folder")
        render_settings.apply(args.preset, bpy.context.scene.render.resolution_x / max(bpy.context.scene.render.resolution_y, 1))
        out = Path(args.out or Path(bpy.data.filepath).parent / "render")
    out.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    if args.save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=str(out / "scene.blend"))

    start, end = scene.frame_start, scene.frame_end
    if args.frames:
        a, _, b = args.frames.partition("-")
        start, end = int(a), int(b or a)

    scene.render.image_settings.file_format = "PNG"
    if args.percent:
        scene.render.resolution_percentage = args.percent
    if args.samples:
        if scene.render.engine == "CYCLES":
            scene.cycles.samples = args.samples
        else:
            scene.eevee.taa_render_samples = args.samples
    total = end - start + 1
    began = time.time()
    done = 0
    for frame in range(start, end + 1):
        target = out / f"frame_{frame:05d}.png"
        if target.exists() and target.stat().st_size > 0:
            continue
        scene.frame_set(frame)
        partial = out / f".frame_{frame:05d}.png"
        scene.render.filepath = str(partial)
        bpy.ops.render.render(write_still=True)
        partial.replace(target)  # only finished frames get their final name
        done += 1
        rate = (time.time() - began) / done
        left = sum(1 for f in range(frame + 1, end + 1) if not (out / f"frame_{f:05d}.png").exists())
        print(f"frame {frame} ({frame - start + 1}/{total}), {rate:.1f} s/frame, about {left * rate / 60:.1f} min left",
              flush=True)

    if not args.no_video and shutil.which("ffmpeg"):
        name = Path(args.import_folder).name if args.import_folder else Path(bpy.data.filepath).stem
        video = out / f"{name}.mp4"
        cmd = ["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(scene.render.fps / scene.render.fps_base),
               "-start_number", str(start), "-i", str(out / "frame_%05d.png"),
               "-frames:v", str(total), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", str(args.crf),
               "-preset", "slow", str(video)]
        print("joining:", " ".join(cmd), flush=True)
        subprocess.run(cmd, check=True)
        print("video:", video)


main()
