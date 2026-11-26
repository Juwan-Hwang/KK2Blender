"""The one place that converts the game's space into Blender's.

Unity is left-handed with +Y up. KKBP writes glTF (right-handed, +Y up) by mirroring X, and glTF
comes into Blender (right-handed, +Z up) as (x, y, z) -> (x, -z, y). Together a Unity vector
(x, y, z) is Blender (-x, -z, y). A transform matrix M becomes C M C^T, which is a proper rotation
again, since mirroring twice cancels.

Matrices from the plugin are 12 floats: the top three rows of a 4x4, row-major.
"""

from __future__ import annotations

import numpy as np

# Unity -> Blender, glTF -> Blender.
UNITY_TO_BLENDER = np.array([[-1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])
GLTF_TO_BLENDER = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])


def _full(c3: np.ndarray) -> np.ndarray:
    c = np.eye(4)
    c[:3, :3] = c3
    return c


C_UNITY = _full(UNITY_TO_BLENDER)
C_GLTF = _full(GLTF_TO_BLENDER)


def rows_to_matrices(rows: np.ndarray) -> np.ndarray:
    """(..., 12) -> (..., 4, 4)."""
    rows = np.asarray(rows, dtype=np.float64)
    shape = rows.shape[:-1]
    m = np.zeros(shape + (4, 4))
    m[..., :3, :] = rows.reshape(shape + (3, 4))
    m[..., 3, 3] = 1.0
    return m


def unity_to_blender(m: np.ndarray) -> np.ndarray:
    """(..., 4, 4) Unity transforms -> Blender transforms."""
    return C_UNITY @ m @ C_UNITY.T


def gltf_to_blender(m: np.ndarray) -> np.ndarray:
    return C_GLTF @ m @ C_GLTF.T


def unity_point(p) -> np.ndarray:
    return np.asarray(p, dtype=np.float64) @ UNITY_TO_BLENDER.T


def quat_to_matrix(q) -> np.ndarray:
    """glTF quaternion (x, y, z, w) -> 3x3."""
    x, y, z, w = (float(v) for v in q)
    n = x * x + y * y + z * z + w * w
    s = 2.0 / n if n > 0 else 0.0
    return np.array([
        [1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w)],
        [s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w)],
        [s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y)],
    ])


def trs(translation, rotation_xyzw, scale=(1.0, 1.0, 1.0)) -> np.ndarray:
    m = np.eye(4)
    m[:3, :3] = quat_to_matrix(rotation_xyzw) * np.asarray(scale, dtype=np.float64)
    m[:3, 3] = translation
    return m


def matrices_to_quaternions(r: np.ndarray) -> np.ndarray:
    """(N, 3, 3) rotation matrices -> (N, 4) quaternions (w, x, y, z), Blender's order."""
    r = np.asarray(r, dtype=np.float64)
    n = r.shape[0]
    q = np.empty((n, 4))
    tr = r[:, 0, 0] + r[:, 1, 1] + r[:, 2, 2]
    cases = np.stack([tr, r[:, 0, 0], r[:, 1, 1], r[:, 2, 2]], axis=1).argmax(axis=1)

    i = cases == 0
    s = np.sqrt(np.maximum(tr[i] + 1.0, 1e-12)) * 2
    q[i, 0] = 0.25 * s
    q[i, 1] = (r[i, 2, 1] - r[i, 1, 2]) / s
    q[i, 2] = (r[i, 0, 2] - r[i, 2, 0]) / s
    q[i, 3] = (r[i, 1, 0] - r[i, 0, 1]) / s

    i = cases == 1
    s = np.sqrt(np.maximum(1.0 + r[i, 0, 0] - r[i, 1, 1] - r[i, 2, 2], 1e-12)) * 2
    q[i, 0] = (r[i, 2, 1] - r[i, 1, 2]) / s
    q[i, 1] = 0.25 * s
    q[i, 2] = (r[i, 0, 1] + r[i, 1, 0]) / s
    q[i, 3] = (r[i, 0, 2] + r[i, 2, 0]) / s

    i = cases == 2
    s = np.sqrt(np.maximum(1.0 + r[i, 1, 1] - r[i, 0, 0] - r[i, 2, 2], 1e-12)) * 2
    q[i, 0] = (r[i, 0, 2] - r[i, 2, 0]) / s
    q[i, 1] = (r[i, 0, 1] + r[i, 1, 0]) / s
    q[i, 2] = 0.25 * s
    q[i, 3] = (r[i, 1, 2] + r[i, 2, 1]) / s

    i = cases == 3
    s = np.sqrt(np.maximum(1.0 + r[i, 2, 2] - r[i, 0, 0] - r[i, 1, 1], 1e-12)) * 2
    q[i, 0] = (r[i, 1, 0] - r[i, 0, 1]) / s
    q[i, 1] = (r[i, 0, 2] + r[i, 2, 0]) / s
    q[i, 2] = (r[i, 1, 2] + r[i, 2, 1]) / s
    q[i, 3] = 0.25 * s

    q /= np.linalg.norm(q, axis=1, keepdims=True)
    return q


def continuous(q: np.ndarray) -> np.ndarray:
    """Flips quaternion signs so each is on the same side as the one before, so F-curves don't jump."""
    q = q.copy()
    for i in range(1, len(q)):
        if np.dot(q[i], q[i - 1]) < 0:
            q[i] = -q[i]
    return q


def decompose(m: np.ndarray):
    """(N, 4, 4) -> location (N, 3), quaternion (N, 4) wxyz continuous, scale (N, 3).

    Negative determinants (mirrors) don't occur in game poses; a degenerate axis gets scale 0 and
    an identity rotation for that axis."""
    loc = m[:, :3, 3].copy()
    basis = m[:, :3, :3]
    scale = np.linalg.norm(basis, axis=1)
    safe = np.where(scale > 1e-12, scale, 1.0)
    rot = basis / safe[:, None, :]
    # Re-orthonormalise, so a little shear from inherited non-uniform scale doesn't skew the rotation.
    u, _, vt = np.linalg.svd(rot)
    rot = u @ vt
    bad = np.linalg.det(rot) < 0
    if bad.any():
        u[bad, :, -1] *= -1
        rot[bad] = u[bad] @ vt[bad]
    return loc, continuous(matrices_to_quaternions(rot)), scale
