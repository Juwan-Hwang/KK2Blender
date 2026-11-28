#!/usr/bin/env python3
"""Builds kk_plugin/data/shader-properties.json, the exporter's own table of shader properties.

KK runs Unity 5.6, which can't list a shader's properties at runtime, so the exporter needs a table
of property names and kinds per shader. This one is made from the card audit's dump of the mod
shaders (tools/card_audit.py: `fixdata/unknown-shaders.json` and `summary.json`'s Material Editor
property names) plus a few Unity shader families written out here. It holds shader and property
names only, nothing from the cards themselves.

Format: {"<shader name>": {"<property>": "c"|"f"|"t"|"v"}}; the entry "" lists properties to try on
every shader (the Material Editor property names seen on cards). At runtime the exporter also
reads KKBP's table when the KKBP exporter is loaded, and tries the union of every table on shaders
none of them lists.

Usage: build_shader_table.py <audit dir> [out.json]
"""

import json
import sys
from pathlib import Path

KIND = {"color": "c", "float": "f", "range": "f", "int": "f", "texture": "t", "vector": "v"}

# Unity's Standard shader (both setups) and the legacy shaders mod items ship with.
STANDARD = {
    "_Color": "c", "_MainTex": "t", "_Cutoff": "f", "_Glossiness": "f", "_GlossMapScale": "f",
    "_SmoothnessTextureChannel": "f", "_Metallic": "f", "_MetallicGlossMap": "t", "_SpecColor": "c",
    "_SpecGlossMap": "t", "_SpecularHighlights": "f", "_GlossyReflections": "f", "_BumpScale": "f",
    "_BumpMap": "t", "_Parallax": "f", "_ParallaxMap": "t", "_OcclusionStrength": "f", "_OcclusionMap": "t",
    "_EmissionColor": "c", "_EmissionMap": "t", "_DetailMask": "t", "_DetailAlbedoMap": "t",
    "_DetailNormalMapScale": "f", "_DetailNormalMap": "t", "_UVSec": "f", "_Mode": "f", "_SrcBlend": "f",
    "_DstBlend": "f", "_ZWrite": "f",
}
# Toony Colors Pro 2's common properties (its PBS and user shaders add more; the union covers them).
TCP2 = {
    "_Color": "c", "_MainTex": "t", "_HColor": "c", "_SColor": "c", "_RampThreshold": "f", "_RampSmooth": "f",
    "_Ramp": "t", "_BumpMap": "t", "_BumpScale": "f", "_SpecColor": "c", "_Glossiness": "f", "_Metallic": "f",
    "_MetallicGlossMap": "t", "_Cutoff": "f", "_Outline": "f", "_OutlineColor": "c", "_RimColor": "c",
    "_RimMin": "f", "_RimMax": "f", "_EmissionColor": "c", "_EmissionMap": "t", "_OcclusionMap": "t",
}
UNITY_SHADERS = {
    "Standard": STANDARD, "Standard (Specular setup)": STANDARD,
    "Legacy Shaders/Diffuse": {"_Color": "c", "_MainTex": "t"},
    "Legacy Shaders/Transparent/Diffuse": {"_Color": "c", "_MainTex": "t"},
    "Legacy Shaders/Transparent/Cutout/Diffuse": {"_Color": "c", "_MainTex": "t", "_Cutoff": "f"},
    "Legacy Shaders/Bumped Diffuse": {"_Color": "c", "_MainTex": "t", "_BumpMap": "t"},
    "Unlit/Texture": {"_MainTex": "t"}, "Unlit/Transparent": {"_MainTex": "t"},
    "Unlit/Transparent Cutout": {"_MainTex": "t", "_Cutoff": "f"}, "Unlit/Color": {"_Color": "c"},
    "Particles/Additive": {"_TintColor": "c", "_MainTex": "t", "_InvFade": "f"},
    "Particles/Additive (Soft)": {"_MainTex": "t", "_InvFade": "f"},
    "Particles/Alpha Blended": {"_TintColor": "c", "_MainTex": "t", "_InvFade": "f"},
    "Toony Colors Pro 2/Standard PBS": TCP2, "Toony Colors Pro 2/Standard PBS (Specular)": TCP2,
    "Toony Colors Pro 2/Standard PBS Cull Off": TCP2, "Toony Colors Pro 2/Standard PBS Cull Off (specular)": TCP2,
}
# UnityChanToonShader 2's built-in-pipeline shaders share KKUTS's properties (KKUTS is a port).
UTS_SHADERS = ["UnityChanToonShader/Toon_DoubleShadeWithFeather",
               "UnityChanToonShader/Toon_DoubleShadeWithFeather_TransClipping",
               "UnityChanToonShader/Toon_DoubleShadeWithFeather_Clipping",
               "UnityChanToonShader/Toon_ShadingGradeMap",
               "UnityChanToonShader/Toon_ShadingGradeMap_TransClipping"]


def build(audit: Path) -> dict:
    table: dict[str, dict[str, str]] = {}
    shaders = json.loads((audit / "fixdata" / "unknown-shaders.json").read_text(encoding="utf-8"))
    for name, entry in sorted(shaders.items()):
        props = (entry.get("shader") or {}).get("props") or []
        if props:
            table[name] = {p: KIND.get(k, "f") for p, k in props}
    for name, props in UNITY_SHADERS.items():
        table.setdefault(name, dict(props))
    if "KKUTS" in table:
        for name in UTS_SHADERS:
            table.setdefault(name, dict(table["KKUTS"]))
    summary = json.loads((audit / "summary.json").read_text(encoding="utf-8"))
    me = summary.get("fix", {}).get("me_properties", {})
    anywhere: dict[str, str] = {}
    for kind, letter in (("texture", "t"), ("color", "c"), ("float", "f")):
        for name, _count in me.get(kind, []):
            anywhere.setdefault("_" + name, letter)
    table[""] = anywhere
    return dict(sorted(table.items()))


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    out = Path(argv[2]) if len(argv) > 2 else Path(__file__).resolve().parent.parent / "kk_plugin" / "data" / "shader-properties.json"
    table = build(Path(argv[1]))
    out.write_text(json.dumps(table, indent=1, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{out}: {len(table) - 1} shaders, {len(table[''])} Material Editor properties")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
