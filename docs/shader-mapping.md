# Shader mapping and card compatibility

KKBP's KK Template Set draws a material from three things the exporter decides: its **Material Role**
(face, body, hair...), its **Blend Mode** (opaque, cutout, alpha, glasses) and its **light/dark bake**
(the main texture rendered lit from the front and from behind). KKBP models KK's own shaders and
xukmi's; every other shader (Material Editor swaps such as KKUTS, mod items with Unity Standard) still
renders plausibly when those three are right. This page lists how each shader family is treated
(`kk_plugin/src/Format/Bundle/ShaderFamilies.cs`, unit tested in `kk_plugin/tests/CompatTests.cs`).

## Material values: every property of the shader

Unity 5.6 can't list a shader's properties, so the exporter reads them from tables:

- its own, `kk_plugin/data/shader-properties.json`, built by `tools/build_shader_table.py` from the card
  audit's dump of the mod shaders (KKUTS family, KKUSS, IBL, Goo, BetterMatcap, Fur, Rinfu stocking...),
  Unity's Standard/legacy/particle shaders, UTS2 and Toony Colors Pro 2, plus every Material Editor
  property name seen on the audited cards (tried on every shader);
- KKBP's `shader-properties.json`, read from the KKBP exporter's assembly when it is loaded in the game;
- KK's character shader properties (the 0.1.4 fixed list), always tried.

A shader the tables list is read with its own properties; any other shader with the union of all of
them. The material is asked for each (`HasProperty`), so extra names cost nothing. Colours, floats,
vectors and textures go into `materials.json` in KKBP's format (`colors`, `floats`, `vectors`,
`textures`); a non-finite value is left out (KKBP's reader rejects it). Per-frame material keying
(`MaterialSampler`, the bake's `mat:` channels) keeps its fixed list of values the game animates.

## Families

| Family | Shaders | Modelled by KKBP | Bake | Blend Mode | Main texture / colour / shade | Normal map | Role |
| --- | --- | --- | --- | --- | --- | --- | --- |
| kk | KK's Shader Forge and Koikano shaders, and the low-detail `main_hair_low`, `main_item_low`, `main_opaque_low2`, `main_opaque_culloff` | yes | game | name/queue (0.1.4 rules) | KK's | `_NormalMap` | hair shaders: `hair` |
| xukmi | `xukmi/*` | yes | game | name/queue | KK's | `_NormalMap` | `*Hair*`: `hair` |
| kkuts | `KKUTS*` (KKUTS, alpha, item, hair, eye, eyew, `_Tess`) | no | game; `_Outline_Width`, `_Farthest_Distance` zeroed; matcap, angel ring and normal maps cleared | name/queue (`KKUTSalpha*` alpha) | `_MainTex`/`_BaseMap`, `_BaseColor`/`_Color`, `_1st_ShadeColor` | `_NormalMap` | `KKUTShair*`: `hair` |
| uts2 | `UnityChanToonShader/*` | no | as kkuts | `*TransClipping`/`*Clipping`: cutout | as kkuts | `_NormalMap` | hair names: `hair` |
| kkuss | `KKUSS*` | no | game; `_outline` zeroed, normal maps cleared | name/queue | `_MainTex`, `_Color` | `_NormalMap` | `KKUSShair`: `hair` |
| ibl | `IBL_Shader*` | no | game; metal, gloss and `_Outline` zeroed, metal/normal maps cleared | `*alpha*`: alpha, `*cutoff*`: cutout | `_MainTex`, `_Color` | `_NormalMap` | — |
| standard | `Standard`, `Standard (Specular setup)`, `Az/Standard*`, `Legacy Shaders/*`, `AIT/*` | no | game; metal and gloss zeroed, metal/normal maps cleared | `_Mode`: 0 opaque, 1 cutout, 2-3 alpha; else queue | `_MainTex`, `_Color` | `_BumpMap` | — |
| tcp2 | `Toony Colors Pro 2/*`, `arktoon/*` | no | game; metal, gloss and outline zeroed | name/queue | `_MainTex`, `_Color`, `_SColor` | `_BumpMap` | — |
| stocking | `Rinfu/StockingShader` | no | game; `_OutlineWidth` zeroed | queue (alpha) | `_Maintex`, `_StockingColorTint`/`_Color` | `_Normalmap` | — |
| matcap | `BetterMatcap`, `MatCap*` | no | flat (the matcap's average colour without a main texture) | name/queue | `_MainTex`, `_Color` | — | — |
| goo | `Goo` | no | flat | alpha | `_Albedo`/`_MainTex`, `_Color` | — | — |
| fur | `Fur/*`, `Az/Fur` | no | flat | queue (alpha) | `_MainTex`/`_FurTex`, `_Color` | — | — |
| scroll | `DA/Chara/TextureScroll`, `DualScroll` | no | flat | name/queue | `_MainTex`, `_Color` | — | — |
| kk_texture | `Shader Forge/main_texture_studio` | no | flat | name/queue | `_MainTex`, `_Color` | — | — |
| effect | `Particles/*`, `Xffect/*` (but heat distortion), `DA/*`, `HOTATE/*`, `Unlit/Glitch*`, `FXHologram`, `Space`, `Custom/AlphaDependingDistance`, `KriptoFX/*` | no | flat | alpha | `_MainTex`, `_TintColor`/`_Color` | — | — |
| screen_effect | `Xffect/heat_distortion_t2`, `*distortion*`, `*Blur*`, `LTZ/RGB_Disturbance` | no | invisible (fully transparent) | alpha | — | — | — |
| unlit | other `Unlit/*` | no | flat | name/queue | `_MainTex`, `_Color` | — | — |
| unknown | anything else | no | game, with the empty-bake check | name/queue | `_MainTex`, `_Color` | `_NormalMap`/`_BumpMap` | hair names: `hair` |

- **Game bake**: the game's own shader draws the material on a quad in UV space (0.1.4). Metal and gloss
  are zeroed on the bake's copy because a lone directional light with no reflections draws metal black.
- **Flat bake**: the main texture, placed by the material's tiling and offset, times the main colour
  (light); that times the shade colour, or 0.7 grey without one (dark). For shaders a UV-space render
  can't draw: shells (fur), screen effects, view-dependent matcaps, scrolling textures.
- **Empty-bake check**: a game bake (other than body and face) that shows under a tenth of what the flat
  bake of the same material shows, with the flat bake showing over 5%, is replaced by the flat bake and
  reported (`bake_fallback`). A black or transparent texture gives nothing to compare, so it's kept.
- **Texture Roles**: KK's names first (`_MainTex` main, `_NormalMap` normal, `_overtex*` overlay...); for
  a family KKBP doesn't model, the family's main texture and normal map take `main` and `normal` when
  no earlier property took them. Every other texture keeps its raw name with no role.

## Outlines

KKUTS, KKUSS, Rinfu's stocking shader, IBL and TCP2 draw an outline pass in the game. In Blender KKBP's
Outlines hook gives every outfit part its own outline (a Solidify shell), so the game's outline must not
reach the bake: the bake turns the `OUTLINE` pass off, its quad faces the camera (outline passes cull
front faces), and the family's outline width is zeroed on the bake's copy, so a game outline can't be
drawn into the texture as well. One outline, KKBP's, results.

## Other compatibility rules

- **Hair accessories**: an accessory carrying a `ChaCustomHairComponent` (ao_hair items, hair mods on
  accessory slots), or drawn with a hair shader (`KKUTShair`, `KKUSShair`, `main_hair_low`...), gets the
  `hair` Material Role, and its bones the `hair` group.
- **Implanted bones** (ModBoneImplantor): a mod item's `BoneImplantProcess.trfSrc` and its children
  live under a body bone while the item is worn. They stay the item's: keyed and recorded with its
  Outfit, parented to the body bone, resting at the clothes' bind pose. Any other skin bone the skeleton
  walk didn't record is added with its ancestors (`late_bone`), so no weight is dropped; weight that
  still can't be kept (a null bone) is reported (`dropped_weights`).
- **Runtime mesh plugins**: the capture reads the renderer's live `sharedMesh` (ObjImport's runtime
  meshes, BlendShapeCreator's added shapes), the live materials (ChaAlphaMask's body alpha mask), and
  textures plugins set through a renderer's property block. Blend shape weights a plugin set
  (ClothingBlendShape) are sampled every frame by the bake and applied per renderer in Blender
  (`blender_addon/kk2blender/shapes.py`), and the weights set at export are noted on the mesh's entry in the bundle's
  `kk2blender.json` (`blend_shape_weights`).

## The export self-check

Every export writes `diagnostics.json` and `diagnostics.txt` into the export folder and the summary to
the BepInEx log: per character, the shaders KKBP doesn't model and how each was mapped (family, bake,
Blend Mode, material count), and counts with an example of: renderers left out, dropped skin weights,
added (late) bones, implanted bones, bind pose and bone count mismatches, failed and replaced bakes,
unreadable textures, mapped materials without a main texture, head/body materials without a role,
Material Editor copies, property-block textures, and plugin-set blend shape weights.
