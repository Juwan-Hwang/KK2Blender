import numpy as np

from kk2blender import pose, space


def rot_unity(axis, deg):
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    if axis == "x":
        r = [[1, 0, 0], [0, c, -s], [0, s, c]]
    elif axis == "y":
        r = [[c, 0, s], [0, 1, 0], [-s, 0, c]]
    else:
        r = [[c, -s, 0], [s, c, 0], [0, 0, 1]]
    m = np.eye(4)
    m[:3, :3] = r
    return m


def translate(x, y, z):
    m = np.eye(4)
    m[:3, 3] = (x, y, z)
    return m


def rows(m):
    return m[:3, :].reshape(12)


def test_unity_point_to_blender():
    np.testing.assert_allclose(space.unity_point([1, 2, 3]), [-1, -3, 2])


def test_converted_rotations_stay_proper():
    for axis in "xyz":
        m = space.unity_to_blender(rot_unity(axis, 37))
        assert np.isclose(np.linalg.det(m[:3, :3]), 1.0)


def test_unity_to_blender_moves_points_like_the_conversion():
    m = translate(1, 2, 3) @ rot_unity("y", 30)
    p = np.array([0.3, -0.2, 0.5, 1.0])
    expected = space.unity_point((m @ p)[:3])
    got = space.unity_to_blender(m) @ np.append(space.unity_point(p[:3]), 1.0)
    np.testing.assert_allclose(got[:3], expected, atol=1e-12)


def test_gltf_rest_matches_unity_rest():
    """A rest written by KKBP (Unity mirrored into glTF) converts to the same Blender matrix as the Unity one."""
    m_unity = translate(0.1, 1.2, -0.3) @ rot_unity("z", 25) @ rot_unity("x", -10)
    s = np.diag([-1.0, 1, 1, 1])
    m_gltf = s @ m_unity @ s
    np.testing.assert_allclose(space.gltf_to_blender(m_gltf), space.unity_to_blender(m_unity), atol=1e-12)


def test_quaternions_round_trip():
    rng = np.random.default_rng(1)
    q = rng.normal(size=(50, 4))
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    w, x, y, z = q.T
    r = np.stack([
        np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
        np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
        np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1),
    ], 1)
    back = space.matrices_to_quaternions(r)
    assert np.allclose(np.abs(np.sum(back * q, axis=1)), 1.0)


def test_decompose_and_continuity():
    ms = np.stack([translate(1, 2, 3) @ rot_unity("y", a) @ np.diag([2, 2, 2, 1]) for a in range(0, 720, 10)])
    loc, quat, scale = space.decompose(ms)
    np.testing.assert_allclose(loc, np.tile([1, 2, 3], (len(ms), 1)))
    np.testing.assert_allclose(scale, 2.0)
    assert (np.sum(quat[1:] * quat[:-1], axis=1) > 0).all()


def _gltf_bone(bid, parent, m_unity_local):
    """KKBP's skeleton.json record for a bone whose local Unity rest is m_unity_local (no scale)."""
    s = np.diag([-1.0, 1, 1, 1])
    g = s @ m_unity_local @ s
    r = g[:3, :3]
    q = space.matrices_to_quaternions(r[None])[0]  # w x y z
    return pose.RestBone(bid, bid, parent, tuple(g[:3, 3]), (q[1], q[2], q[3], q[0]))


def test_pose_reproduces_the_games_skinning():
    """End to end: Blender's skinning matrices P @ inv(L) from our basis equal the game's
    B(M_t inv(S)) inv(B(R_rest)), even when KKBP re-aimed the Blender bones."""
    rest_local = {"hips": translate(0, 1.0, 0) @ rot_unity("y", 10),
                  "spine": translate(0, 0.2, 0.01) @ rot_unity("x", -15)}
    bones = [_gltf_bone("hips", None, rest_local["hips"]), _gltf_bone("spine", "hips", rest_local["spine"])]
    rest = pose.rest_matrices(bones)
    np.testing.assert_allclose(rest["hips"], space.unity_to_blender(rest_local["hips"]), atol=1e-9)

    # KKBP's Blender bones: same heads, different axes (tails and roll re-aimed).
    ml = {name: rest[name] @ rot_unity("z", 90) @ rot_unity("x", 33) for name in rest}
    parents = {"hips": None, "spine": "hips"}
    game_scale = {"hips": (1.0, 1.0, 1.0), "spine": (1.1, 0.9, 1.0)}

    frames = 5
    world = {}
    baked = {}
    for name in ("hips", "spine"):
        seq = []
        for f in range(frames):
            local = rest_local[name] @ rot_unity("z", 7 * f) @ np.diag(list(game_scale[name]) + [1])
            m = local if parents[name] is None else world[(parents[name], f)] @ local
            world[(name, f)] = m
            seq.append(rows(m))
        baked[name] = np.array(seq)

    posed = {n: pose.posed_matrices(baked[n], rest[n], ml[n], game_scale[n]) for n in baked}
    basis = pose.basis_matrices(posed, parents, ml, frames)

    # Evaluate the pose as Blender does and compare skinning matrices.
    for f in range(frames):
        p_hips = ml["hips"] @ basis["hips"][f]
        p_spine = p_hips @ np.linalg.inv(ml["hips"]) @ ml["spine"] @ basis["spine"][f]
        for name, p in (("hips", p_hips), ("spine", p_spine)):
            m_t = space.rows_to_matrices(baked[name][f])
            game = space.unity_to_blender(m_t @ np.diag([1 / s for s in game_scale[name]] + [1])) \
                @ np.linalg.inv(rest[name])
            np.testing.assert_allclose(p @ np.linalg.inv(ml[name]), game, atol=1e-9)


def test_unbaked_parent_follows_at_rest():
    ml = {"a": translate(0, 1, 0), "b": translate(0, 2, 0), "c": translate(0, 3, 0)}
    parents = {"a": None, "b": "a", "c": "b"}
    posed = {"c": np.stack([translate(1, 3, 0)] * 2)}
    basis = pose.basis_matrices(posed, parents, ml, 2)
    np.testing.assert_allclose(basis["c"][0], translate(1, 0, 0))


def test_fill_gaps():
    rows_ = np.array([[np.nan], [1.0], [np.nan], [3.0], [np.nan]])
    np.testing.assert_array_equal(pose.fill_gaps(rows_)[:, 0], [1, 1, 1, 3, 3])
