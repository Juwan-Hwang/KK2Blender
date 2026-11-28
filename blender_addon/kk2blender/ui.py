"""The add-on's UI: Import All in File > Import and a KK2Blender tab in the 3D Viewport sidebar."""

from __future__ import annotations

import bpy
from bpy.props import BoolProperty, EnumProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from . import build, kkbp_link, lighting, render_settings

LIGHTING_ITEMS = [(p.id, p.label, p.description) for p in lighting.PRESETS.values()]
RENDER_ITEMS = [(p.id, p.label, "") for p in render_settings.PRESETS.values()]


class KK2B_OT_import_all(bpy.types.Operator, ImportHelper):
    """Import a scene exported by KK2Blender's Export All: characters, animation, camera, lights"""

    bl_idname = "kk2b.import_all"
    bl_label = "Import All"
    bl_options = {"REGISTER", "UNDO"}

    filename_ext = ".json"
    filter_glob: StringProperty(default="scene.json", options={"HIDDEN"})
    lighting: EnumProperty(name="Lighting", items=LIGHTING_ITEMS, default="soft")
    render: EnumProperty(name="Render", items=RENDER_ITEMS, default="final")
    characters: BoolProperty(name="Characters", default=True, description="Import characters with KKBP")
    map_library: BoolProperty(name="Map from library", default=True,
                              description="Link the map from a kkbp-maps asset library when one has it")
    light_factor: FloatProperty(name="Studio light strength", default=1.0, min=0.0, soft_max=4.0)

    def execute(self, context):
        try:
            report = build.import_scene(self.filepath, self.lighting, self.render, self.characters,
                                        self.map_library, self.light_factor)
        except Exception as e:
            self.report({"ERROR"}, str(e))
            return {"CANCELLED"}
        for w in report.warnings:
            self.report({"WARNING"}, w)
        context.scene["kk2b_report"] = "\n".join(report.lines + report.warnings)
        self.report({"INFO"}, report.lines[-1] if report.lines else "Imported")
        return {"FINISHED"}


class KK2B_OT_apply_lighting(bpy.types.Operator):
    """Switch the lighting preset"""

    bl_idname = "kk2b.apply_lighting"
    bl_label = "Apply Lighting"
    bl_options = {"REGISTER", "UNDO"}

    preset: EnumProperty(name="Lighting", items=LIGHTING_ITEMS)

    def execute(self, context):
        studio = [o for o in context.scene.objects if o.type == "LIGHT" and "kk2b_owner" in o]
        lighting.apply(self.preset, context.scene.camera, studio)
        return {"FINISHED"}


class KK2B_OT_apply_render(bpy.types.Operator):
    """Switch the render preset"""

    bl_idname = "kk2b.apply_render"
    bl_label = "Apply Render Preset"
    bl_options = {"REGISTER", "UNDO"}

    preset: EnumProperty(name="Render", items=RENDER_ITEMS)

    def execute(self, context):
        r = context.scene.render
        render_settings.apply(self.preset, r.resolution_x / max(r.resolution_y, 1))
        return {"FINISHED"}


class KK2B_PT_panel(bpy.types.Panel):
    bl_label = "KK2Blender"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "KK2Blender"

    def draw(self, context):
        col = self.layout.column()
        col.operator(KK2B_OT_import_all.bl_idname, icon="IMPORT")
        if not kkbp_link.available():
            col.label(text="KKBP isn't enabled: characters can't be imported", icon="ERROR")
        elif not kkbp_link.pillow_ready():
            col.label(text="KKBP can't load Pillow: characters can't be imported", icon="ERROR")
            col.label(text="KK2Blender ships Pillow: reinstall it from its zip and restart Blender")
            if hasattr(bpy.types, "KKBP_OT_install_pillow"):  # KKBP's own download, as a fallback
                col.operator("kkbp.install_pillow", icon="IMPORT")
        col.separator()
        col.label(text="Lighting")
        row = col.row(align=True)
        for p in lighting.PRESETS.values():
            row.operator(KK2B_OT_apply_lighting.bl_idname, text=p.label).preset = p.id
        col.label(text="Render")
        grid = col.grid_flow(columns=2, align=True)
        for p in render_settings.PRESETS.values():
            grid.operator(KK2B_OT_apply_render.bl_idname, text=p.label).preset = p.id
        report = context.scene.get("kk2b_report")
        if report:
            box = col.box()
            for line in report.splitlines()[-8:]:
                box.label(text=line)


def menu_import(self, context):
    self.layout.operator(KK2B_OT_import_all.bl_idname, text="KK2Blender scene (scene.json)")


CLASSES = (KK2B_OT_import_all, KK2B_OT_apply_lighting, KK2B_OT_apply_render, KK2B_PT_panel)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)
