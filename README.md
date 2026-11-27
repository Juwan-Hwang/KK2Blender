# KK2Blender

One-click pipeline from **Koikatsu CharaStudio** to **Blender**: press **Export All** in Studio,
press **Import All** in Blender, render. The Blender scene matches the Studio scene: characters with
their outfits, Timeline animation with hair and skirt physics and facial expressions, outfit changes
during the animation, props, camera and lights, with automatic lighting and render settings.

Status: **v0.1, first version, not yet tested in the game.** The Blender side is tested end to end
against the real KKBP importer (see [Testing](#testing)); the Studio plugin builds against the game's
assemblies and waits for its first run in Studio.

## How it works

```
CharaStudio                      export folder                     Blender 5.2+
[Export All] ──► cards/c00.png   (each character's card)          [Import All]
             ──► tracks/*.k2t    (every frame: all bones,          ├─ KKBP imports each character
                                  faces, outfits, props,           ├─ bones, expressions, outfit
                                  lights, camera)                  │  visibility, eye looks keyed
             ──► scene.json      (what is in the scene)            ├─ camera, lights, props, map
             ──► meshes/         (KKBP bundle per character,       ├─ lighting preset
                                  all outfits)                     └─ render preset
```

The plugin doesn't re-implement the game: it plays the scene with a fixed frame rate and records the
game's own result every frame (the same idea as VideoExport, without rendering images). Physics,
IK, KKPE, ABMX, joint corrections and expressions are all captured as the game computed them.
Character meshes are written in Studio as [KKBP](https://github.com/kkbp-dev/kkbp) Export Bundles by our
own exporter, and KKBP imports them with its materials and shaders ([docs/character-bundles.md](docs/character-bundles.md)).

Details: [docs/design.md](docs/design.md), file format: [docs/bake-format.md](docs/bake-format.md).

## Install

Game side (Windows, KK with HF Patch):

1. `KK2Blender.Studio.dll` into `Koikatu\BepInEx\plugins\KK2Blender\`.
2. Optional: KKBP's exporter `KKBP.Exporter.KK.dll` (from the `kkbp-exporters-*.zip` of a
   [KKBP release](https://github.com/kkbp-dev/kkbp/releases)) into `Koikatu\BepInEx\plugins\KKBP\`. Only
   used as a fallback when no character could be exported in Studio.

Blender side (5.2 or later, Windows or Linux):

1. Install the KKBP extension zip (`kkbp-*.zip`) and let it install Pillow (its panel has a button).
2. Install the KK2Blender extension zip.
3. Optional: build a map library with [kkbp-maps](https://github.com/kkbp-dev/kkbp-maps) and add it
   as an asset library; Import All then links the scene's map.

## Use

1. In CharaStudio, open the scene, click the KK2Blender toolbar button (or Ctrl+Shift+E) and press
   **Export All**. It records the Timeline (or a set length without one) at the chosen frame rate.
   The character meshes (every outfit) are written at the end, in Studio.
2. In Blender: File > Import > KK2Blender scene, pick the export's `scene.json`.
3. Render, or headless:

   ```
   blender -b --python render/render.py -- --import <export folder> --preset final --lighting soft
   ```

   Frames that are already rendered are skipped, so a stopped render resumes. With ffmpeg on PATH the
   frames are joined into an mp4.

Lighting presets: **Soft anime** (default), **Studio match** (Studio's own lights), **Cinematic**,
**Daylight**. Render presets: Preview, Final (EEVEE 1080p), Final 4K, Cycles.

## Known gaps

- Nothing here has run inside the game yet; the first in-game test is [docs/testing-plan.md](docs/testing-plan.md).
- Props and maps use simplified materials (a KK-like toon shader for props; albedo × baked lightmap for maps).
- Material values the game animates (blush, gloss) are keyed where KKBP's material has a matching
  parameter; the import report lists the ones it couldn't place.
- The light/dark texture bakes come from the game's shaders under a plain light, so they may differ a little from KKBP's.

## Testing

| What | How |
| --- | --- |
| Track format (C#) | `dotnet test kk_plugin/tests` |
| Add-on logic | `pytest tests/python` (includes reading a file written by the C# code) |
| Add-on in Blender | `blender -b --factory-startup --python-exit-code 1 --python tests/blender/run_tests.py` |
| End to end with KKBP | `KKBP_SRC=<kkbp source> blender -b --python-exit-code 1 --python tests/blender/run_kkbp_integration.py` |

The end-to-end test builds a KKBP test bundle with KKBP's own test tool, writes an export around it
as the plugin would, runs Import All with the real KKBP importer and checks that every bone's
skinning matches the game's to within 1e-4, that expressions are keyed and that a piece of clothing
comes off on the right frame. CI definition: [ci/](ci/).

## Layout

| Path | What |
| --- | --- |
| `kk_plugin/` | BepInEx plugin for CharaStudio: Export All (C#, net35) |
| `blender_addon/kk2blender/` | Blender extension: Import All, lighting and render presets |
| `render/` | Headless render script |
| `tests/` | Python and Blender tests |
| `docs/` | Design, file format, test plan |

## Credits

Built on [KKBP](https://github.com/kkbp-dev/kkbp) (used as a dependency, MIT). Learned from
[KKBridge](https://github.com/rintrint/KKBridge), [kkbp-maps](https://github.com/kkbp-dev/kkbp-maps)
and VideoExport. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

License: MIT.
