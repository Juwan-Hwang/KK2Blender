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


def write_glb_triangle(path, node_translation=(0.0, 0.0, 0.0)):
    """A one-triangle .glb in glTF space, like the plugin's prop files."""
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
    j = json.dumps(gltf).encode()
    j += b" " * ((4 - len(j) % 4) % 4)
    total = 12 + 8 + len(j) + 8 + len(binary)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(j), 0x4E4F534A) + j
                           + struct.pack("<II", len(binary), 0x004E4942) + binary)
