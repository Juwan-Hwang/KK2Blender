"""Render presets: the engine, samples, colour management and output for a finished render."""

from __future__ import annotations

from dataclasses import dataclass

import bpy


@dataclass(frozen=True)
class RenderPreset:
    id: str
    label: str
    engine: str
    samples: int
    width: int
    raytracing: bool = True
    denoise: bool = True


PRESETS = {
    p.id: p for p in (
        RenderPreset("preview", "Preview (EEVEE, 720p)", "BLENDER_EEVEE", 16, 1280, raytracing=False),
        RenderPreset("final", "Final (EEVEE, 1080p)", "BLENDER_EEVEE", 64, 1920),
        RenderPreset("final_4k", "Final (EEVEE, 4K)", "BLENDER_EEVEE", 96, 3840),
        RenderPreset("cycles", "Cycles (1080p)", "CYCLES", 256, 1920),
    )
}


def _even(v: float) -> int:
    v = int(round(v))
    return v + (v % 2)


def apply(preset_id: str, aspect: float = 16 / 9, output: str = "//render/frame_"):
    p = PRESETS[preset_id]
    scene = bpy.context.scene
    r = scene.render
    r.engine = p.engine
    r.resolution_x = p.width
    r.resolution_y = _even(p.width / max(aspect, 0.1))
    r.resolution_percentage = 100
    r.film_transparent = False
    r.filepath = output
    r.image_settings.file_format = "PNG"
    r.image_settings.color_mode = "RGB"
    r.use_persistent_data = True

    # Anime colours: the Standard view keeps KK's flat colours; AgX/Filmic would desaturate them.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"

    if p.engine == "BLENDER_EEVEE":
        e = scene.eevee
        e.taa_render_samples = p.samples
        e.taa_samples = 16
        for attr, value in (("use_raytracing", p.raytracing), ("use_shadows", True),
                            ("shadow_resolution_scale", 1.0), ("use_gtao", True)):
            if hasattr(e, attr):
                setattr(e, attr, value)
    else:
        c = scene.cycles
        c.samples = p.samples
        c.use_adaptive_sampling = True
        c.adaptive_threshold = 0.02
        c.use_denoising = p.denoise
        c.device = "GPU"
        prefs = bpy.context.preferences.addons.get("cycles")
        if prefs is not None:
            cp = prefs.preferences
            for backend in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
                try:
                    cp.compute_device_type = backend
                    cp.get_devices()
                    if any(d.type == backend for d in cp.devices):
                        for d in cp.devices:
                            d.use = d.type == backend
                        break
                except (TypeError, ValueError):
                    continue
            else:
                c.device = "CPU"
    scene["kk2b_render"] = p.id
