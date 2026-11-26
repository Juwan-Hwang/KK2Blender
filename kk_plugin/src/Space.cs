using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Records Unity values as plain floats. Everything is written in Unity's own space (left-handed,
    /// +Y up, metres); the Blender add-on converts in one place (docs/bake-format.md).
    /// </summary>
    internal static class Space
    {
        /// <summary>The top three rows of a 4x4 matrix, row-major: 12 floats.</summary>
        public static void Matrix(Matrix4x4 m, float[] into, int at)
        {
            into[at + 0] = m.m00; into[at + 1] = m.m01; into[at + 2] = m.m02; into[at + 3] = m.m03;
            into[at + 4] = m.m10; into[at + 5] = m.m11; into[at + 6] = m.m12; into[at + 7] = m.m13;
            into[at + 8] = m.m20; into[at + 9] = m.m21; into[at + 10] = m.m22; into[at + 11] = m.m23;
        }

        public static float[] Matrix(Matrix4x4 m)
        {
            var r = new float[12];
            Matrix(m, r, 0);
            return r;
        }

        public static float[] Vector(Vector3 v)
        {
            return new[] { v.x, v.y, v.z };
        }

        public static float[] Color(Color c)
        {
            return new[] { c.r, c.g, c.b, c.a };
        }

        /// <summary>The path of <paramref name="t"/> below <paramref name="root"/>, names joined by '/'.</summary>
        public static string PathBelow(Transform t, Transform root)
        {
            if (t == root)
            {
                return "";
            }
            var parts = new System.Collections.Generic.List<string>();
            for (var p = t; p != null && p != root; p = p.parent)
            {
                parts.Add(p.name);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
