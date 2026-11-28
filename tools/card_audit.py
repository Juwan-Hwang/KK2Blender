#!/usr/bin/env python3
"""Static audit of Koikatsu (KK) character cards, without running the game.

Reads card PNGs (and optionally the Sideloader mods folder) and reports, per card, the things the
KK2Blender exporter / Blender importer may mishandle: missing or modded items, Material Editor shader
swaps / material copies / texture overrides, uncensor bodies, ABMX, accessory parents, and (with the
`bundles` step) the shaders and Unity Cloth components inside the mods the cards use.

Read-only on the game folder. Needs `msgpack` (and `lz4` for ABMX data); the `bundles` and `shaders` steps
also need `UnityPy`. Run it where the game files are (e.g. in WSL on /mnt/<drive>/Koikatu) to avoid copying GBs.

    card_audit.py modindex <mods_dir> <modindex.json>
    card_audit.py scan <out.jsonl> <dir_or_png>... [--jobs N]
    card_audit.py bundles <out.jsonl> <modindex.json> <bundles.json> [--max-secs S]
    card_audit.py shaders <out.jsonl> <modindex.json> <shaders.json>
    card_audit.py summary <out.jsonl> <modindex.json> <shaders.json> <bundles.json> <summary.json>

The format follows ChaFile.LoadFile (KK): PNG, then int32 product no, string marker, string version,
int32 + face PNG, int32 + MessagePack BlockHeader, int64 total, then the blocks (Custom, Coordinate,
Parameter, Status, KKEx). KKEx is ExtensibleSaveFormat's Dictionary<guid, [version, data]>.
"""
import io
import json
import os
import struct
import sys
import time
import zipfile
from collections import Counter, defaultdict

import msgpack

# ChaListDefine.CategoryNo (KK)
CATEGORY = {
    100: "bo_head", 101: "bo_hair_b", 102: "bo_hair_f", 103: "bo_hair_s", 104: "bo_hair_o", 105: "co_top",
    106: "co_bot", 107: "co_bra", 108: "co_shorts", 109: "co_gloves", 110: "co_panst", 111: "co_socks",
    112: "co_shoes", 120: "ao_none", 121: "ao_hair", 122: "ao_head", 123: "ao_face", 124: "ao_neck",
    125: "ao_body", 126: "ao_waist", 127: "ao_leg", 128: "ao_arm", 129: "ao_hand", 130: "ao_kokan",
    200: "cpo_sailor_a", 201: "cpo_sailor_b", 202: "cpo_sailor_c", 210: "cpo_jacket_a", 211: "cpo_jacket_b",
    212: "cpo_jacket_c", 400: "mt_face_detail", 401: "mt_eyeshadow", 402: "mt_cheek", 403: "mt_lip",
    404: "mt_lipline", 405: "mt_face_paint", 406: "mt_eyebrow", 407: "mt_eye_white", 408: "mt_eye",
    409: "mt_eye_gradation", 410: "mt_eye_hi_up", 411: "mt_eye_hi_down", 412: "mt_eyeline_up",
    413: "mt_eyeline_down", 414: "mt_nose", 415: "mt_mole", 416: "face_preset", 420: "mt_body_detail",
    421: "mt_body_paint", 422: "mt_sunburn", 423: "mt_nip", 424: "mt_underhair", 430: "mt_pattern",
    431: "mt_emblem", 432: "mt_ramp", 433: "mt_hairgloss",
}
MESH_CATEGORIES = {c for c in CATEGORY if 100 <= c <= 130 or 200 <= c <= 212}
ME_OBJECT = {0: "Unknown", 1: "Clothing", 2: "Accessory", 3: "Hair", 4: "Character"}
ME_RENDERER_PROP = {0: "Enabled", 1: "ShadowCastingMode", 2: "ReceiveShadows", 3: "RecalculateNormals",
                    4: "UpdateWhenOffscreen"}

# Shaders the KKBP material templates model (KKBP importer/exporter shader lists); anything else is
# "unknown" to the Blender side and falls back to a generic template.
KNOWN_SHADERS = set("""
Koikano/create_eye Koikano/create_eyew Koikano/create_skinbody Koikano/create_skinface Koikano/create_topN
Koikano/hair_main_low_sun Koikano/hair_main_sun Koikano/hair_main_sun_front Koikano/low_clothes_alpha
Koikano/low_clothes_opaque Koikano/main_add Koikano/main_clothes_alpha Koikano/main_clothes_emblem
Koikano/main_clothes_item Koikano/main_clothes_item_glasses Koikano/main_clothes_opaque Koikano/main_color
Koikano/main_eye Koikano/main_eyew Koikano/main_nose Koikano/main_skin Koikano/sub_sp_yousei_butterfly
Koikano/sub_texture_animation Shader?Forge/AlphaMaskMultiply Shader?Forge/FlatColorTransparent
Shader?Forge/create_body Shader?Forge/create_eye Shader?Forge/create_eyewhite Shader?Forge/create_hair
Shader?Forge/create_head Shader?Forge/create_topN Shader?Forge/hair_07 Shader?Forge/hair_08
Shader?Forge/main_StandardMDK_studio Shader?Forge/main_alpha Shader?Forge/main_color Shader?Forge/main_emblem
Shader?Forge/main_emblem_clothes Shader?Forge/main_hair Shader?Forge/main_hair_front Shader?Forge/main_item
Shader?Forge/main_item_ditherd Shader?Forge/main_item_emission Shader?Forge/main_item_glasses
Shader?Forge/main_item_studio Shader?Forge/main_item_studio_add Shader?Forge/main_item_studio_alpha
Shader?Forge/main_opaque Shader?Forge/main_opaque2 Shader?Forge/main_opaque_low Shader?Forge/main_skin
Shader?Forge/main_skin_low Shader?Forge/main_texture Shader?Forge/mnpb Shader?Forge/shadowcast
Shader?Forge/studio_hair_07 Shader?Forge/toon_eye_lod0 Shader?Forge/toon_eyew_lod0 Shader?Forge/toon_glasses_lod0
Shader?Forge/toon_nose_lod0 Shader?Forge/toon_textureanimation xukmi/EyePlus xukmi/EyePlusTess xukmi/EyeWPlus
xukmi/EyeWPlusTess xukmi/HairFrontPlus xukmi/HairFrontPlusReflect xukmi/HairFrontPlusTess
xukmi/HairFrontPlusTessReflect xukmi/HairPlus xukmi/HairPlusReflect xukmi/HairPlusTess xukmi/HairPlusTessReflect
xukmi/MainAlphaPlus xukmi/MainAlphaPlusTess xukmi/MainItemAlphaPlus xukmi/MainItemAlphaPlusTess xukmi/MainItemPlus
xukmi/MainItemPlusTess xukmi/MainItemStudioAlphaPlus xukmi/MainItemStudioAlphaPlusTess xukmi/MainItemStudioPlus
xukmi/MainItemStudioPlusTess xukmi/MainOpaquePlus xukmi/MainOpaquePlusTess xukmi/SkinPlus xukmi/SkinPlusReflect
xukmi/SkinPlusTess xukmi/SkinPlusTessReflect
""".replace("?", " ").replace("Shader Forge/", "Shader_Forge/").split())
KNOWN_SHADERS = {s.replace("Shader_Forge/", "Shader Forge/") for s in KNOWN_SHADERS}


def shader_known(name):
    return name in KNOWN_SHADERS


def _ext_hook(code, data):
    # MessagePack-CSharp's LZ4 block (ext 99): msgpack int (uncompressed length), then the LZ4 block.
    if code == 99:
        import lz4.block
        n = msgpack.unpackb(data[:5])
        return _unpack(lz4.block.decompress(data[5:], uncompressed_size=n), True)
    return msgpack.ExtType(code, data)


def _unpack(b, lz4=False):
    if lz4:
        return msgpack.unpackb(b, raw=False, strict_map_key=False, ext_hook=_ext_hook)
    return msgpack.unpackb(b, raw=False, strict_map_key=False)


def _rstr(f):
    n = s = 0
    while True:
        b = f.read(1)[0]
        n |= (b & 0x7F) << s
        s += 7
        if b < 0x80:
            break
    return f.read(n).decode("utf-8", "replace")


def _i32(f):
    return struct.unpack("<i", f.read(4))[0]


def _len_blobs(raw, count):
    g = io.BytesIO(raw)
    out = []
    for _ in range(count):
        n = _i32(g)
        out.append(_unpack(g.read(n)))
    return out, g


def read_card(path):
    """Returns the card's blocks: marker, parameter, face, body, hair, coordinates, ext."""
    data = open(path, "rb").read()
    i = data.find(b"IEND")
    if i < 0 or len(data) < i + 12:
        raise ValueError("not a card (no data after the PNG)")
    f = io.BytesIO(data[i + 8:])
    product = _i32(f)
    marker = _rstr(f)
    version = _rstr(f)
    if "KoiKatu" not in marker:
        raise ValueError("not a Koikatsu character card: " + marker)
    n = _i32(f)
    f.seek(n, 1)
    n = _i32(f)
    header = _unpack(f.read(n))
    f.read(8)
    base = f.tell()
    card = {"product": product, "marker": marker, "version": version, "size": len(data)}
    card["block_errors"] = []
    for info in header["lstInfo"]:
        f.seek(base + info["pos"])
        raw = f.read(info["size"])
        name = info["name"]
        try:
            _read_block(card, name, raw, info)
        except Exception as ex:
            card["block_errors"].append("%s: %r (size %d, read %d)" % (name, ex, info["size"], len(raw)))
    return card


def _read_block(card, name, raw, info):
    if True:
        if name == "Custom":
            (card["face"], card["body"], card["hair"]), _ = _len_blobs(raw, 3)
        elif name == "Coordinate":
            coords = []
            for blob in _unpack(raw):
                g = io.BytesIO(blob)
                clothes = _unpack(g.read(_i32(g)))
                acc = _unpack(g.read(_i32(g)))
                coords.append({"clothes": clothes, "accessory": acc})
            card["coordinates"] = coords
        elif name == "Parameter":
            card["parameter"] = _unpack(raw)
        elif name == "KKEx":
            card["ext"] = _unpack(raw)
            card["ext_size"] = info["size"]


def _ext(card, guid):
    v = (card.get("ext") or {}).get(guid)
    if isinstance(v, list) and len(v) == 2:
        return v[1] or {}
    return None


def _me_list(me, key):
    v = me.get(key)
    if not v:
        return []
    try:
        return _unpack(v)
    except Exception:
        return []


def analyze(path):
    card = read_card(path)
    p = card.get("parameter") or {}
    out = {
        "file": path, "marker": card["marker"], "size_mb": round(card["size"] / 1e6, 1),
        "name": ((p.get("lastname") or "") + " " + (p.get("firstname") or "")).strip(),
        "block_errors": card.get("block_errors"),
        "sex": p.get("sex"), "plugins": sorted((card.get("ext") or {}).keys()),
    }
    face, body, hair = card.get("face") or {}, card.get("body") or {}, card.get("hair") or {}
    out["head_id"] = face.get("headId")
    out["hair_ids"] = [h.get("id") for h in hair.get("parts", [])]

    # Sideloader: every modded slot the card references.
    uar = _ext(card, "com.bepis.sideloader.universalautoresolver") or _ext(card, "EC.Core.Sideloader.UniversalAutoResolver")
    resolved = []
    for b in (uar or {}).get("info") or []:
        try:
            r = _unpack(b)
        except Exception:
            continue
        resolved.append({"guid": r.get("ModID"), "slot": r.get("Slot"), "prop": r.get("Property"),
                         "cat": r.get("CategoryNo")})
    out["mods"] = resolved

    # Accessories (all outfits) and clothes ids.
    acc = []
    for ci, c in enumerate(card.get("coordinates") or []):
        for si, a in enumerate((c["accessory"] or {}).get("parts", [])):
            if a.get("type", 120) != 120:
                acc.append({"outfit": ci, "slot": si, "type": a.get("type"), "id": a.get("id"),
                            "parent": a.get("parentKey")})
    out["accessories"] = acc
    out["outfits"] = len(card.get("coordinates") or [])

    # Uncensor Selector.
    us = _ext(card, "com.deathweasel.bepinex.uncensorselector")
    if us is not None:
        out["uncensor"] = {k: us.get(k) for k in ("BodyGUID", "PenisGUID", "BallsGUID", "DisplayPenis", "DisplayBalls")}

    # ABMX.
    abm = _ext(card, "KKABMPlugin.ABMData")
    if abm and abm.get("boneData"):
        try:
            bd = _unpack(abm["boneData"], True)
            names = [x[0] for x in bd if isinstance(x, (list, tuple)) and x and isinstance(x[0], str)]
            out["abmx_bones"] = len(bd) if isinstance(bd, (list, dict)) else -1
            out["abmx_names"] = names[:200]
        except Exception:
            out["abmx_bones"] = -1

    # Material Editor.
    me = _ext(card, "com.deathweasel.bepinex.materialeditor")
    if me:
        tex = me.get("TextureDictionary")
        texd = {}
        if tex:
            try:
                texd = _unpack(tex)
            except Exception:
                texd = {}
        m = {"textures": len(texd), "texture_mb": round(sum(len(v) for v in texd.values()) / 1e6, 1)}

        def obj(x):
            return ME_OBJECT.get(x.get("ObjectType"), str(x.get("ObjectType")))

        m["shaders"] = [{"obj": obj(x), "outfit": x.get("CoordinateIndex"), "slot": x.get("Slot"),
                         "material": x.get("MaterialName"), "shader": x.get("ShaderName"),
                         "queue": x.get("RenderQueue")}
                        for x in _me_list(me, "MaterialShaderList") if x.get("ShaderName") or x.get("RenderQueue")]
        m["copies"] = [{"obj": obj(x), "outfit": x.get("CoordinateIndex"), "slot": x.get("Slot"),
                        "material": x.get("MaterialName"), "copy": x.get("MaterialCopyName")}
                       for x in _me_list(me, "MaterialCopyList")]
        m["renderer"] = [{"obj": obj(x), "outfit": x.get("CoordinateIndex"), "slot": x.get("Slot"),
                          "renderer": x.get("RendererName"), "prop": ME_RENDERER_PROP.get(x.get("Property"), x.get("Property")),
                          "value": x.get("Value")}
                         for x in _me_list(me, "RendererPropertyList")]
        m["texprops"] = [{"obj": obj(x), "outfit": x.get("CoordinateIndex"), "slot": x.get("Slot"),
                          "material": x.get("MaterialName"), "prop": x.get("Property"),
                          "tex": x.get("TexID") is not None,
                          "uv": x.get("Offset") is not None or x.get("Scale") is not None}
                         for x in _me_list(me, "MaterialTexturePropertyList")]
        m["float_props"] = Counter(x.get("Property") for x in _me_list(me, "MaterialFloatPropertyList"))
        m["color_props"] = Counter(x.get("Property") for x in _me_list(me, "MaterialColorPropertyList"))
        m["keyword_props"] = Counter(x.get("Property") for x in _me_list(me, "MaterialKeywordPropertyList"))
        m["char_materials"] = sorted({x.get("MaterialName") for k in ("MaterialFloatPropertyList", "MaterialColorPropertyList",
                                                                         "MaterialTexturePropertyList", "MaterialShaderList")
                                      for x in _me_list(me, k) if x.get("ObjectType") == 4})
        out["me"] = m
    return out


def cmd_scan(outp, targets, jobs=4):
    from multiprocessing import Pool
    files = []
    for t in targets:
        if os.path.isfile(t):
            files.append(t)
        else:
            for dp, _, fn in os.walk(t):
                files += [os.path.join(dp, f) for f in fn if f.lower().endswith(".png")]
    done = set()
    if os.path.exists(outp):
        for line in open(outp, encoding="utf-8"):
            try:
                done.add(json.loads(line)["file"])
            except Exception:
                pass
    todo = [f for f in sorted(files) if f not in done]
    print("cards", len(files), "todo", len(todo), flush=True)
    t0 = time.time()
    with open(outp, "a", encoding="utf-8") as w, Pool(jobs) as pool:
        for k, r in enumerate(pool.imap_unordered(_safe, todo, chunksize=2)):
            w.write(json.dumps(r, ensure_ascii=False, default=str) + "\n")
            if k % 100 == 0:
                w.flush()
                print(k, round(time.time() - t0), flush=True)
    print("done", round(time.time() - t0), flush=True)


def _safe(f):
    try:
        return analyze(f)
    except Exception as ex:
        return {"file": f, "error": repr(ex)[:300]}


def cmd_modindex(root, outp):
    import xml.etree.ElementTree as ET
    mods, errors, n, t0 = {}, [], 0, time.time()
    for dp, _, fn in os.walk(root):
        for f in fn:
            if not f.lower().endswith((".zipmod", ".zip")):
                continue
            p = os.path.join(dp, f)
            n += 1
            try:
                z = zipfile.ZipFile(p)
                names = z.namelist()
                mf = next((x for x in names if x.lower() == "manifest.xml"), None)
                if not mf:
                    continue
                x = ET.fromstring(z.read(mf).decode("utf-8-sig", "replace").strip())
                g = (x.findtext("guid") or "").strip()
                if not g:
                    continue
                e = {"path": p, "version": (x.findtext("version") or "").strip(), "name": (x.findtext("name") or "").strip(),
                     "author": (x.findtext("author") or "").strip(), "items": {}, "uncensors": [], "bundles": []}
                us = x.find("KK_UncensorSelector")
                if us is not None:
                    for kind in ("body", "penis", "balls"):
                        for b in us.findall(kind):
                            e["uncensors"].append({"kind": kind, "guid": b.findtext("guid"), "name": b.findtext("displayName"),
                                                   "raw": ET.tostring(b, encoding="unicode")[:800]})
                for me in x.findall("MaterialEditor"):
                    for sh in me.findall("Shader"):
                        e.setdefault("me_shaders", []).append(dict(sh.attrib))
                for nm in names:
                    low = nm.lower()
                    if low.endswith(".unity3d"):
                        e["bundles"].append(nm)
                    if low.startswith("abdata/list/characustom/") and low.endswith(".csv"):
                        txt = z.read(nm).decode("utf-8-sig", "replace").splitlines()
                        if len(txt) < 3:
                            continue
                        cat = txt[0].split(",")[0].strip()
                        if not cat.lstrip("-").isdigit():
                            continue
                        hi = next((k for k, l in enumerate(txt[:6]) if l.startswith("ID,")), None)
                        if hi is None:
                            continue
                        hdr = [h.strip() for h in txt[hi].split(",")]
                        for line in txt[hi + 1:]:
                            cols = line.split(",")
                            if cols and cols[0].strip().lstrip("-").isdigit():
                                e["items"]["%d:%d" % (int(cat), int(cols[0]))] = dict(zip(hdr, [c.strip() for c in cols]))
                old = mods.get(g)
                if old is None or old["version"] <= e["version"]:
                    if old:
                        e["dupes"] = old.get("dupes", []) + [old["path"]]
                    mods[g] = e
                else:
                    old.setdefault("dupes", []).append(p)
            except Exception as ex:
                errors.append([p, repr(ex)[:200]])
    json.dump({"mods": mods, "errors": errors, "scanned": n, "secs": time.time() - t0}, open(outp, "w"))
    print("done", n, len(mods), len(errors), round(time.time() - t0))


def load_results(path):
    return [json.loads(l) for l in open(path, encoding="utf-8")]


def referenced_items(results, index):
    """(guid, category, slot) -> number of cards, for mesh categories, with the mod's list row."""
    mods = index["mods"]
    refs = Counter()
    for r in results:
        for m in {(x["guid"], x["cat"], x["slot"]) for x in r.get("mods", []) if x["cat"] in MESH_CATEGORIES}:
            refs[m] += 1
    return refs


def cmd_bundles(results_path, index_path, outp, max_secs=1500):
    """Opens the asset bundles of the mod items the cards reference and lists, per bundle: shader names
    (Shader objects inside it, and the external file of shaders it only references), materials with their
    shader, and the Cloth / DynamicBone / SkinnedMeshRenderer components it holds."""
    import UnityPy
    results, index = load_results(results_path), json.load(open(index_path))
    mods = index["mods"]
    refs = referenced_items(results, index)
    bundles = defaultdict(set)  # (zip path, bundle path) -> items
    for (g, cat, slot), _ in refs.most_common():
        mod = mods.get(g)
        if not mod:
            continue
        row = mod["items"].get("%s:%s" % (cat, slot))
        if not row:
            continue
        ab = row.get("MainAB")
        if ab and ab != "0":
            bundles[(mod["path"], "abdata/" + ab)].add("%s|%s|%s|%s" % (g, cat, slot, row.get("MainData")))
    done = {}
    if os.path.exists(outp):
        done = json.load(open(outp))
    t0 = time.time()
    order = sorted(bundles, key=lambda k: -len(bundles[k]))
    for key in order:
        sk = key[0] + "!" + key[1]
        if sk in done:
            continue
        if time.time() - t0 > max_secs:
            break
        rec = {"items": sorted(bundles[key])}
        try:
            z = zipfile.ZipFile(key[0])
            name = next((n for n in z.namelist() if n.lower() == key[1].lower()), None)
            if name is None:
                rec["error"] = "bundle not in zip"
            else:
                env = UnityPy.load(z.read(name))
                shaders, mats, comps = set(), [], Counter()
                cab_ext = {}
                for obj in env.objects:
                    t = obj.type.name
                    if t == "Shader":
                        try:
                            d = obj.read_typetree()
                            nm = (d.get("m_ParsedForm") or {}).get("m_Name") or d.get("m_Name")
                            shaders.add(nm)
                        except Exception:
                            shaders.add("?")
                    elif t == "Material":
                        try:
                            d = obj.read_typetree()
                            sh = d.get("m_Shader") or {}
                            sname = None
                            if sh.get("m_FileID") == 0 and sh.get("m_PathID"):
                                so = obj.assets_file.objects.get(sh["m_PathID"])
                                if so is not None:
                                    sd = so.read_typetree()
                                    sname = (sd.get("m_ParsedForm") or {}).get("m_Name") or sd.get("m_Name")
                            elif sh.get("m_FileID"):
                                ex = obj.assets_file.externals[sh["m_FileID"] - 1]
                                sname = "external:%s#%s" % (getattr(ex, "path", "?"), sh.get("m_PathID"))
                            mats.append([d.get("m_Name"), sname,
                                         sorted(k for k, _ in (d.get("m_SavedProperties") or {}).get("m_TexEnvs", []))[:40],
                                         sorted(k for k, _ in (d.get("m_SavedProperties") or {}).get("m_Floats", []))[:60],
                                         sorted(k for k, _ in (d.get("m_SavedProperties") or {}).get("m_Colors", []))[:40],
                                         d.get("m_CustomRenderQueue")])
                        except Exception as ex:
                            mats.append([None, "error:" + repr(ex)[:80]])
                    elif t in ("Cloth", "SkinnedMeshRenderer", "MeshRenderer", "ParticleSystem", "Light", "Projector"):
                        comps[t] += 1
                    elif t == "MonoBehaviour":
                        try:
                            sc = obj.read().m_Script.read().m_ClassName
                            comps["MB:" + sc] += 1
                        except Exception:
                            comps["MB:?"] += 1
                rec.update({"shaders": sorted(s for s in shaders if s), "materials": mats, "components": dict(comps)})
        except Exception as ex:
            rec["error"] = repr(ex)[:300]
        done[sk] = rec
        if len(done) % 25 == 0:
            json.dump(done, open(outp, "w"))
    json.dump(done, open(outp, "w"))
    print("bundles", len(done), "of", len(order), round(time.time() - t0))


def _shader_info(d):
    pf = d.get("m_ParsedForm") or {}
    props = []
    for p in ((pf.get("m_PropInfo") or {}).get("m_Props") or []):
        kind = {0: "color", 1: "vector", 2: "float", 3: "range", 4: "texture"}.get(p.get("m_Type"), p.get("m_Type"))
        props.append([p.get("m_Name"), kind])
    subs = []
    for ss in pf.get("m_SubShaders") or []:
        passes = []
        for ps in ss.get("m_Passes") or []:
            st = ps.get("m_State") or {}
            tags = (st.get("m_Tags") or {}).get("tags") or []
            passes.append({"name": st.get("m_Name"), "tags": dict(tags) if tags and isinstance(tags[0], (list, tuple)) else tags})
        tags = (ss.get("m_Tags") or {}).get("tags") or []
        subs.append({"tags": dict(tags) if tags and isinstance(tags[0], (list, tuple)) else tags, "passes": passes})
    return {"name": pf.get("m_Name") or d.get("m_Name"), "props": props, "subshaders": subs,
            "fallback": pf.get("m_FallbackName"), "keywords": (d.get("m_KeywordNames") or [])[:80] if isinstance(d.get("m_KeywordNames"), list) else None}


def cmd_shaders(results_path, index_path, outp):
    """For every shader Material Editor swaps in on the cards that the KKBP templates don't model, reads the
    shader from the mod that provides it (manifest <MaterialEditor><Shader>) and lists its properties,
    sub shaders, passes and tags."""
    import UnityPy
    results, mods = load_results(results_path), json.load(open(index_path))["mods"]
    used = Counter()
    for r in results:
        for n in {s["shader"] for s in (r.get("me") or {}).get("shaders", []) if s.get("shader")}:
            used[n] += 1
    provider = {}
    for g, m in mods.items():
        for s in m.get("me_shaders", []):
            provider.setdefault(s.get("Name"), (g, m["path"], s.get("AssetBundle")))
    out = {}
    for name, cards in used.most_common():
        rec = {"cards": cards, "known_to_kkbp": shader_known(name)}
        if name in provider:
            g, path, ab = provider[name]
            rec.update({"mod": g, "bundle": ab})
            if not rec["known_to_kkbp"] or name.startswith("xukmi/"):
                try:
                    z = zipfile.ZipFile(path)
                    nm = next((n for n in z.namelist() if n.lower() == ("abdata/" + ab).lower()), None)
                    env = UnityPy.load(z.read(nm))
                    for obj in env.objects:
                        if obj.type.name == "Shader":
                            info = _shader_info(obj.read_typetree())
                            if info["name"] == name:
                                rec["shader"] = info
                except Exception as ex:
                    rec["error"] = repr(ex)[:200]
        else:
            rec["mod"] = None
        out[name] = rec
    json.dump(out, open(outp, "w"), indent=1)
    print("shaders", len(out))


VANILLA_CHARA_MATERIALS = {
    "cf_m_face_00", "cf_m_body", "cm_m_body", "cf_m_eyeline_00_up", "cf_m_eyeline_kage", "cf_m_eyeline_down",
    "cf_m_mayuge_00", "cf_m_hitomi_00", "cf_m_sirome_00", "cf_m_noseline_00", "cf_m_tooth", "cf_m_tang",
    "cf_m_gageye_00", "cf_m_gageye_01", "cf_m_gageye_02", "cf_m_namida_00", "cf_m_namida_01", "cf_m_namida_02",
    "cm_m_dan_f", "cm_m_dankon", "cm_m_dan", "cf_m_mm", "cf_m_hitomi_00_L", "cf_m_hitomi_00_R",
}


def _strip_copy(name):
    name = name or ""
    i = name.find(".MECopy")
    return name[:i] if i >= 0 else name


def summarize(results, index, shaders=None, bundles=None):
    """Groups every card's findings by problem class. Returns {class: {"cards": [...], "count": n}} plus
    fix data (shader properties, Material Editor property names, mod mesh facts)."""
    mods = index["mods"]
    uncensor_of = {u["guid"]: (g, u) for g, m in mods.items() for u in m.get("uncensors", [])}
    item_bundle = {}
    for key, b in (bundles or {}).items():
        for it in b.get("items", []):
            g, cat, slot, prefab = it.split("|", 3)
            item_bundle[(g, int(cat), int(slot))] = (key, prefab)
    classes = defaultdict(list)

    def add(cls, r, reason):
        classes[cls].append({"file": r["file"], "name": r.get("name", ""), "reason": reason})

    for r in results:
        if "error" in r:
            add("unreadable", r, r["error"])
            continue
        if r.get("block_errors"):
            add("truncated_or_corrupt_block", r, "; ".join(r["block_errors"]))
        if "Sun" in (r.get("marker") or ""):
            add("kks_card", r, r["marker"])
        # Sideloader references.
        missing_mesh, missing_other, modhead = Counter(), Counter(), []
        for x in r.get("mods", []):
            g, cat, slot = x["guid"], x["cat"], x["slot"]
            mod = mods.get(g)
            have = mod is not None and (cat is None or cat >= 500 or "%s:%s" % (cat, slot) in mod["items"])
            if not have:
                what = "%s (%s %s:%s)%s" % (g or "<no guid>", x.get("prop"), CATEGORY.get(cat, cat), slot, "" if mod is None else " [mod installed, slot missing]")
                (missing_mesh if cat in MESH_CATEGORIES else missing_other)[what] += 1
            if cat == 100:
                modhead.append(g)
        if missing_mesh:
            add("missing_mod_mesh", r, "; ".join(sorted(missing_mesh)))
        if missing_other:
            add("missing_mod_texture", r, "; ".join(sorted(missing_other)))
        if modhead:
            mats = sorted({_strip_copy(m) for m in (r.get("me") or {}).get("char_materials", [])} - VANILLA_CHARA_MATERIALS - {""})
            add("mod_head", r, "head %s%s" % (",".join(sorted(set(modhead))), ("; non-vanilla face materials: " + ", ".join(mats[:8])) if mats else ""))
        # Uncensor.
        u = r.get("uncensor") or {}
        body = u.get("BodyGUID")
        if body:
            if body not in uncensor_of and not body.startswith("Default.Body."):
                add("uncensor_missing", r, "body %s not installed" % body)
            else:
                add("uncensor_body", r, "%s (%s)" % (body, uncensor_of[body][1].get("name") if body in uncensor_of else "built-in default body"))
        else:
            add("uncensor_unset", r, "no body set: Uncensor Selector's default body is used (this install: Random)")
        if u.get("PenisGUID") or u.get("DisplayPenis"):
            add("uncensor_penis", r, "penis %s, shown %s" % (u.get("PenisGUID"), u.get("DisplayPenis")))
        # Material Editor.
        me = r.get("me") or {}
        unknown = Counter()
        for s in me.get("shaders", []):
            if s.get("shader") and not shader_known(s["shader"]):
                unknown["%s on %s %s" % (s["shader"], s["obj"], s.get("material"))] += 1
        if unknown:
            fam = sorted({k.split(" on ")[0] for k in unknown})
            add("me_unknown_shader", r, "shaders %s (%d materials)" % (", ".join(fam), sum(unknown.values())))
        known_swaps = sorted({s["shader"] for s in me.get("shaders", []) if s.get("shader") and shader_known(s["shader"])})
        if known_swaps:
            add("me_known_shader_swap", r, ", ".join(known_swaps))
        if any(not s.get("shader") and s.get("queue") for s in me.get("shaders", [])):
            add("me_render_queue", r, "%d render queue overrides" % sum(1 for s in me.get("shaders", []) if not s.get("shader")))
        if me.get("copies"):
            c = Counter(x["obj"] for x in me["copies"])
            add("me_material_copy", r, ", ".join("%s x%d" % kv for kv in c.items()) + "; e.g. " + ", ".join(sorted({x["copy"] for x in me["copies"]})[:4]))
        if me.get("texture_mb", 0) >= 50:
            add("me_heavy_textures", r, "%d textures, %.0f MB" % (me["textures"], me["texture_mb"]))
        dis = [x for x in me.get("renderer", []) if x["prop"] == "Enabled" and str(x["value"]) == "0"]
        if dis:
            add("me_renderer_disabled", r, "%d renderers disabled (e.g. %s)" % (len(dis), ", ".join(sorted({x["renderer"] for x in dis})[:4])))
        # Accessories.
        odd = Counter(a["parent"] for a in r.get("accessories", []) if not (a.get("parent") or "").startswith("a_n_"))
        if odd:
            add("accessory_unusual_parent", r, ", ".join("%s x%d" % ((k or "<empty>"), v) for k, v in odd.most_common(6)))
        hairacc = sum(1 for a in r.get("accessories", []) if a.get("type") == 121)
        if hairacc:
            add("hair_accessory", r, "%d ao_hair accessories" % hairacc)
        if r.get("abmx_bones"):
            add("abmx", r, "%s bone modifiers" % r["abmx_bones"])
        if r.get("outfits", 7) > 7:
            add("more_outfits", r, "%d outfits" % r["outfits"])
        plugs = set(r.get("plugins", []))
        for guid, cls in (("org.njaecha.plugins.objimport", "plugin_objimport"), ("nakay.kk.ClothingBlendShape", "plugin_clothing_blendshape"),
                          ("com.shallty.blendshapecreator", "plugin_blendshape_creator"), ("madevil.kk.AAAPK", "plugin_aaapk_reparent"),
                          ("BendUrAcc", "plugin_bendurAcc"), ("nakay.kk.ChaAlphaMask", "plugin_alpha_mask"),
                          ("com.deathweasel.bepinex.bodyshaders", "plugin_body_shaders"), ("org.njaecha.plugins.editfangs", "plugin_editfangs")):
            if guid in plugs:
                add(cls, r, guid)
        # Mod mesh facts from the bundles (inferred per bundle: a bundle can hold several items).
        cloth, implant, accclothes, bshaders, vshaders = set(), set(), set(), set(), set()
        for x in r.get("mods", []):
            kb = item_bundle.get((x["guid"], x["cat"], x["slot"]))
            if not kb:
                continue
            b = bundles[kb[0]]
            comps = b.get("components", {})
            tag = "%s %s:%s (%s)" % (x["guid"], CATEGORY.get(x["cat"], x["cat"]), x["slot"], kb[1])
            if comps.get("Cloth"):
                cloth.add(tag)
            if comps.get("MB:BoneImplantProcess"):
                implant.add(tag)
            if comps.get("MB:ChaAccessoryClothes"):
                accclothes.add(tag)
            for m in b.get("materials", []):
                sn = m[1] if len(m) > 1 else None
                if sn and not str(sn).startswith(("external:", "error:")) and not shader_known(sn):
                    (vshaders if str(sn).startswith("Shader Forge/") else bshaders).add("%s in %s" % (sn, x["guid"]))
        if cloth:
            add("mod_unity_cloth", r, "; ".join(sorted(cloth)[:6]))
        if implant:
            add("mod_bone_implant", r, "; ".join(sorted(implant)[:6]))
        if accclothes:
            add("mod_accessory_clothes", r, "; ".join(sorted(accclothes)[:6]))
        if bshaders:
            add("mod_unknown_shader", r, "; ".join(sorted(bshaders)[:8]))
        if vshaders:
            add("mod_unmodelled_vanilla_shader", r, "; ".join(sorted(vshaders)[:8]))

    # Fix data.
    fix = {}
    used_props = {"float": Counter(), "color": Counter(), "texture": Counter(), "keyword": Counter()}
    for r in results:
        me = r.get("me") or {}
        used_props["float"].update(me.get("float_props", {}))
        used_props["color"].update(me.get("color_props", {}))
        used_props["keyword"].update(me.get("keyword_props", {}))
        used_props["texture"].update(t["prop"] for t in me.get("texprops", []))
    fix["me_properties"] = {k: v.most_common() for k, v in used_props.items()}
    if shaders:
        fix["shaders"] = shaders
    if bundles:
        bs = Counter()
        for b in bundles.values():
            for m in b.get("materials", []):
                sn = m[1] if len(m) > 1 else None
                if sn and not shader_known(sn):
                    bs[str(sn) if not str(sn).startswith("external:") else "external"] += 1
        fix["mod_bundle_shaders"] = bs.most_common()
        fix["bundles_scanned"] = len(bundles)
        comps = Counter()
        for b in bundles.values():
            comps.update(b.get("components", {}))
        fix["components"] = comps.most_common()
    fix["mods_indexed"] = len(mods)
    refs = referenced_items(results, index)
    fix["bundles_total"] = len({(mods[g]["path"], mods[g]["items"].get("%s:%s" % (c, s), {}).get("MainAB"))
                                for (g, c, s) in refs if g in mods and mods[g]["items"].get("%s:%s" % (c, s), {}).get("MainAB") not in (None, "", "0")})
    out = {k: {"count": len(v), "cards": v} for k, v in sorted(classes.items(), key=lambda kv: -len(kv[1]))}
    return out, fix


def cmd_summary(results_path, index_path, shaders_path, bundles_path, outp):
    shaders = json.load(open(shaders_path)) if shaders_path and os.path.exists(shaders_path) else None
    bundles = json.load(open(bundles_path)) if bundles_path and os.path.exists(bundles_path) else None
    classes, fix = summarize(load_results(results_path), json.load(open(index_path)), shaders, bundles)
    json.dump({"classes": classes, "fix": fix}, open(outp, "w"), ensure_ascii=False, indent=1, default=str)
    for k, v in classes.items():
        print(v["count"], k)


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a:
        print(__doc__)
    elif a[0] == "modindex":
        cmd_modindex(a[1], a[2])
    elif a[0] == "scan":
        jobs = 4
        if "--jobs" in a:
            k = a.index("--jobs")
            jobs = int(a[k + 1])
            a = a[:k] + a[k + 2:]
        cmd_scan(a[1], a[2:], jobs)
    elif a[0] == "bundles":
        ms = 1500
        if "--max-secs" in a:
            k = a.index("--max-secs")
            ms = int(a[k + 1])
            a = a[:k] + a[k + 2:]
        cmd_bundles(a[1], a[2], a[3], ms)
    elif a[0] == "summary":
        cmd_summary(a[1], a[2], a[3] if len(a) > 3 else None, a[4] if len(a) > 4 else None, a[5] if len(a) > 5 else "summary.json")
    elif a[0] == "shaders":
        cmd_shaders(a[1], a[2], a[3])
    else:
        print(__doc__)
