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
