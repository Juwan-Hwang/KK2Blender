"""A KK-like toon look for props: the base colour lit in two tones with a soft edge, as KK's item
shaders draw them, instead of a glossy PBR surface. EEVEE only (Shader to RGB); in Cycles the
material falls back to its diffuse colour."""

from __future__ import annotations

import bpy

GROUP = "KK2Blender Toon"


def _group():
    g = bpy.data.node_groups.get(GROUP)
    if g is not None:
        return g
    g = bpy.data.node_groups.new(GROUP, "ShaderNodeTree")
    g.interface.new_socket("Color", in_out="INPUT", socket_type="NodeSocketColor").default_value = (0.8, 0.8, 0.8, 1)
    shade = g.interface.new_socket("Shadow", in_out="INPUT", socket_type="NodeSocketFloat")
    shade.default_value, shade.min_value, shade.max_value = 0.72, 0.0, 1.0
    g.interface.new_socket("Emission", in_out="INPUT", socket_type="NodeSocketColor").default_value = (0, 0, 0, 1)
    g.interface.new_socket("Shader", in_out="OUTPUT", socket_type="NodeSocketShader")
    n, l = g.nodes, g.links
    gin, gout = n.new("NodeGroupInput"), n.new("NodeGroupOutput")
    diffuse = n.new("ShaderNodeBsdfDiffuse")
    to_rgb = n.new("ShaderNodeShaderToRGB")
    ramp = n.new("ShaderNodeValToRGB")
    ramp.color_ramp.interpolation = "EASE"
    ramp.color_ramp.elements[0].position = 0.18
    ramp.color_ramp.elements[1].position = 0.26
    lo = n.new("ShaderNodeMath")
    lo.operation = "MAXIMUM"
    bw = n.new("ShaderNodeRGBToBW")
    mix = n.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.blend_type = "MULTIPLY"
    mix.inputs[0].default_value = 1.0
    emit = n.new("ShaderNodeEmission")
    add = n.new("ShaderNodeAddShader")
    emit2 = n.new("ShaderNodeEmission")
    l.new(diffuse.outputs[0], to_rgb.inputs[0])
    l.new(to_rgb.outputs["Color"], bw.inputs[0])
    l.new(bw.outputs[0], ramp.inputs[0])
    l.new(ramp.outputs["Color"], lo.inputs[0])
    l.new(gin.outputs["Shadow"], lo.inputs[1])
    l.new(gin.outputs["Color"], mix.inputs[6])
    l.new(lo.outputs[0], mix.inputs[7])
    l.new(mix.outputs[2], emit.inputs["Color"])
    l.new(gin.outputs["Emission"], emit2.inputs["Color"])
    l.new(emit.outputs[0], add.inputs[0])
    l.new(emit2.outputs[0], add.inputs[1])
    l.new(add.outputs[0], gout.inputs["Shader"])
    return g


def apply(mat):
    """Replaces a glTF material's Principled surface with the toon group, keeping its base colour
    (texture or value), alpha and emission."""
    if mat is None or not mat.use_nodes or mat.get("kk2b_toon"):
        return
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next((x for x in nodes if x.type == "BSDF_PRINCIPLED"), None)
    out = next((x for x in nodes if x.type == "OUTPUT_MATERIAL"), None)
    if bsdf is None or out is None:
        return
    toon = nodes.new("ShaderNodeGroup")
    toon.node_tree = _group()
    toon.location = (bsdf.location.x, bsdf.location.y - 400)
    for name, target in (("Base Color", "Color"), ("Emission Color", "Emission")):
        src = bsdf.inputs.get(name)
        if src is None:
            continue
        link = next((x for x in links if x.to_socket == src), None)
        if link is not None:
            links.new(link.from_socket, toon.inputs[target])
        elif name == "Base Color":
            toon.inputs[target].default_value = src.default_value
    surface = toon.outputs["Shader"]
    alpha = bsdf.inputs.get("Alpha")
    alpha_link = next((x for x in links if alpha is not None and x.to_socket == alpha), None)
    if alpha_link is not None or (alpha is not None and alpha.default_value < 1.0):
        transparent = nodes.new("ShaderNodeBsdfTransparent")
        mix = nodes.new("ShaderNodeMixShader")
        if alpha_link is not None:
            links.new(alpha_link.from_socket, mix.inputs[0])
        else:
            mix.inputs[0].default_value = alpha.default_value
        links.new(transparent.outputs[0], mix.inputs[1])
        links.new(surface, mix.inputs[2])
        surface = mix.outputs[0]
    links.new(surface, out.inputs["Surface"])
    mat["kk2b_toon"] = True
