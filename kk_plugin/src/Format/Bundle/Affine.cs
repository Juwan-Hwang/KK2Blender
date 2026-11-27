using System;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// An affine transform as the top three rows of a 4x4 matrix, row-major, in double precision:
    /// <c>p' = M p + t</c> with M = (m00..m22) and t = (m03, m13, m23). Unity-free, so the bundle maths
    /// (rest poses, skinning, the change to glTF's axes) is tested off the game.
    /// </summary>
    public struct Affine
    {
        public double M00, M01, M02, M03;
        public double M10, M11, M12, M13;
        public double M20, M21, M22, M23;

        public static readonly Affine Identity = new Affine { M00 = 1, M11 = 1, M22 = 1 };

        /// <summary>From 12 floats: three rows of four.</summary>
        public static Affine FromRows(float[] r, int at = 0)
        {
            return new Affine
            {
                M00 = r[at], M01 = r[at + 1], M02 = r[at + 2], M03 = r[at + 3],
                M10 = r[at + 4], M11 = r[at + 5], M12 = r[at + 6], M13 = r[at + 7],
                M20 = r[at + 8], M21 = r[at + 9], M22 = r[at + 10], M23 = r[at + 11],
            };
        }

        public double[] ToRows()
        {
            return new[] { M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23 };
        }

        /// <summary>Translation, rotation (quaternion x, y, z, w) and scale along the rotated axes: T R S.</summary>
        public static Affine FromTrs(double[] t, double[] q, double[] s)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n > 0)
            {
                x /= n; y /= n; z /= n; w /= n;
            }
            double sx = s != null ? s[0] : 1, sy = s != null ? s[1] : 1, sz = s != null ? s[2] : 1;
            var a = new Affine
            {
                M00 = (1 - 2 * (y * y + z * z)) * sx,
                M01 = (2 * (x * y - z * w)) * sy,
                M02 = (2 * (x * z + y * w)) * sz,
                M10 = (2 * (x * y + z * w)) * sx,
                M11 = (1 - 2 * (x * x + z * z)) * sy,
                M12 = (2 * (y * z - x * w)) * sz,
                M20 = (2 * (x * z - y * w)) * sx,
                M21 = (2 * (y * z + x * w)) * sy,
                M22 = (1 - 2 * (x * x + y * y)) * sz,
            };
            if (t != null)
            {
                a.M03 = t[0];
                a.M13 = t[1];
                a.M23 = t[2];
            }
            return a;
        }

        public static Affine operator *(Affine a, Affine b)
        {
            return new Affine
            {
                M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
                M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
                M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
                M03 = a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03,
                M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
                M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
                M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
                M13 = a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13,
                M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
                M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
                M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
                M23 = a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23,
            };
        }

        public double Determinant
        {
            get
            {
                return M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);
            }
        }

        public Affine Inverse()
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-300)
            {
                throw new InvalidOperationException("The matrix can't be inverted.");
            }
            double inv = 1.0 / det;
            var r = new Affine
            {
                M00 = (M11 * M22 - M12 * M21) * inv,
                M01 = (M02 * M21 - M01 * M22) * inv,
                M02 = (M01 * M12 - M02 * M11) * inv,
                M10 = (M12 * M20 - M10 * M22) * inv,
                M11 = (M00 * M22 - M02 * M20) * inv,
                M12 = (M02 * M10 - M00 * M12) * inv,
                M20 = (M10 * M21 - M11 * M20) * inv,
                M21 = (M01 * M20 - M00 * M21) * inv,
                M22 = (M00 * M11 - M01 * M10) * inv,
            };
            r.M03 = -(r.M00 * M03 + r.M01 * M13 + r.M02 * M23);
            r.M13 = -(r.M10 * M03 + r.M11 * M13 + r.M12 * M23);
            r.M23 = -(r.M20 * M03 + r.M21 * M13 + r.M22 * M23);
            return r;
        }

        public void Point(double x, double y, double z, out double rx, out double ry, out double rz)
        {
            rx = M00 * x + M01 * y + M02 * z + M03;
            ry = M10 * x + M11 * y + M12 * z + M13;
            rz = M20 * x + M21 * y + M22 * z + M23;
        }

        public void Vector(double x, double y, double z, out double rx, out double ry, out double rz)
        {
            rx = M00 * x + M01 * y + M02 * z;
            ry = M10 * x + M11 * y + M12 * z;
            rz = M20 * x + M21 * y + M22 * z;
        }

        /// <summary>Multiplies every entry by <paramref name="k"/>; for summing weighted matrices.</summary>
        public Affine Scaled(double k)
        {
            return new Affine
            {
                M00 = M00 * k, M01 = M01 * k, M02 = M02 * k, M03 = M03 * k,
                M10 = M10 * k, M11 = M11 * k, M12 = M12 * k, M13 = M13 * k,
                M20 = M20 * k, M21 = M21 * k, M22 = M22 * k, M23 = M23 * k,
            };
        }

        public static Affine operator +(Affine a, Affine b)
        {
            return new Affine
            {
                M00 = a.M00 + b.M00, M01 = a.M01 + b.M01, M02 = a.M02 + b.M02, M03 = a.M03 + b.M03,
                M10 = a.M10 + b.M10, M11 = a.M11 + b.M11, M12 = a.M12 + b.M12, M13 = a.M13 + b.M13,
                M20 = a.M20 + b.M20, M21 = a.M21 + b.M21, M22 = a.M22 + b.M22, M23 = a.M23 + b.M23,
            };
        }

        /// <summary>The largest absolute difference between two matrices' entries, the translation
        /// weighted by <paramref name="length"/>⁻¹ so that both read in metres for a part of that size.</summary>
        public static double Distance(Affine a, Affine b, double length)
        {
            double d = 0;
            double[] x = a.ToRows(), y = b.ToRows();
            for (int i = 0; i < 12; i++)
            {
                double k = (i % 4 == 3) ? 1.0 : length;
                d = Math.Max(d, Math.Abs(x[i] - y[i]) * k);
            }
            return d;
        }

        /// <summary>
        /// Splits the matrix into translation, rotation and the scale along the rotated axes (T R S),
        /// taking the column lengths as the scale. A shear (a parent scaled unevenly under a rotated
        /// child) has no exact T R S; the rotation is then the nearest one Gram-Schmidt gives.
        /// </summary>
        public void Decompose(out double[] t, out double[] q, out double[] s)
        {
            t = new[] { M03, M13, M23 };
            double sx = Math.Sqrt(M00 * M00 + M10 * M10 + M20 * M20);
            double sy = Math.Sqrt(M01 * M01 + M11 * M11 + M21 * M21);
            double sz = Math.Sqrt(M02 * M02 + M12 * M12 + M22 * M22);
            if (Determinant < 0)
            {
                // A mirroring transform can't be a rotation; fold the mirror into X.
                sx = -sx;
            }
            s = new[] { sx, sy, sz };
            // Orthonormal axes (Gram-Schmidt from X, then Y).
            double[] x = Normalize(M00 / Nz(sx), M10 / Nz(sx), M20 / Nz(sx));
            double[] y = { M01 / Nz(sy), M11 / Nz(sy), M21 / Nz(sy) };
            double d = x[0] * y[0] + x[1] * y[1] + x[2] * y[2];
            y = Normalize(y[0] - d * x[0], y[1] - d * x[1], y[2] - d * x[2]);
            double[] z = { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
            q = Quaternion(x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2]);
        }

        /// <summary>The rotation and translation of <see cref="Decompose"/>, without the scale.</summary>
        public Affine Rigid()
        {
            double[] t, q, s;
            Decompose(out t, out q, out s);
            return FromTrs(t, q, null);
        }

        private static double Nz(double v)
        {
            return Math.Abs(v) < 1e-12 ? 1e-12 : v;
        }

        private static double[] Normalize(double x, double y, double z)
        {
            double n = Math.Sqrt(x * x + y * y + z * z);
            if (n < 1e-12)
            {
                return new[] { 1.0, 0, 0 };
            }
            return new[] { x / n, y / n, z / n };
        }

        /// <summary>Quaternion (x, y, z, w) of a rotation matrix given row by row.</summary>
        public static double[] Quaternion(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            double trace = m00 + m11 + m22;
            double x, y, z, w;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2;
                w = 0.25 * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                w = (m21 - m12) / s;
                x = 0.25 * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25 * s;
                z = (m12 + m21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25 * s;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (w < 0)
            {
                n = -n; // keep w positive, so equal rotations write equal numbers
            }
            return new[] { x / n, y / n, z / n, w / n };
        }

        /// <summary>
        /// The same transform in glTF's axes: Unity is left-handed and glTF right-handed, both +Y up, and
        /// the bundle converts by negating X (export-bundle.md). For a matrix that is C M C, C = diag(-1, 1, 1).
        /// </summary>
        public Affine MirrorX()
        {
            return new Affine
            {
                M00 = M00, M01 = -M01, M02 = -M02, M03 = -M03,
                M10 = -M10, M11 = M11, M12 = M12, M13 = M13,
                M20 = -M20, M21 = M21, M22 = M22, M23 = M23,
            };
        }
    }
}
