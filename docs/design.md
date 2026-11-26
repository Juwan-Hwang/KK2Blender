# KK2Blender design (v0.1, 2026-11-27)

## 1. Goal

Press **Export All** in KK CharaStudio, press **Import All** in Blender, render. The Blender
result should match what was set up in Studio, with no manual lighting work:

- characters, including mod clothes and accessories
- outfit / clothing-state changes during the animation
- props and maps
- Timeline animation, including hair/skirt physics (DynamicBone) and facial expressions
- camera and Studio lights

Out of scope for v1: editing in Blender beyond lighting/render presets, VR, H-scene game mode
(non-Studio), Unity `Cloth` component simulation (see risks).

## 2. Machines

| Machine | Role |
| --- | --- |
| Windows PC (KK + HF Patch, weak GPU) | Runs KK Studio, runs the export, hosts the shared folder |
| Linux PC (strong GPU, small disk) | Mounts the share, runs `blender -b` batch renders |
| Sandbox (Hark) | Writes code, builds, reaches both PCs over Tailscale |

The export folder lives on the Windows share; Linux reads it over SMB and writes frames back
(or to a local scratch dir, then moves finished videos back).

## 3. Architecture

```
KK Studio                         Share: exports/<scene>/            Blender (Linux, headless)
-----------                       ------------------------           -------------------------
[Export All] ──► characters/<id>/  (KKBP Exporter output: pmx,       [Import All]
                                     textures, json, all outfits)     ├─ KKBP import per character
             ──► anim/<id>.k2b     (baked per-frame bones,            ├─ apply baked bones/shapes/visibility
                                     blendshapes, visibility)         ├─ props/maps (glTF)
             ──► props/*.glb       (static meshes + textures)         ├─ camera + lights
             ──► scene.json        (layout, camera, lights,           ├─ lighting preset + render settings
                                     timeline length, fps)            └─ save scene.blend
                                                                     blender -b scene.blend -P render/render.py
```

### 3.1 KK plugin: Export All (C#, BepInEx, net35)

1. **Characters**: for each `OCIChar`, drive the user-installed KKBP Exporter through
   reflection (all outfits, no pose freeze, T-pose) into `characters/<id>/`. We do not copy its
   code (no license).
2. **Bake pass**: play the Timeline from 0 with `Time.captureFramerate = fps` and sample at
   `WaitForEndOfFrame`, exactly as KKBridge does, so DynamicBone physics runs per frame and is
   deterministic. Each frame we record:
   - local TRS of **every** transform under each character root (body, face, hair, skirt,
     accessory and physics bones), by transform path. No VMD/Tda mapping, so nothing is lost.
   - blend shape weights of every `SkinnedMeshRenderer` on the character (face, eyes, mouth,
     tears, blush), plus eye/eyebrow/mouth pattern indices and eye texture offsets for
     expressions that swap textures.
   - outfit state: current coordinate index, `fileStatus.clothesState[]` per slot, accessory
     show flags, and `activeSelf` / `renderer.enabled` of each clothing and accessory object.
   - each Studio object's world TRS and visibility (props, items, folders, lights).
   - camera world TRS + FOV, and each light's type, color, intensity, range, spot angle.
3. **Props/maps**: walk the scene's static `MeshFilter`/`MeshRenderer` objects once, write
   glTF (`.glb`) with base color/normal/emission textures. Our own writer; Unity 5.6 constraints.
4. **scene.json**: version, fps, frame count, coordinate conventions, object list, file index.

Bake file format `.k2b`: small header + float32 arrays (frame-major), one channel table in
JSON. Roughly 1,000 transforms x 10 floats x 300 frames is ~12 MB per character per 10 s,
compressible with gzip.

### 3.2 Blender add-on: Import All (Python, Blender 4.2 LTS first)

1. Read `scene.json`, set fps, frame range, unit scale, Unity (left-handed, Y-up) to Blender
   (right-handed, Z-up) conversion in one place.
2. Call the KKBP Importer operator per character (keep the KK armature, not Rigify, so bone
   names match the bake). Append characters into one scene (KKBP imports one per file).
3. Apply `.k2b`: write F-curves per bone (local rotation/location/scale relative to rest),
   shape-key F-curves, and visibility keys (`hide_render`/`hide_viewport`) for outfit changes.
4. Import props/maps glTF and key their transforms and visibility.
5. Create the camera (sensor/FOV match) and lights from Studio data.
6. **Auto lighting**: presets (e.g. "Studio match", "Soft anime 3-point", "Cinematic rim"),
   each a world HDRI/color + key/fill/rim lights placed relative to the camera and character
   bounds, plus exposure; default chosen so no manual tweak is needed.
7. **Render presets**: Eevee (fast, toon-accurate with KKBP shader) and Cycles
   (OptiX, denoise, adaptive sampling). Resolution, fps, output path, PNG sequence + ffmpeg mux.

### 3.3 Headless batch render (Linux)

`render/render.py` + `render/queue.sh`: `blender -b scene.blend -P render.py -- --preset ...`,
renders a frame range to local scratch, resumes from the last finished frame, muxes with
ffmpeg, copies the video back to the share and cleans scratch (small disk).

## 4. What we reuse

| Source | License | Reuse |
| --- | --- | --- |
| KKBridge | MIT | Capture loop (captureFramerate + Timeline play + end detection), Timeline reflection shim, camera target/FOV reflection, expression pattern lookup ideas. Not its VMD/Tda bone mapping. |
| KKBP Importer 9.6.0 | MIT (some files may differ) | Character import, KK shaders, material setup, shape keys. Called as a dependency; small pieces copied only with notices. |
| KKBP Exporter | No license file | Runtime dependency the user installs. No code copied. |
| Koikatsu Unity Porter | Binary only | Nothing. |

## 5. Biggest risks

1. **Name matching between game and KKBP output.** KKBP may rename bones, shape keys and
   duplicate accessory bones. Mitigation: bake by transform path, write a mapping table at
   import, test on M1 first.
2. **Expressions** use pattern blending and texture swaps, not only blend shapes. Some
   (gag eyes, highlights, tears) may need material keys.
3. **Outfit changes and clothing states** (half-off) switch meshes. KKBP's "all outfits"
   export must contain every mesh we need to toggle.
4. **Props/maps export**: no reusable code; custom shaders and many materials. Expect
   simplified materials first.
5. **Unity `Cloth`** in some mod clothes is not bone-driven. Would need per-vertex cache
   (Alembic/MDD). Deferred.
6. **KKBP versions**: Importer targets Blender 4.2 LTS and needs mmd_tools; exporter is
   decompiled and may change. Pin versions.
7. **"No manual lighting"** is a taste problem. Presets plus a contact-sheet preview pass.
8. **Disk and network**: Linux disk is small; render to scratch, move out per chunk.

## 6. Milestones

- **M0 Setup**: SSH to the Windows PC / WSL, read the KK install (game DLLs for building),
  build toolchain (dotnet SDK, net35 target, references from the user's install), Blender
  4.2 + mmd_tools + KKBP on Linux, SMB share mounted.
- **M1 10-second single-character test**: one character, one outfit, 10 s Timeline with body
  motion, hair/skirt physics, a blink and one expression change, a moving camera. Export ->
  import -> Eevee 1080p30 headless render. Pass: side-by-side with a Studio capture, pose and
  physics visibly matching, no manual steps.
- **M2 Lights + auto lighting**: Studio lights mapped, 3 presets, Cycles preset.
- **M3 Outfits**: coordinate switches, clothing states, accessory toggles mid-animation.
- **M4 Props and maps**: glTF export, transforms and visibility keyed.
- **M5 Multi-character and polish**: several characters, one-click everything, render queue,
  install docs.
