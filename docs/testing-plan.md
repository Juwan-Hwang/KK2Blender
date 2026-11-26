# First test in the game (M1)

Goal: one character, one short Timeline clip, through the whole pipeline on the Windows PC.

## Setup (about 5 minutes)

1. Copy `KK2Blender.Studio.dll` to `E:\Koikatu\BepInEx\plugins\KK2Blender\`.
2. Copy `KKBP.Exporter.KK.dll` (KKBP release 2026.11.25, `kkbp-exporters-2026.11.25.zip`) to
   `E:\Koikatu\BepInEx\plugins\KKBP\`.
3. Blender 5.2: install the `kkbp-2026.11.25.zip` extension, press its Install Pillow button, then
   install the KK2Blender extension zip.

## Steps and what to check

| # | Step | Check |
| --- | --- | --- |
| 1 | Start CharaStudio | BepInEx log has `KK2Blender` loaded, no errors; toolbar button shows |
| 2 | Load a scene with one character and a 5–10 s Timeline | The window shows the Timeline length |
| 3 | Export All at 30 fps | Progress counts frames; ends with "Done"; folder has scene.json, cards/c00.png, tracks/*.k2t; `BepInEx/config/kkbp-autoexport.json` exists |
| 4 | Press "Export meshes now" (starts Koikatu.exe) | The maker opens, exports, the game quits; `meshes/` has one bundle; `kkbp-autoexport.result.json` says succeeded |
| 5 | Blender: Import All on scene.json | Report lists bones, expression keys, visibility; no errors |
| 6 | Scrub the timeline | Body, hair and skirt move as in Studio; face changes; camera matches Studio's view |
| 7 | Render a few frames (Preview) | Lit, no black materials |
| 8 | Side by side with a VideoExport capture of the same frames | Pose and framing match |

## If something is off

- Send `BepInEx/LogOutput.log` and the Blender report (KK2Blender tab).
- Bones wrong but face right: likely bone matching; the report names unmatched bones.
- Everything mirrored or rotated: the space conversion; compare one bone's matrix.
- Character missing: check step 4 (KKBP bundle) and the report's notes.
