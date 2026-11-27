# Character bundles

Export All writes one [KKBP](https://github.com/kkbp-dev/kkbp) Export Bundle (contract schema 1.3, KKBP
2026.11.25) per Studio character into `meshes/<yyyyMMddHHmmss>_<name>/`, inside CharaStudio. The main game
and KKBP's maker exporter aren't needed. The code is ours: `kk_plugin/src/Character/` reads the game,
`kk_plugin/src/Format/Bundle/` writes the bundle from plain arrays (unit tested off the game).

## What is in a bundle

| Pass | Contents |
| --- | --- |
| Body (`body.glb`) | body, face, eyes, eye whites, eyelines, eyebrows, nose line, teeth, head tongue, tears, gag eyes |
| Outfit NN (`outfits/NN/outfit.glb`), one per coordinate | clothes and their half-off meshes (`alternate`), hair, accessories (also MoreAccessories' extra slots), mod clothes; the body alpha mask of that coordinate as `body_textures` |

Every renderer is captured whether it shows or not (clothes taken off, hidden accessories), so the bake's
visibility channels decide what shows. Left out: mosaic and male-part stand-ins, silhouette bodies, the
condom, the body's rigged tongue (`n_tang/o_tang`, see below), hit meshes and hit transforms, and meshes the
game doesn't let scripts read (none in KK as far as known; each is a warning in the log).

Materials carry their shader values, every texture slot they hold (normal maps unpacked from Unity's
DXT5nm), and the light and dark main texture bakes KKBP's templates require, rendered with the game's own
shader on a UV-space quad (and the flattened mesh for overlays on body and face) by our own camera and light.
Tears and gag eyes get no bake (their templates don't use one).

## Capture pose: the bind pose

Bones rest at the meshes' **bind pose**, not at the Studio pose and not at a T-pose animation. For each mesh
the bones are placed at `C_j = K · Bind_j⁻¹`, so every skin matrix of that mesh is the same `K` and the mesh
rests at `K · v` with no blending error. Then any later bone matrices `M_j` move the rest mesh exactly as the
game moves its own: `M_j · C_j⁻¹ · v_rest = M_j · Bind_j · v`. Nothing in the scene is posed for the capture.

- The body mesh places the shared skeleton; later meshes take `K` from their most weighted already-placed
  bone. A mesh whose own bind pose disagrees with bones already placed (rare: a mod bound in another pose)
  can't be exact; it is skinned with blended matrices and the log names it with its largest mismatch.
- Bones no mesh weights follow their parent with their current local transform.
- `skeleton.json` holds each rest without scale (KKBP ADR 0008); a scale in the bind pose (rare) is
  `game_scale`. Body sliders and ABMX are not in the rest at all: they are in the bake's bone matrices.
  So the imported character at rest has the base body's proportions until the bake is applied.
- Translations are relative to the character root (`ChaControl.transform`), like the bake's `bone:` channels.

The Blender side poses a bone with `P = B(M_t · S⁻¹) · B(R_rest)⁻¹ · L` (`pose.py`), which is exact for this.

## Matching bundle data to the bake

- Each mesh record in `body.json` / `outfit.json` has `extensions.kk.path`: the renderer's transform path
  below the character root, named by the same `PathTable` the bake uses (`#2` for duplicates). Its
  `name` is the renderer's GameObject name. Meshes with a Unity `Cloth` also have `extensions.kk.cloth = true`
  and `extensions.kk.source_vertices` (glTF vertex i → Unity mesh vertex; we never weld or split, so it is
  the identity). These mesh-level extensions are outside KKBP's strict schema; KKBP's reader ignores them.
- `kk2blender.json` in the bundle maps Ids to the game: `bones` (Id → path, outfit), `meshes` (Id → path,
  pass, renderer name, owner, vertex count, `ordinal`, `blend_shapes` in index order), `materials`
  (Id → renderer path, slot), plus capture notes and warnings.
- Every glTF vertex carries two custom attributes, `_KK_MESH` (the mesh's `ordinal`) and `_KK_VERTEX` (its
  Unity vertex index). Blender's glTF importer keeps them as float point attributes, and they survive KKBP's
  joins (Body is one object made of all Body Pass meshes) and welds, so any Blender vertex can be traced
  back to its game mesh and vertex.

## Expressions

- `raw:<blend shape name>`: every blend shape of every mesh, in the game mesh's blend shape index order,
  at weight 100 (the frame at 100, or the last frame scaled to 100). In `expressions.json`: category `other`,
  name null, `game_name` the blend shape name. The bake's `shape:<path>` weights (0..100 per index) drive them.
- Finished keys like KKBP's: per eye set (eye list position → `contract/expressions/kk.json` name, else
  `Eye set <id> <name>`), eyebrow and mouth pattern (open, and closed where the game blends), the table's
  recipes, and the showing keys `Tears small|med|big` and `Gag eye 00..02`. Gag eye sets record what they show
  (the gag mesh), hide (pupils, eye base as the eye set says) and the gag material's texture pattern and
  animation values; heart and star pupils record the eye material's `_expression` pattern.
- Tear and gag meshes rest inside the head; their showing key moves them out (KKBP ADR 0010). Their raw keys
  are offsets and add to either. The Blender side should set the showing key while the game shows the mesh.

## Known limits

- In-between blend shape frames can't be exact with one shape key (KK's face uses single frames).
- The rigged tongue is left out: KKBP would give the head tongue its weights, which follows the game only
  when the rigged tongue shows (mouth pattern 21).
- `_alpha_a` / `_alpha_b` (which half of the body alpha mask applies) change with clothing states; the
  bundle holds the values at export. The bake records them per frame in the material channels.
- No vertex welding or mirroring (KKBP ADRs 0047, 0048): copies more than 1 µm apart stay apart.
