"""Automatic lighting: presets that light the scene well with no manual work.

Studio lights the characters with one directional "character light" that follows the camera. The
presets keep that idea: a rig of sun lights parented to the camera, so the characters stay lit the
same way however the camera moves, plus a world colour for the ambient term. Suns have no position,
so the rig works at any scene scale.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import bpy
from mathutils import Euler

RIG_NAME = "KK2Blender Light Rig"


@dataclass(frozen=True)
class Sun:
    name: str
    yaw: float  # degrees, around the camera's up axis; 0 = from behind the camera, + = from the left
    pitch: float  # degrees above the camera's horizon
    strength: float
    color: tuple
    angle: float = 3.0  # degrees of softness
    shadow: bool = True


@dataclass(frozen=True)
class Preset:
    id: str
    label: str
    description: str
    world: tuple  # RGB
    world_strength: float
    suns: tuple
    keep_studio_lights: bool = False
    exposure: float = 0.0
    bloom: float = 0.0


PRESETS = {
    p.id: p for p in (
        Preset("studio", "Studio match", "Studio's own lights as they were set, with Studio's ambient.",
               (0.55, 0.55, 0.58), 0.8, (), keep_studio_lights=True),
        Preset("soft", "Soft anime", "A soft key from the upper left of the camera, a gentle fill and a "
               "thin rim; clean, even skin. The default.",
               (0.62, 0.64, 0.70), 0.7,
               (Sun("Key", 35, 40, 3.2, (1.0, 0.97, 0.93), 4.0),
                Sun("Fill", -50, 10, 0.8, (0.88, 0.92, 1.0), 15.0, shadow=False),
                Sun("Rim", 160, 25, 2.4, (1.0, 1.0, 1.0), 2.0, shadow=False)),
               bloom=0.15),
        Preset("cinematic", "Cinematic", "Low warm key from the side, cool ambient, strong rim and bloom.",
               (0.30, 0.36, 0.48), 0.45,
               (Sun("Key", 70, 18, 4.0, (1.0, 0.86, 0.70), 2.0),
                Sun("Rim", -150, 30, 3.5, (0.75, 0.85, 1.0), 1.5, shadow=False)),
               exposure=-0.2, bloom=0.35),
        Preset("daylight", "Daylight", "A bright high sun and sky, for outdoor maps.",
               (0.55, 0.70, 0.95), 1.0,
               (Sun("Sun", 30, 55, 4.5, (1.0, 0.96, 0.88), 1.0),),
               bloom=0.1),
    )
}


def _world(color, strength):
    world = bpy.context.scene.world or bpy.data.worlds.new("KK2Blender World")
    bpy.context.scene.world = world
    world.use_nodes = True
    nodes = world.node_tree.nodes
    bg = next((n for n in nodes if n.type == "BACKGROUND"), None)
    if bg is None:
        bg = nodes.new("ShaderNodeBackground")
        out = next((n for n in nodes if n.type == "OUTPUT_WORLD"), None) or nodes.new("ShaderNodeOutputWorld")
        world.node_tree.links.new(bg.outputs[0], out.inputs[0])
    bg.inputs["Color"].default_value = (*color, 1.0)
    bg.inputs["Strength"].default_value = strength


def remove_rig():
    rig = bpy.data.objects.get(RIG_NAME)
    if rig is None:
        return
    for child in list(rig.children):
        data = child.data
        bpy.data.objects.remove(child)
        if data is not None and data.users == 0:
            bpy.data.lights.remove(data)
    bpy.data.objects.remove(rig)


def apply(preset_id: str, camera: bpy.types.Object | None, studio_lights=()):
    """Sets the world, replaces the light rig and shows or hides the Studio lights."""
    preset = PRESETS[preset_id]
    scene = bpy.context.scene
    _world(preset.world, preset.world_strength)
    remove_rig()
    for obj in studio_lights:
        obj.hide_render = obj.hide_viewport = not preset.keep_studio_lights
    if preset.suns:
        rig = bpy.data.objects.new(RIG_NAME, None)
        scene.collection.objects.link(rig)
        if camera is not None:
            rig.parent = camera
        for sun in preset.suns:
            data = bpy.data.lights.new(f"KK2Blender {sun.name}", "SUN")
            data.energy = sun.strength
            data.color = sun.color
            data.angle = math.radians(sun.angle)
            data.use_shadow = sun.shadow
            obj = bpy.data.objects.new(data.name, data)
            scene.collection.objects.link(obj)
            obj.parent = rig
            # Camera space: the camera looks down -Z with +Y up. A sun shines down its own -Z, so it
            # starts aligned with the view (light from behind the camera), is tipped to come from
            # above (X, applied first) and swung to come from the left for a positive yaw (Y).
            obj.rotation_euler = Euler((math.radians(-sun.pitch), math.radians(-sun.yaw), 0.0), "XYZ")
    scene.view_settings.exposure = preset.exposure
    scene["kk2b_bloom"] = preset.bloom
    scene["kk2b_lighting"] = preset.id
