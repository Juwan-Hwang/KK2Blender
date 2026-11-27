using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Finds the pose a character is captured in: the meshes' bind pose.
    /// <para>
    /// Unity skins a vertex as <c>v = Σ w_j · Bone_j · Bind_j · v_mesh</c>. If every bone of a mesh is
    /// put at <c>C_j = K · Bind_j⁻¹</c> for one matrix K, each skin matrix is K and the mesh rests at
    /// <c>K · v_mesh</c>, with no blending error. Bones and meshes captured that way can be posed by any
    /// later bone matrices <c>M_j</c> exactly as the game does (<c>M_j · C_j⁻¹ · v_rest</c>), which is
    /// what the Blender side relies on, and nothing in the scene has to be moved to capture it.
    /// </para>
    /// <para>
    /// Meshes are taken in order (body first). A mesh's K comes from its most weighted bone that an
    /// earlier mesh already placed; for a mesh with none, from its renderer's current place relative to
    /// the nearest placed ancestor. Its bones not yet placed then get <c>K · Bind_j⁻¹</c>. A mesh whose
    /// bones were placed by another mesh with a different bind pose can't be exact; it is skinned with
    /// the blended matrices (as a T-pose capture would be) and its largest error is reported. Bones that
    /// no mesh weights follow their parent with their current local transform.
    /// </para>
    /// All matrices are in the character root's space, Unity axes.
    /// </summary>
    public sealed class RestSolver
    {
        public sealed class Bone
        {
            public string Key;
            public string ParentKey;
            /// <summary>The bone's current matrix in the character root's space.</summary>
            public Affine Current = Affine.Identity;
            /// <summary>The bone's current local transform (relative to its parent).</summary>
            public Affine Local = Affine.Identity;
        }

        public sealed class Skin
        {
            public string Key;
            /// <summary>Bone keys of the renderer's bones array (null for a missing bone).</summary>
            public string[] BoneKeys;
            public Affine[] Bindposes;
            public int[] Joints; // 4 per vertex, into BoneKeys
            public float[] Weights; // 4 per vertex
            /// <summary>The bone key of the renderer's own transform.</summary>
            public string RendererKey;
            /// <summary>The renderer transform's current matrix in the character root's space.</summary>
            public Affine Renderer = Affine.Identity;
            /// <summary>Not skinned: drawn rigidly by its own transform.</summary>
            public bool Rigid;

            // Results.
            public Affine K = Affine.Identity;
            /// <summary>Rest skin matrix C_j · Bind_j per entry of BoneKeys (rigid: one entry, the renderer's rest).</summary>
            public Affine[] RestSkin;
            public bool Exact = true;
            /// <summary>The largest distance between a joint's rest skin matrix and K (metres for a 1 m part).</summary>
            public double Mismatch;
        }

        public const double Tolerance = 1e-4;

        private readonly Dictionary<string, Bone> bones = new Dictionary<string, Bone>();
        private readonly Dictionary<string, Affine> rests = new Dictionary<string, Affine>();

        public RestSolver(IEnumerable<Bone> allBones)
        {
            foreach (var b in allBones)
            {
                bones[b.Key] = b;
            }
        }

        public IDictionary<string, Affine> Rests
        {
            get { return rests; }
        }

        /// <summary>Places the bones of every skin (in order), then every bone no skin placed.</summary>
        public void Solve(IList<Skin> skins)
        {
            foreach (var s in skins)
            {
                if (!s.Rigid)
                {
                    Place(s);
                }
            }
            foreach (var key in bones.Keys)
            {
                RestOf(key);
            }
            foreach (var s in skins)
            {
                if (s.Rigid)
                {
                    s.K = RestOf(s.RendererKey);
                    s.RestSkin = new[] { s.K };
                    s.Exact = true;
                }
                else
                {
                    Measure(s);
                }
            }
        }

        private void Place(Skin s)
        {
            int n = s.BoneKeys.Length;
            var weightSum = new double[n];
            for (int i = 0; i < s.Joints.Length; i++)
            {
                int j = s.Joints[i];
                if (j >= 0 && j < n && s.Weights[i] > 0)
                {
                    weightSum[j] += s.Weights[i];
                }
            }
            int best = -1;
            for (int j = 0; j < n; j++)
            {
                if (weightSum[j] > 0 && s.BoneKeys[j] != null && rests.ContainsKey(s.BoneKeys[j]) && (best < 0 || weightSum[j] > weightSum[best]))
                {
                    best = j;
                }
            }
            if (best >= 0)
            {
                s.K = rests[s.BoneKeys[best]] * s.Bindposes[best];
            }
            else
            {
                s.K = Anchored(s.RendererKey, s.Renderer);
            }
            for (int j = 0; j < n; j++)
            {
                string key = s.BoneKeys[j];
                if (weightSum[j] > 0 && key != null && bones.ContainsKey(key) && !rests.ContainsKey(key))
                {
                    rests[key] = s.K * s.Bindposes[j].Inverse();
                }
            }
        }

        /// <summary>A transform's current matrix carried to the rest pose of its nearest placed ancestor.</summary>
        private Affine Anchored(string key, Affine current)
        {
            Bone b;
            string k = key != null && bones.TryGetValue(key, out b) ? b.ParentKey : null;
            while (k != null)
            {
                Affine rest;
                if (rests.TryGetValue(k, out rest))
                {
                    return rest * bones[k].Current.Inverse() * current;
                }
                k = bones.TryGetValue(k, out b) ? b.ParentKey : null;
            }
            return current;
        }

        /// <summary>The rest matrix of a bone: placed by a skin, or its parent's rest times its local transform.</summary>
        public Affine RestOf(string key)
        {
            Affine rest;
            if (key == null)
            {
                return Affine.Identity;
            }
            if (rests.TryGetValue(key, out rest))
            {
                return rest;
            }
            Bone b;
            if (!bones.TryGetValue(key, out b))
            {
                return Affine.Identity;
            }
            if (b.ParentKey == null || !bones.ContainsKey(b.ParentKey))
            {
                rest = b.Current;
            }
            else
            {
                rest = RestOf(b.ParentKey) * b.Local;
            }
            rests[key] = rest;
            return rest;
        }

        private void Measure(Skin s)
        {
            int n = s.BoneKeys.Length;
            s.RestSkin = new Affine[n];
            var used = new bool[n];
            for (int i = 0; i < s.Joints.Length; i++)
            {
                int j = s.Joints[i];
                if (j >= 0 && j < n && s.Weights[i] > 0)
                {
                    used[j] = true;
                }
            }
            s.Mismatch = 0;
            for (int j = 0; j < n; j++)
            {
                if (s.BoneKeys[j] == null)
                {
                    s.RestSkin[j] = s.K;
                    continue;
                }
                s.RestSkin[j] = RestOf(s.BoneKeys[j]) * s.Bindposes[j];
                if (used[j])
                {
                    s.Mismatch = Math.Max(s.Mismatch, Affine.Distance(s.RestSkin[j], s.K, 1.0));
                }
            }
            s.Exact = s.Mismatch <= Tolerance;
        }
    }
}
