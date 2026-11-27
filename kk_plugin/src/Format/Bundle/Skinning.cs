using System;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Moves a game mesh's stored vertices into the rest pose the <see cref="RestSolver"/> found, the
    /// way Unity's skinning would put them there: positions and shape-key offsets by each vertex's
    /// blended skin matrix, normals by its inverse transpose, tangents by the matrix (handedness kept).
    /// </summary>
    public sealed class Skinning
    {
        private readonly RestSolver.Skin skin;
        private readonly Affine[] perVertex; // null when every vertex uses K
        private readonly Affine[] normalMatrix; // inverse transposes, same indexing (or one entry)
        private readonly double[] handedness;

        public Skinning(RestSolver.Skin skin, int vertexCount)
        {
            this.skin = skin;
            if (skin.Exact || skin.Rigid)
            {
                var m = skin.Rigid ? skin.RestSkin[0] : skin.K;
                normalMatrix = new[] { NormalMatrix(m) };
                handedness = new[] { m.Determinant < 0 ? -1.0 : 1.0 };
                return;
            }
            if (skin.Joints.Length < vertexCount * 4 || skin.Weights.Length < vertexCount * 4)
            {
                throw new ArgumentException("skin has " + skin.Joints.Length / 4 + " weighted vertices, mesh has " + vertexCount);
            }
            perVertex = new Affine[vertexCount];
            normalMatrix = new Affine[vertexCount];
            handedness = new double[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                var sum = new Affine();
                double total = 0;
                for (int k = 0; k < 4; k++)
                {
                    int j = skin.Joints[v * 4 + k];
                    double w = skin.Weights[v * 4 + k];
                    if (w > 0 && j >= 0 && j < skin.RestSkin.Length)
                    {
                        sum = sum + skin.RestSkin[j].Scaled(w);
                        total += w;
                    }
                }
                var m = total > 0 ? sum.Scaled(1.0 / total) : skin.K;
                perVertex[v] = m;
                normalMatrix[v] = NormalMatrix(m);
                handedness[v] = m.Determinant < 0 ? -1.0 : 1.0;
            }
        }

        private Affine MatrixOf(int v)
        {
            if (perVertex != null)
            {
                return v < perVertex.Length ? perVertex[v] : skin.K;
            }
            return skin.Rigid ? skin.RestSkin[0] : skin.K;
        }

        private Affine NormalOf(int v)
        {
            return perVertex != null && v < normalMatrix.Length ? normalMatrix[v] : normalMatrix[0];
        }

        private static Affine NormalMatrix(Affine m)
        {
            Affine inv;
            try
            {
                inv = m.Inverse();
            }
            catch (InvalidOperationException)
            {
                return Affine.Identity;
            }
            // Transpose of the linear part; no translation.
            return new Affine
            {
                M00 = inv.M00, M01 = inv.M10, M02 = inv.M20,
                M10 = inv.M01, M11 = inv.M11, M12 = inv.M21,
                M20 = inv.M02, M21 = inv.M12, M22 = inv.M22,
            };
        }

        public float[] Positions(float[] p)
        {
            var r = new float[p.Length];
            for (int v = 0; v < p.Length / 3; v++)
            {
                double x, y, z;
                MatrixOf(v).Point(p[v * 3], p[v * 3 + 1], p[v * 3 + 2], out x, out y, out z);
                r[v * 3] = (float)x;
                r[v * 3 + 1] = (float)y;
                r[v * 3 + 2] = (float)z;
            }
            return r;
        }

        /// <summary>Offsets (shape keys): the linear part only.</summary>
        public float[] Offsets(float[] d)
        {
            var r = new float[d.Length];
            for (int v = 0; v < d.Length / 3; v++)
            {
                double x, y, z;
                MatrixOf(v).Vector(d[v * 3], d[v * 3 + 1], d[v * 3 + 2], out x, out y, out z);
                r[v * 3] = (float)x;
                r[v * 3 + 1] = (float)y;
                r[v * 3 + 2] = (float)z;
            }
            return r;
        }

        /// <summary>Normals at unit length; offsets of normals (shape keys) when <paramref name="offsets"/>.</summary>
        public float[] Normals(float[] n, bool offsets = false)
        {
            var r = new float[n.Length];
            for (int v = 0; v < n.Length / 3; v++)
            {
                double x, y, z;
                NormalOf(v).Vector(n[v * 3], n[v * 3 + 1], n[v * 3 + 2], out x, out y, out z);
                if (!offsets)
                {
                    double len = Math.Sqrt(x * x + y * y + z * z);
                    if (len > 1e-12)
                    {
                        x /= len; y /= len; z /= len;
                    }
                }
                r[v * 3] = (float)x;
                r[v * 3 + 1] = (float)y;
                r[v * 3 + 2] = (float)z;
            }
            return r;
        }

        public float[] Tangents(float[] t)
        {
            var r = new float[t.Length];
            for (int v = 0; v < t.Length / 4; v++)
            {
                double x, y, z;
                MatrixOf(v).Vector(t[v * 4], t[v * 4 + 1], t[v * 4 + 2], out x, out y, out z);
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (len > 1e-12)
                {
                    x /= len; y /= len; z /= len;
                }
                double h = perVertex != null && v < handedness.Length ? handedness[v] : handedness[0];
                r[v * 4] = (float)x;
                r[v * 4 + 1] = (float)y;
                r[v * 4 + 2] = (float)z;
                r[v * 4 + 3] = (float)(t[v * 4 + 3] * h);
            }
            return r;
        }
    }
}
