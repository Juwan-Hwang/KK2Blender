"""Applies one character's bake to its KKBP import: bones, root, expressions, visibility, eye looks."""

from __future__ import annotations

import bpy
import numpy as np
from mathutils import Matrix

from . import anim, face, k2t, matching, pose, space

OUTFIT = "kkbp_outfit"  # KKBP's custom property: the Outfit an object or bone belongs to
ID = "kkbp_id"  # KKBP's custom property: a material's bundle Id


def _np(m: Matrix) -> np.ndarray:
    return np.array([list(r) for r in m], dtype=np.float64)


def _frames(n: int, start: int) -> np.ndarray:
    return np.arange(n, dtype=np.float64) + start


class CharacterBake:
    def __init__(self, ch, bundle, tracks: k2t.TrackFile, frame_start: int, log):
        self.ch = ch
        self.bundle = bundle
        self.tracks = tracks
        self.frame_start = frame_start
        self.log = log
        self.n = tracks.frame_count
        status = tracks.full("status") if "status" in tracks else np.zeros((self.n, len(face.STATUS)))
        self.status = status
        self.coordinate = np.nan_to_num(status[:, 0], nan=-1).round().astype(int)

    # --- bones -------------------------------------------------------------------------------

    def apply_bones(self):
        arm = self.ch.armature
        if arm is None:
            self.log("no armature")
            return
        bone_paths = [n[len("bone:"):] for n in self.tracks.names("bone:")]
        present = {p: self.tracks.present("bone:" + p) for p in bone_paths}
        allowed = {}
        for b in arm.data.bones:
            outfit = b.get(OUTFIT)
            if outfit is not None:
                worn = self.coordinate == int(outfit)
                allowed[b.name] = {p for p, pr in present.items() if (pr & worn).any()}
        skeleton = self.bundle.skeleton
        bones = [(b.id, b.name, b.parent) for b in skeleton.bones if b.id in arm.data.bones]
        matches = matching.match_bones(bones, bone_paths, allowed)
        ambiguous = [bid for bid, m in matches.items() if m.ambiguous]
        if ambiguous:
            self.log(f"{len(ambiguous)} bones matched one of several same-named transforms: {', '.join(ambiguous[:8])}")
        missing = [b[0] for b in bones if b[0] not in matches]
        if missing:
            self.log(f"{len(missing)} KKBP bones have no baked transform and stay at rest: {', '.join(missing[:8])}")

        rest = pose.rest_matrices([pose.RestBone(b.id, b.name, b.parent, b.translation, b.rotation, b.game_scale)
                                   for b in skeleton.bones])
        ml = {b.name: _np(b.matrix_local) for b in arm.data.bones}
        parents = {b.name: (b.parent.name if b.parent else None) for b in arm.data.bones}
        by_id = {b.id: b for b in skeleton.bones}
        posed = {}
        for bid, m in matches.items():
            rows = pose.fill_gaps(self.tracks.full("bone:" + m.path))
            if np.isnan(rows).all():
                continue
            posed[bid] = pose.posed_matrices(rows, rest[bid], ml[bid], by_id[bid].game_scale)
        basis = pose.basis_matrices(posed, parents, ml, self.n)

        # The bake already holds the game's joint corrections and look-at; KKBP's own constraints and
        # drivers on these bones would apply them twice.
        for name in basis:
            pb = arm.pose.bones[name]
            for c in list(pb.constraints):
                pb.constraints.remove(c)
            pb.rotation_mode = "QUATERNION"
        if arm.animation_data:
            for d in list(arm.animation_data.drivers):
                if d.data_path.startswith('pose.bones["') and d.data_path.split('"')[1] in basis:
                    arm.animation_data.drivers.remove(d)

        curves = anim.Curves(arm, f"{arm.name} (KK2Blender)")
        frames = _frames(self.n, self.frame_start)
        for name, mats in basis.items():
            loc, quat, scale = space.decompose(mats)
            path = f'pose.bones["{name}"]'
            for i in range(3):
                curves.curve(path + ".location", i, frames, loc[:, i], group=name)
            for i in range(4):
                curves.curve(path + ".rotation_quaternion", i, frames, quat[:, i], group=name)
            for i in range(3):
                curves.curve(path + ".scale", i, frames, scale[:, i], group=name, eps=1e-5)
        self.log(f"animated {len(basis)} bones over {self.n} frames")

    def apply_root(self):
        """The character root's world transform (Studio position, rotation, scale) on the armature."""
        arm = self.ch.armature
        if arm is None or "root" not in self.tracks:
            return
        rows = pose.fill_gaps(self.tracks.full("root"))
        m = space.unity_to_blender(space.rows_to_matrices(rows)) @ _np(arm.matrix_world)
        loc, quat, scale = space.decompose(m)
        arm.rotation_mode = "QUATERNION"
        curves = anim.Curves(arm, f"{arm.name} (KK2Blender)")
        frames = _frames(self.n, self.frame_start)
        for i in range(3):
            curves.curve("location", i, frames, loc[:, i], group="Object Transforms")
        for i in range(4):
            curves.curve("rotation_quaternion", i, frames, quat[:, i], group="Object Transforms")
        for i in range(3):
            curves.curve("scale", i, frames, scale[:, i], group="Object Transforms", eps=1e-5)

    # --- expressions -------------------------------------------------------------------------

    def apply_face(self):
        records = [face.Expression(r.mesh, r.target, r.category, r.name, r.game_name)
                   for r in self.bundle.expressions.records]
        if not records:
            return
        header = self.tracks.header
        eyesets = header.get("eyesets", [])
        weights: dict[str, np.ndarray] = {}
        for mesh in sorted({r.mesh for r in records}):
            for cat in ("mouth", "eyebrow"):
                if f"face:{cat}" in self.tracks:
                    keys = face.pattern_keys(records, mesh, cat)
                    weights.update(face.pattern_weights(self.tracks.full(f"face:{cat}"), keys))
            if "face:eyes" in self.tracks:
                weights.update(face.eye_weights(self.tracks.full("face:eyes"),
                                                self.status[:, face.STATUS.index("eyeset")], eyesets, records, mesh))
            weights.update(face.tear_weights(self.status, records, mesh))
        targets = {r.target for r in records}
        frames = _frames(self.n, self.frame_start)
        done = 0
        for obj in self.ch.parts():
            if obj.type != "MESH" or obj.data.shape_keys is None:
                continue
            key = obj.data.shape_keys
            names = [kb.name for kb in key.key_blocks if kb.name in targets]
            if not names:
                continue
            if key.animation_data:
                for d in list(key.animation_data.drivers):
                    if d.data_path.startswith('key_blocks["') and d.data_path.split('"')[1] in names:
                        key.animation_data.drivers.remove(d)
            curves = anim.Curves(key, f"{obj.name} expressions (KK2Blender)")
            for name in names:
                w = weights.get(name)
                values = np.zeros(self.n) if w is None else np.clip(w, 0.0, 1.0)
                curves.curve(f'key_blocks["{name}"].value', 0, frames, values, group="Expressions")
                done += 1
        self.log(f"animated {done} expression keys")

    # --- visibility --------------------------------------------------------------------------

    def mesh_visibility(self) -> dict[str, np.ndarray]:
        """{KKBP mesh id: (frames,) bool}. A Body Pass mesh shows when its renderer drew; an Outfit
        mesh when its outfit was worn and its renderer drew (outfits share renderer names)."""
        renderers = self.tracks.header.get("renderers", {})
        by_leaf: dict[str, list[str]] = {}
        for path, info in renderers.items():
            by_leaf.setdefault(info.get("name") or path.rsplit("/", 1)[-1], []).append(path)
        out = {}
        for p in self.bundle.passes:
            for mesh in p.meshes:
                paths = by_leaf.get(mesh.name, [])
                if not paths:
                    continue
                vis = np.zeros(self.n, dtype=bool)
                for path in paths:
                    ch = "vis:" + path
                    if ch in self.tracks:
                        vis |= np.nan_to_num(self.tracks.full(ch)[:, 0]) > 0.5
                if p.kind == "outfit" and p.outfit is not None:
                    vis &= self.coordinate == int(p.outfit)
                out[mesh.id] = vis
        return out

    def apply_visibility(self):
        mesh_vis = self.mesh_visibility()
        if not mesh_vis:
            return
        material_vis: dict[str, np.ndarray] = {}
        for p in self.bundle.passes:
            for mesh in p.meshes:
                if mesh.id not in mesh_vis:
                    continue
                for mat in mesh.materials:
                    material_vis[mat] = material_vis.get(mat, np.zeros(self.n, dtype=bool)) | mesh_vis[mesh.id]
        frames = _frames(self.n, self.frame_start)
        keyed = 0
        for obj in self.ch.parts():
            if obj.type != "MESH":
                continue
            ids = {m.get(ID) for m in obj.data.materials if m is not None and m.get(ID)}
            known = [material_vis[i] for i in ids if i in material_vis]
            if not known:
                continue
            visible = np.logical_or.reduce(known)
            if visible.all():
                continue
            hidden = (~visible).astype(np.float64)
            curves = anim.Curves(obj, f"{obj.name} visibility (KK2Blender)")
            curves.curve("hide_render", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
            curves.curve("hide_viewport", 0, frames, hidden, group="Visibility", interpolation="CONSTANT")
            keyed += 1
        self.log(f"keyed visibility on {keyed} objects")

    # --- eyes --------------------------------------------------------------------------------

    def apply_eye_textures(self):
        """The game moves the pupils and highlights by texture offset; KKBP's eye materials take the
        same textures, so each image node of those materials gets a Mapping node keyed the same way."""
        info = self.tracks.header.get("eye_textures", {})
        if not info:
            return
        offsets = {}
        for ch_name, meta in info.items():
            if ch_name not in self.tracks:
                continue
            data = self.tracks.full(ch_name)
            for i, tex in enumerate(meta.get("textures", [])):
                offsets.setdefault(tex, []).append(data[:, i * 4:(i + 1) * 4])
        if not offsets:
            return
        frames = _frames(self.n, self.frame_start)
        side_of_material = {}
        for m in self.bundle.materials:
            if m.role in ("eye_l", "eye_r"):
                side_of_material[m.id] = 0 if m.role == "eye_l" else 1
        for mat in {m for o in self.ch.parts() if o.type == "MESH" for m in o.data.materials if m}:
            side = side_of_material.get(mat.get(ID))
            if side is None or not mat.use_nodes:
                continue
            tex_data = offsets.get("_MainTex", [])
            if len(tex_data) <= side:
                continue
            uv = tex_data[side]
            if np.allclose(np.nan_to_num(uv), np.nan_to_num(uv[0])):
                continue
            nodes, links = mat.node_tree.nodes, mat.node_tree.links
            for img in [n for n in nodes if n.type == "TEX_IMAGE"]:
                mapping = nodes.new("ShaderNodeMapping")
                mapping.label = "KK2Blender eye look"
                coord = nodes.new("ShaderNodeTexCoord")
                links.new(coord.outputs["UV"], mapping.inputs["Vector"])
                links.new(mapping.outputs["Vector"], img.inputs["Vector"])
                curves = anim.Curves(mat.node_tree, f"{mat.name} eye look (KK2Blender)")
                path = f'nodes["{mapping.name}"].inputs["Location"].default_value'
                spath = f'nodes["{mapping.name}"].inputs["Scale"].default_value'
                curves.curve(path, 0, frames, uv[:, 0], group="Eye look")
                curves.curve(path, 1, frames, uv[:, 1], group="Eye look")
                curves.curve(spath, 0, frames, uv[:, 2], group="Eye look")
                curves.curve(spath, 1, frames, uv[:, 3], group="Eye look")
                break


def apply(ch, bundle, tracks, frame_start, log):
    bake = CharacterBake(ch, bundle, tracks, frame_start, log)
    bake.apply_bones()
    bake.apply_root()
    bake.apply_face()
    bake.apply_visibility()
    try:
        bake.apply_eye_textures()
    except Exception as e:  # eye looks are a nicety; never fail the import over them
        log(f"eye look not applied: {e}")
