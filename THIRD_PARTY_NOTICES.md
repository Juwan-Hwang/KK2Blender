# Credits

All code in this repository is written for this project. No third-party code is copied or vendored.
These projects were studied for their logic and approach:

- KKBridge (rintrint): Timeline capture loop, camera access
- kkbp-dev/kkbp and the legacy KKBP importer/exporter: character export bundle, shaders, joint corrections
- kkbp-dev/kkbp-maps: map and prop libraries, game lighting
- rogerghi/kk-studio-to-blender-importer: Studio scene-card parsing, prop resolving
- VideoExport (Joan6694): fixed-frame-rate capture and per-frame physics updates
- Timeline (Joan6694): its public static API, called by reflection

KKBP (MIT, Copyright (c) 2026 KKBP developers) is used as an installed dependency: its exporter runs
in the game and its importer in Blender. Its test bundle builder is used by this project's tests
from a KKBP source checkout; it is not copied here.
