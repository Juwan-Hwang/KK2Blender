# KK2Blender

One-click pipeline from **Koikatsu CharaStudio** to **Blender**: export a whole Studio scene
(characters, outfits, props/maps, Timeline animation with physics and expressions, camera, lights)
with one button in KK, import it with one button in Blender, get automatic lighting and render
settings, and batch-render headless on a Linux GPU box.

Status: design phase. See [docs/design.md](docs/design.md).

## Layout

| Path | What |
| --- | --- |
| `kk_plugin/` | BepInEx plugin for KK Studio: "Export All" (C#, net35) |
| `blender_addon/` | Blender add-on: "Import All", lighting presets, render settings (Python) |
| `render/` | Headless batch render scripts for `blender -b` on Linux |
| `docs/` | Design doc, format spec, test logs |

## Credits

Learned from (logic only, no code copied):
- [KKBridge](https://github.com/rintrint/KKBridge) by rintrint (MIT)
- [KKBP](https://github.com/kkbp-dev/kkbp) and [kkbp-maps](https://github.com/kkbp-dev/kkbp-maps) by the KKBP developers (MIT)

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
