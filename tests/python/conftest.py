import importlib.util
import struct
import json
import sys
import types
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
PKG = ROOT / "blender_addon" / "kk2blender"

# Load the add-on's pure modules as a package without running its bpy-dependent parts.
if "kk2blender" not in sys.modules:
    pkg = types.ModuleType("kk2blender")
    pkg.__path__ = [str(PKG)]
    sys.modules["kk2blender"] = pkg


def write_k2t(path, frame_count, channels, header=None):
    """A Python writer for tests, mirroring the plugin's TrackSet.Save.
    channels: list of (name, kind, start, records ndarray (count, width), constant)."""
    infos, blobs, offset = [], [], 0
    for name, kind, start, records, constant in channels:
        records = np.asarray(records, dtype="<f4")
        data = records[:1] if constant else records
        infos.append({"name": name, "kind": kind, "width": records.shape[1], "start": start,
                      "count": records.shape[0], "constant": constant, "offset": offset})
        blobs.append(data.tobytes())
        offset += data.nbytes
    h = dict(header or {})
    h["frame_count"] = frame_count
    h["channels"] = infos
    hb = json.dumps(h).encode()
    hb += b" " * ((4 - len(hb) % 4) % 4)
    Path(path).write_bytes(b"K2T1" + struct.pack("<I", len(hb)) + hb + b"".join(blobs))


def write_glb_triangle(path, node_translation=(0.0, 0.0, 0.0), material=None):
    """A one-triangle .glb in glTF space, like the plugin's prop files; `material`, a glTF material
    dict, is given to its triangle."""
    positions = np.array([[0, 0, 0], [0.1, 0, 0], [0, 0.1, 0]], dtype="<f4")
    indices = np.array([0, 1, 2], dtype="<u4")
    binary = positions.tobytes() + indices.tobytes()
    gltf = {
        "asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "tri", "mesh": 0, "translation": list(node_translation)}],
        "meshes": [{"name": "tri", "primitives": [{"attributes": {"POSITION": 0}, "indices": 1}]}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 36},
                        {"buffer": 0, "byteOffset": 36, "byteLength": 12}],
        "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
                       "min": [0, 0, 0], "max": [0.1, 0.1, 0]},
                      {"bufferView": 1, "componentType": 5125, "count": 3, "type": "SCALAR"}],
    }
    if material is not None:
        gltf["materials"] = [material]
        gltf["meshes"][0]["primitives"][0]["material"] = 0
    j = json.dumps(gltf).encode()
    j += b" " * ((4 - len(j) % 4) % 4)
    total = 12 + 8 + len(j) + 8 + len(binary)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(j), 0x4E4F534A) + j
                           + struct.pack("<II", len(binary), 0x004E4942) + binary)


def png_bytes(rgba=(255, 255, 255, 255), size=2):
    """A tiny solid RGBA PNG."""
    import zlib
    raw = b"".join(b"\x00" + bytes(rgba) * size for _ in range(size))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def write_glb_lightmapped_quad(path, base_rgba=(200, 100, 50, 255), lightmap_rgba=(51, 51, 51, 255)):
    """A triangle with a base colour texture and a lightmap on UV1, like the plugin's map parts."""
    positions = np.array([[0, 0, 0], [1, 0, 0], [0, 0, 1]], dtype="<f4")
    uv = np.array([[0, 0], [1, 0], [0, 1]], dtype="<f4")
    indices = np.array([0, 1, 2], dtype="<u4")
    img0, img1 = png_bytes(base_rgba), png_bytes(lightmap_rgba)
    blobs = [positions.tobytes(), uv.tobytes(), uv.tobytes(), indices.tobytes(), img0, img1]
    views, binary = [], b""
    for b in blobs:
        while len(binary) % 4:
            binary += b"\x00"
        views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(b)})
        binary += b
    while len(binary) % 4:
        binary += b"\x00"
    gltf = {
        "asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "floor", "mesh": 0}],
        "meshes": [{"name": "floor", "primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1, "TEXCOORD_1": 2},
                                                     "indices": 3, "material": 0}]}],
        "materials": [{"name": "floor_mat", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}},
                       "occlusionTexture": {"index": 1, "texCoord": 1}, "extras": {"kk2b_lightmap": "rgbm"}}],
        "textures": [{"source": 0}, {"source": 1}],
        "images": [{"bufferView": 4, "mimeType": "image/png"}, {"bufferView": 5, "mimeType": "image/png"}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": views,
        "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 0, 1]},
                      {"bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC2"},
                      {"bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC2"},
                      {"bufferView": 3, "componentType": 5125, "count": 3, "type": "SCALAR"}],
    }
    j = json.dumps(gltf).encode()
    j += b" " * ((4 - len(j) % 4) % 4)
    total = 12 + 8 + len(j) + 8 + len(binary)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(j), 0x4E4F534A) + j
                           + struct.pack("<II", len(binary), 0x004E4942) + binary)
