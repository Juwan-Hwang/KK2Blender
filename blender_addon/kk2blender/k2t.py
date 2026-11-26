"""Reads KK2Blender track files (.k2t), written by the Studio plugin. See docs/bake-format.md.

Pure Python and numpy, so it runs (and is tested) outside Blender.
"""

from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

MAGIC = b"K2T1"


class TrackFileError(Exception):
    pass


@dataclass
class ChannelInfo:
    name: str
    kind: str
    width: int
    start: int
    count: int
    constant: bool
    offset: int


@dataclass
class TrackFile:
    """One track file: its header and channels. Channel data is read on demand."""

    path: Path
    header: dict
    frame_count: int
    channels: dict[str, ChannelInfo]
    _data: memoryview = field(repr=False, default=None)

    def __contains__(self, name: str) -> bool:
        return name in self.channels

    def names(self, prefix: str = "") -> list[str]:
        return [n for n in self.channels if n.startswith(prefix)]

    def raw(self, name: str) -> np.ndarray:
        """The stored records: (count, width), or (1, width) for a constant channel."""
        ch = self.channels[name]
        rows = 1 if ch.constant else ch.count
        return np.frombuffer(self._data, dtype="<f4", count=rows * ch.width, offset=ch.offset).reshape(rows, ch.width)

    def full(self, name: str) -> np.ndarray:
        """(frame_count, width) float32: every frame, NaN where the channel had no record."""
        ch = self.channels[name]
        out = np.full((self.frame_count, ch.width), np.nan, dtype=np.float32)
        raw = self.raw(name)
        end = min(self.frame_count, ch.start + ch.count)
        if end > ch.start:
            out[ch.start:end] = raw if ch.constant else raw[: end - ch.start]
        return out

    def present(self, name: str) -> np.ndarray:
        """(frame_count,) bool: frames where the channel has a real record."""
        return ~np.isnan(self.full(name)).all(axis=1)


def read(path) -> TrackFile:
    path = Path(path)
    data = path.read_bytes()
    if len(data) < 8 or data[:4] != MAGIC:
        raise TrackFileError(f"{path} is not a KK2Blender track file")
    (length,) = struct.unpack_from("<I", data, 4)
    if 8 + length > len(data):
        raise TrackFileError(f"{path}: the header runs past the end of the file")
    try:
        header = json.loads(data[8:8 + length].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise TrackFileError(f"{path}: unreadable header ({e})") from e
    base = 8 + length
    payload = memoryview(data)[base:]
    channels = {}
    for c in header.get("channels", []):
        info = ChannelInfo(c["name"], c["kind"], int(c["width"]), int(c["start"]), int(c["count"]),
                           bool(c["constant"]), int(c["offset"]))
        rows = 1 if info.constant else info.count
        if info.offset + rows * info.width * 4 > len(payload):
            raise TrackFileError(f"{path}: channel {info.name} runs past the end of the file")
        channels[info.name] = info
    return TrackFile(path, header, int(header.get("frame_count", 0)), channels, payload)
