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

    def raw_shape_weights(self) -> dict[str, np.ndarray]:
        """{"raw:<game blend shape name>": (frames,) weight 0..1} from every skinned renderer's baked
        blend shape weights. KK's blend shape names carry their mesh ("eye_face.f00_def_op",
        "kuti_ha.ha00_def_op"), so a name means the same shape wherever it is."""
        out: dict[str, np.ndarray] = {}
        for path, info in self.tracks.header.get("renderers", {}).items():
            names = info.get("blend_shapes")
            ch = "shape:" + path
            if not names or ch not in self.tracks:
                continue
            data = self.tracks.full(ch)
            if data.shape[1] != len(names):
                continue
            for i, name in enumerate(names):
                key = "raw:" + name
                w = np.nan_to_num(data[:, i]) / 100.0
                # A shape on a renderer that isn't drawn yet (an outfit not worn) mustn't win.
                if key not in out or (np.abs(w).max() > np.abs(out[key]).max()):
                    out[key] = w
        return out

    def apply_raw_face(self) -> int:
        """Keys the game's own blend shapes, exported as "raw:" shape keys, with the game's weights
        every frame: blinks, pattern blends, mouth movement and every plugin's shape changes exactly.
        Returns how many keys were driven; 0 when the bundle has no raw keys."""
        weights = None
        frames = _frames(self.n, self.frame_start)
        done = 0
        for obj in self.ch.parts():
            if obj.type != "MESH" or obj.data.shape_keys is None:
                continue
            key = obj.data.shape_keys
            raw = [kb.name for kb in key.key_blocks if kb.name.startswith("raw:")]
            if not raw:
                continue
            if weights is None:
                weights = self.raw_shape_weights()
            if key.animation_data:
                for d in list(key.animation_data.drivers):
                    if d.data_path.startswith('key_blocks["raw:'):
                        key.animation_data.drivers.remove(d)
            curves = anim.Curves(key, f"{obj.name} face (KK2Blender)")
            for name in raw:
                w = weights.get(name)
                if w is None:
                    continue
                curves.curve(f'key_blocks["{name}"].value', 0, frames, np.clip(w, 0.0, 1.0), group="Game shapes")
                done += 1
            # The finished pattern keys would add to the same shapes: they stay at 0. Tear and gag-eye
            # keys also show their meshes (KKBP rests those inside the head), so they stay driven.
            pattern_keys = {r.target for r in self.bundle.expressions.records
                            if r.category in ("eye", "mouth", "eyebrow")}
            for kb in key.key_blocks:
                if kb.name in pattern_keys:
                    kb.value = 0.0
        if done:
            self.log(f"animated {done} game blend shapes")
            self.apply_gag_eyes()
        return done

    def apply_gag_eyes(self):
        """With the game's shapes driving the face, KKBP's gag eyes (ADR 0042: one "Gag eyes" key, a
        dropdown on the gag material and correction keys) still show the gag mesh, its texture and
        hide the pupils. Their face shapes would come on top of the game's, so those keys are made
        flat, and the key and the dropdown's number are keyed from the eye set in use."""
        import sys
        from . import kkbp_link
        api = kkbp_link.api()
        pkg = api.__name__.rpartition(".")[0]
        gag = sys.modules.get(pkg + ".steps.gag")
        templates = sys.modules.get(pkg + ".templates")
        if gag is None or templates is None:
            return
        targets = gag.gag_expressions(self.bundle)
        if not targets:
            return
        owner = next((o for o in self.ch.parts() if o.type == "MESH" and o.data.shape_keys
                      and gag.GAG_KEY in o.data.shape_keys.key_blocks), None)
        if owner is None:
            return
        # Flatten the gag key and every correction key.
        for obj in self.ch.parts():
            if obj.type != "MESH" or obj.data.shape_keys is None:
                continue
            kbs = obj.data.shape_keys.key_blocks
            names = [gag.GAG_KEY] + list(gag.corrections(obj))
            for name in names:
                kb = kbs.get(name)
                if kb is None:
                    continue
                base = kb.relative_key
                co = np.empty(len(base.data) * 3, dtype=np.float32)
                base.data.foreach_get("co", co)
                kb.data.foreach_set("co", co)
        # Which gag expression each frame's eye set is, by the game name KKBP recorded.
        by_name = {}
        for r in self.bundle.expressions.records:
            if r.category == "gag" and r.game_name in targets or (r.target in targets and r.game_name):
                by_name.setdefault(r.game_name, targets.index(r.target) + 1)
        eyesets = {int(e["id"]): e for e in self.tracks.header.get("eyesets", [])}
        current = np.nan_to_num(self.status[:, face.STATUS.index("eyeset")], nan=-1).astype(int)
        number = np.zeros(self.n)
        for f, eid in enumerate(current):
            e = eyesets.get(int(eid))
            if e is not None and int(e.get("gag", 0)) > 0:
                number[f] = by_name.get(e.get("name"), 0)
        on = (number > 0).astype(float)
        frames = _frames(self.n, self.frame_start)
        anim.Curves(owner.data.shape_keys, "").curve(f'key_blocks["{gag.GAG_KEY}"].value', 0, frames, on,
                                                     group="Gag eyes", interpolation="CONSTANT")
        for mat in {m for o in self.ch.parts() if o.type == "MESH" for m in o.data.materials if m}:
            try:
                _, number_path = templates.picker_paths(mat)
            except Exception:
                continue
            if not number_path:
                continue
            try:
                mat.path_resolve(number_path)
            except ValueError:
                continue
            anim.Curves(mat, f"{mat.name} gag (KK2Blender)").curve(number_path, 0, frames, number,
                                                                    group="Gag eyes", interpolation="CONSTANT")
        self.log(f"gag eyes keyed on {int(on.sum())} frames")

    def apply_face(self):
        raw = self.apply_raw_face() > 0
        records = [face.Expression(r.mesh, r.target, r.category, r.name, r.game_name)
                   for r in self.bundle.expressions.records]
        if raw:
            # Exact game shapes drive the face; tears still need their showing keys (gag eyes: apply_gag_eyes).
            records = [r for r in records if r.category == "tears"]
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

    def apply_outfit_schedule(self):
        """Keys the outfit worn on each frame on the armature, for the body textures (outfits.py)."""
        arm = self.ch.armature
        if arm is None or not len(self.coordinate):
            return
        from . import outfits
        arm[outfits.PROP] = float(self.coordinate[0])
        arm[outfits.KEY] = self.ch.key
        anim.Curves(arm, "").curve(f'["{outfits.PROP}"]', 0, _frames(self.n, self.frame_start),
                                   self.coordinate.astype(float), group="Outfit", interpolation="CONSTANT")

    # --- material values ------------------------------------------------------------------

    # Game shader property -> KKBP Material Template parameters it sets (KKBP's templates name their
    # parameters after the game's, see its library/descriptions.py).
    MATERIAL_PARAMS = {
        "_Color": ("color", "color_alpha"), "_Color2": ("color2", "color2_alpha"),
        "_Color3": ("color3", "color3_alpha"), "_ShadowColor": ("shadow_color", None),
        "_shadowcolor": ("shadow_color", None), "_overcolor1": ("overlay_color1", "overlay_color1_alpha"),
        "_overcolor2": ("overlay_color2", "overlay_color2_alpha"), "_SpecularColor": ("specular_color", None),
    }

    def apply_material_values(self):
        """Keys the material values the game changed during the clip (a blush coming up, colours a
        plugin animated) on the KKBP materials that have a matching parameter. Values that never
        change are left as KKBP set them from the bundle."""
        import sys
        from . import kkbp_link
        try:
            pkg = kkbp_link.api().__name__.rpartition(".")[0]
        except kkbp_link.KkbpMissing:
            return
        templates = sys.modules.get(pkg + ".templates")
        info = self.tracks.header.get("materials", {})
        if templates is None or not info:
            return
        by_game_name: dict[str, list[str]] = {}
        for ch_name, meta in info.items():
            by_game_name.setdefault(meta.get("material", ""), []).append(ch_name)
        game_name_of = {m.id: m.name for m in self.bundle.materials}
        frames = _frames(self.n, self.frame_start)
        keyed, unmatched = 0, set()
        for mat in {m for o in self.ch.parts() if o.type == "MESH" for m in o.data.materials if m}:
            gname = game_name_of.get(mat.get(ID))
            channels = by_game_name.get(gname, []) + by_game_name.get(f"{gname} (Instance)", [])
            if not channels:
                continue
            ch_name = channels[0]
            meta = info[ch_name]
            data = self.tracks.full(ch_name)
            changing = ~np.all(np.isclose(data, data[:1], equal_nan=True), axis=0)
            if not changing.any():
                continue
            try:
                params = set(templates.params(mat))
            except Exception:
                continue
            curves = None
            for i, prop in enumerate(meta.get("colors", [])):
                cols = slice(i * 4, i * 4 + 4)
                if not changing[cols].any():
                    continue
                rgb_param, alpha_param = self.MATERIAL_PARAMS.get(prop, (None, None))
                done_here = False
                for pname, comps in ((rgb_param, range(3)), (alpha_param, (3,))):
                    if pname is None or pname not in params:
                        continue
                    socket = templates.param(mat, pname)
                    path = socket.path_from_id() + ".default_value"
                    owner = socket.id_data
                    curves = anim.Curves(owner, f"{owner.name} values (KK2Blender)")
                    for c in comps:
                        index = c if len(comps) > 1 else -1
                        values = data[:, i * 4 + c]
                        if index < 0:
                            curves.curve(path, 0, frames, values, group="Game values")
                        else:
                            curves.curve(path, index, frames, values, group="Game values")
                    keyed += 1
                    done_here = True
                if not done_here:
                    unmatched.add(f"{gname}.{prop}")
        if keyed:
            self.log(f"keyed {keyed} material values")
        if unmatched:
            self.log(f"material values that changed but have no KKBP parameter: {', '.join(sorted(unmatched)[:10])}")

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
    bake.apply_outfit_schedule()
    try:
        bake.apply_material_values()
    except Exception as e:
        log(f"material values not applied: {e}")
    try:
        bake.apply_eye_textures()
    except Exception as e:  # eye looks are a nicety; never fail the import over them
        log(f"eye look not applied: {e}")
