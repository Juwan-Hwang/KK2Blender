"""Studio's scene effects (bloom, fog, vignette, depth of field, ambient occlusion) in Blender.

Bloom, fog and vignette are a compositor node group (Blender 5's scene.compositing_node_group);
depth of field is the camera's own, focused where Studio's camera orbits, as Studio's is. The
Unity effects and Blender's don't share units, so the values are mapped to look alike rather
than converted exactly.
"""

from __future__ import annotations

import bpy

GROUP_NAME = "KK2Blender Effects"


def _enabled(effects: dict, key: str) -> dict | None:
    e = (effects or {}).get(key)
    return e if isinstance(e, dict) and e.get("enable") else None


def _set_enum(socket, *choices):
    for c in choices:
        try:
            socket.default_value = c
            return True
        except (TypeError, ValueError):
            continue
    return False


def apply(effects: dict | None, extra_bloom: float = 0.0):
    """Builds the compositor group for the scene. `extra_bloom` is the lighting preset's own bloom,
    used when Studio's bloom is off."""
    scene = bpy.context.scene
    effects = effects or {}
    bloom = _enabled(effects, "bloom")
    fog = _enabled(effects, "fog")
    vignette = _enabled(effects, "vignette")
    if not (bloom or fog or vignette or extra_bloom > 0):
        if getattr(scene, "compositing_node_group", None) is not None and scene.compositing_node_group.name == GROUP_NAME:
            scene.compositing_node_group = None
        return None

    old = bpy.data.node_groups.get(GROUP_NAME)
    if old is not None:
        bpy.data.node_groups.remove(old)
    tree = bpy.data.node_groups.new(GROUP_NAME, "CompositorNodeTree")
    tree.interface.new_socket("Image", in_out="OUTPUT", socket_type="NodeSocketColor")
    nodes, links = tree.nodes, tree.links
    if fog:
        bpy.context.view_layer.use_pass_mist = True
    layers = nodes.new("CompositorNodeRLayers")
    layers.location = (-600, 0)
    out = nodes.new("NodeGroupOutput")
    out.location = (600, 0)
    current = layers.outputs["Image"]
    x = -350

    if fog:
        world = scene.world or bpy.data.worlds.new("KK2Blender World")
        scene.world = world
        ms = world.mist_settings
        ms.start = float(fog.get("start", 0.0))
        ms.depth = max(float(fog.get("height", 10.0)) * 4.0, 5.0)
        ms.falloff = "QUADRATIC"
        mist = layers.outputs.get("Mist")
        if mist is not None:
            mix = nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            mix.location = (x, 0)
            color = fog.get("color", [0.5, 0.5, 0.5, 1.0])
            links.new(mist, mix.inputs[0])
            links.new(current, mix.inputs[6])
            mix.inputs[7].default_value = (color[0], color[1], color[2], 1.0)
            current = mix.outputs[2]
            x += 250

    strength = 0.0
    threshold = 1.0
    if bloom:
        strength = max(float(bloom.get("intensity", 0.5)), 0.0) * 0.6
        threshold = float(bloom.get("threshold", 0.9))
    elif extra_bloom > 0:
        strength, threshold = extra_bloom, 0.95
    if strength > 0:
        glare = nodes.new("CompositorNodeGlare")
        glare.location = (x, 0)
        _set_enum(glare.inputs["Type"], "Bloom", "BLOOM")
        glare.inputs["Threshold"].default_value = threshold
        glare.inputs["Strength"].default_value = min(strength, 1.0)
        if bloom and "Size" in glare.inputs:
            glare.inputs["Size"].default_value = min(max(float(bloom.get("blur", 0.5)), 0.1), 1.0)
        links.new(current, glare.inputs["Image"])
        current = glare.outputs["Image"]
        x += 250

    if vignette:
        mask = nodes.new("CompositorNodeEllipseMask")
        mask.location = (x, -250)
        mask.inputs["Size"].default_value = (1.25, 1.25)
        blur = nodes.new("CompositorNodeBlur")
        blur.location = (x + 150, -250)
        if "Size" in blur.inputs:
            try:
                blur.inputs["Size"].default_value = (200, 200)
            except (TypeError, ValueError):
                blur.inputs["Size"].default_value = 200
        links.new(mask.outputs["Mask"], blur.inputs["Image"])
        mix = nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.location = (x + 300, 0)
        mix.inputs[0].default_value = 0.6
        links.new(current, mix.inputs[6])
        links.new(blur.outputs["Image"], mix.inputs[7])
        current = mix.outputs[2]

    links.new(current, out.inputs[0])
    scene.compositing_node_group = tree
    return tree


def apply_ambient_occlusion(effects: dict | None):
    """Studio's SSAO: EEVEE's raytraced fast GI gives the contact shadows."""
    ao = _enabled(effects or {}, "ao")
    e = bpy.context.scene.eevee
    if ao and hasattr(e, "use_fast_gi"):
        e.use_fast_gi = True
        e.fast_gi_distance = max(float(ao.get("radius", 0.5)), 0.1)
