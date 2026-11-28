# Export folder format (version 1)

What **Export All** writes and **Import All** reads.

```
kk2b_<yyyyMMdd_HHmmss>/
  scene.json        what is in the scene; written last, with "complete": true
  cards/c00.png     each character's card, saved from Studio
  tracks/c00.k2t    per-frame data of character c00
  tracks/scene.k2t  per-frame data of everything else
  tracks/time.k2t   the Timeline time of each frame
  meshes/           KKBP bundles of the characters
  props/item_N.glb  each Studio item's meshes and materials, in the item's space
  map/part_NNN.glb  the map's meshes with baked lightmaps (UV1, Unity RGBM)
```

## Space

Everything is in Unity's space: left-handed, +Y up, game units. A matrix is the top three rows of
a 4x4 transform, row-major (12 floats). The add-on converts with one rule (`space.py`): a Unity
vector (x, y, z) is Blender (-x, -z, y), and a matrix M becomes C M Cᵀ.

## Track files (.k2t)

```
"K2T1"                     4 bytes
header length              u32 little-endian, a multiple of 4
header                     UTF-8 JSON
data                       float32 little-endian, one block per channel
```

The header holds `frame_count`, `channels`, and the subject's own fields. A channel is

| Field | Meaning |
| --- | --- |
| `name` | what it records, see below |
| `kind` | the record layout (`matrix`, `bool`, `weights`, …) |
| `width` | floats per record |
| `start` | first frame with a record |
| `count` | records from `start` |
| `constant` | every record is the same; only one is stored |
| `offset` | byte offset of its block in the data |

Frames outside `start .. start + count - 1` have no record, and frames inside where the object didn't
exist (an outfit rebuilt) hold NaN.

### Character tracks (`tracks/cNN.k2t`)

| Channel | Width | Record |
| --- | --- | --- |
| `root` | 12 | character root, world matrix |
| `visible` | 1 | the character shows |
| `bone:<path>` | 12 | every transform below the root, root-relative matrix (with scale) |
| `vis:<path>` | 1 | a renderer draws (1) or not (0) |
| `shape:<path>` | n | blend shape weights of a skinned renderer, 0..100 |
| `face:eyes`, `face:mouth`, `face:eyebrow` | 2 × patterns | per pattern: open weight, closed weight (0..1) |
| `eyetex:<i>` | 4 × textures | eye-look texture offset x, y and scale x, y per texture |
| `status` | 12 | see `status_layout`: coordinate, blush, tears level, skin gloss, nipple, eyes open max, mouth open max, eye set, mouth pattern, eyebrow pattern, eye look, neck look |
| `clothes` | 8 | clothing state per slot (0 on, 1 and 2 partly off, 3 off) |
| `acc_show` | 20 | accessory shown per slot |
| `mat:<path>:<slot>` | varies | the material values the game changes: colours (4), floats (1), vectors (4), textures (name index, offset xy, scale xy), in the order the header's `materials` entry lists |
| `cloth:<path>` | 3 × vertices | Unity Cloth vertex positions, local to the cloth renderer |

`<path>` is the transform's path below the character root, names joined by `/`; a second sibling
with the same path gets `#2`. Header fields: `renderers` (per path: renderer name, owner such as
`clothes:0` or `accessory:3`, mesh name, blend shape names, material names), `face` (pattern counts),
`eye_textures` (which renderer, material and texture names), `eyesets` (id, name, eye pattern, gag).

### Scene tracks (`tracks/scene.k2t`)

| Channel | Width | Record |
| --- | --- | --- |
| `camera` | 17 | world matrix, vertical FOV (degrees), near, far, orthographic, orthographic size |
| `camera_target` | 4 | the point Studio's camera orbits (x, y, z) and the distance to it (depth of field focus) |
| `studiocam:<key>` | 14 | a camera object placed in Studio: world matrix, shown, active |
| `item:<key>` | 14 (13 before 0.1.6) | Studio item world matrix, shown, item Alpha (Studio slider or Timeline; fades only the item's alpha renderers, whose glb materials carry `extras.kk2b_item_alpha`) |
| `item:<key>:bone:<path>` | 12 | bones of animated or skinned items, item-relative |
| `folder:<key>` | 13 | Studio folder world matrix, shown |
| `light:<n>` | 20 | world matrix, RGB, intensity, range, spot angle, enabled, shadow strength |
| `map` | 13 | map root world matrix, shown |

Header `lights` says each light's name, type, owner (`studio_light`, `camera` for Studio's
character light, `map`, `other`) and Studio key.

## scene.json

| Field | Meaning |
| --- | --- |
| `format`, `version` | `kk2blender-scene`, 1 |
| `complete` | true once every file is written |
| `screen` | Studio's window size, for the render aspect |
| `characters` | id, Studio key, name, sex, parent, card, tracks, coordinate count, coordinate at export |
| `items` | key, name, parent, track, group/category/no, colours, alpha, game asset (bundle, file) |
| `folders`, `studio_lights` | Studio's own settings for those |
| `map` | number, name, asset, option, sun (time of day), position, rotation, `parts` (each `map/part_NNN.glb` with its matrix relative to the map root) |
| `studio_cameras` | key, name, parent, track of each camera object |
| `saved_views` | Studio's saved camera views: index, matrix, fov, target distance |
| `effects` | Studio's scene effects: AO, bloom, DOF, fog, vignette, sun shafts, ramp, line width, character light |
| `bake` | fps, frame_count, duration, source (`timeline` or `fixed_length`), settle time, track files |
| `kkbp` | where the KKBP request and result are, and its state |
