using System;
using System.Collections.Generic;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Writes meshes as .glb: each Studio item's look, in the item's own space: its meshes as they are now
    /// (skinned ones baked at the current pose) and simple materials, the main texture tinted by the
    /// item's colours through its colour mask. The Blender add-on parents it under the item's animated
    /// empty. Not a shader-accurate copy; it gives props their real shape and colours.
    /// </summary>
    internal sealed class PropExporter
    {
        private readonly Dictionary<int, int> imageOf = new Dictionary<int, int>();
        private readonly List<string> warnings = new List<string>();
        private GlbBuilder glb;
        private Dictionary<Material, int> materialOf;

        public List<string> Warnings { get { return warnings; } }

        private bool withLightmaps;
        private readonly Dictionary<int, int> lightmapImageOf = new Dictionary<int, int>();
        private readonly Dictionary<Mesh, Bounds[]> batchBounds = new Dictionary<Mesh, Bounds[]>();
        private readonly HashSet<KeyValuePair<Mesh, int>> batchTaken = new HashSet<KeyValuePair<Mesh, int>>();

        /// <summary>The .glb bytes, or null when the item has no mesh to write.</summary>
        public byte[] Export(OCIItem item)
        {
            return Export(item.objectItem.transform, false, true);
        }

        /// <summary>
        /// Every renderer below <paramref name="root"/>, in root's space. With lightmaps, baked lightmaps
        /// come along on a second UV set (maps). Inactive objects are left out unless asked for.
        /// </summary>
        public byte[] Export(Transform root, bool lightmaps, bool includeInactive)
        {
            glb = new GlbBuilder();
            materialOf = new Dictionary<Material, int>();
            imageOf.Clear();
            lightmapImageOf.Clear();
            withLightmaps = lightmaps;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive))
            {
                if (!includeInactive && !r.enabled)
                {
                    continue;
                }
                try
                {
                    AddRenderer(r, toRoot);
                }
                catch (Exception e)
                {
                    warnings.Add(root.name + "/" + r.name + ": " + e.Message);
                }
            }
            return glb.NodeCount > 0 ? glb.Build() : null;
        }

        private void AddRenderer(Renderer r, Matrix4x4 toItem)
        {
            if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
            {
                return;
            }
            Mesh mesh = null;
            bool baked = false;
            Matrix4x4 place;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                if (smr.sharedMesh == null)
                {
                    return;
                }
                mesh = new Mesh();
                smr.BakeMesh(mesh);
                baked = true;
                // BakeMesh gives vertices in the renderer's space without its scale.
                var t = smr.transform;
                place = toItem * Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                {
                    return;
                }
                mesh = mf.sharedMesh;
                place = toItem * r.transform.localToWorldMatrix;
                if (r.isPartOfStaticBatch)
                {
                    AddStaticBatched(r, mesh, toItem);
                    return;
                }
                if (!mesh.isReadable)
                {
                    mesh = BakeUnreadable(mesh);
                    baked = true;
                    if (mesh == null)
                    {
                        warnings.Add(r.name + ": its mesh isn't readable");
                        return;
                    }
                }
            }
            try
            {
                AddMesh(r, mesh, place);
            }
            finally
            {
                if (baked)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
            }
        }

        /// <summary>
        /// Unity can still skin an unreadable mesh into a new, readable one: give it to a throwaway
        /// SkinnedMeshRenderer with no bones and bake.
        /// </summary>
        private static Mesh BakeUnreadable(Mesh source)
        {
            var go = new GameObject("KK2Blender bake");
            try
            {
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = source;
                var mesh = new Mesh();
                smr.BakeMesh(mesh);
                return mesh.vertexCount == source.vertexCount ? mesh : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// A statically batched renderer shares one combined mesh, in world space, with its batch.
        /// Unity doesn't say which sub-meshes are this renderer's, so they are found by their bounds:
        /// the sub-meshes, in order, that lie inside the renderer's world bounds, one per material.
        /// </summary>
        private void AddStaticBatched(Renderer r, Mesh combined, Matrix4x4 toRoot)
        {
            if (!combined.isReadable)
            {
                warnings.Add(r.name + ": its static batch isn't readable");
                return;
            }
            Bounds[] bounds;
            if (!batchBounds.TryGetValue(combined, out bounds))
            {
                var verts = combined.vertices;
                bounds = new Bounds[combined.subMeshCount];
                for (int s = 0; s < bounds.Length; s++)
                {
                    var idx = combined.GetIndices(s);
                    if (idx.Length == 0)
                    {
                        continue;
                    }
                    var b = new Bounds(verts[idx[0]], Vector3.zero);
                    foreach (int i in idx)
                    {
                        b.Encapsulate(verts[i]);
                    }
                    bounds[s] = b;
                }
                batchBounds[combined] = bounds;
            }
            var mine = new List<int>();
            var rb = r.bounds;
            rb.Expand(0.002f);
            int want = r.sharedMaterials.Length;
            for (int s = 0; s < bounds.Length && mine.Count < want; s++)
            {
                var key = new KeyValuePair<Mesh, int>(combined, s);
                if (batchTaken.Contains(key) || bounds[s].size == Vector3.zero && bounds[s].center == Vector3.zero)
                {
                    continue;
                }
                if (rb.Contains(bounds[s].min) && rb.Contains(bounds[s].max))
                {
                    mine.Add(s);
                }
            }
            if (mine.Count == 0)
            {
                warnings.Add(r.name + ": its part of the static batch wasn't found");
                return;
            }
            foreach (int s in mine)
            {
                batchTaken.Add(new KeyValuePair<Mesh, int>(combined, s));
            }
            AddMesh(r, combined, toRoot, mine);
        }

        private void AddMesh(Renderer r, Mesh mesh, Matrix4x4 place)
        {
            AddMesh(r, mesh, place, null);
        }

        private void AddMesh(Renderer r, Mesh mesh, Matrix4x4 place, List<int> onlySubMeshes)
        {
            var v = mesh.vertices;
            if (v == null || v.Length == 0)
            {
                return;
            }
            var n = mesh.normals;
            var uv = mesh.uv;
            Vector2[] uv2 = null;
            Vector4 so = new Vector4(1f, 1f, 0f, 0f);
            if (withLightmaps && r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)
            {
                uv2 = mesh.uv2;
                if (uv2 == null || uv2.Length != v.Length)
                {
                    uv2 = uv;
                }
                // A statically batched mesh already has its lightmap scale and offset applied.
                if (!r.isPartOfStaticBatch)
                {
                    so = r.lightmapScaleOffset;
                }
            }

            // The triangles to write, and (for a shared combined mesh) only the vertices they use.
            int subCount = onlySubMeshes != null ? onlySubMeshes.Count : mesh.subMeshCount;
            var triangles = new List<int[]>();
            for (int k = 0; k < subCount; k++)
            {
                triangles.Add(mesh.GetTriangles(onlySubMeshes != null ? onlySubMeshes[k] : k));
            }
            int[] remap = null;
            int count = v.Length;
            if (onlySubMeshes != null)
            {
                remap = new int[v.Length];
                for (int i = 0; i < remap.Length; i++)
                {
                    remap[i] = -1;
                }
                count = 0;
                foreach (var tris in triangles)
                {
                    foreach (int i in tris)
                    {
                        if (remap[i] < 0)
                        {
                            remap[i] = count++;
                        }
                    }
                }
            }

            var spec = new GlbBuilder.MeshSpec { Name = r.name };
            // glTF space: Unity mirrored in X (as KKBP does); triangles reverse their winding.
            spec.Positions = new float[count * 3];
            bool normals = n != null && n.Length == v.Length;
            bool uvs = uv != null && uv.Length == v.Length;
            bool uvs2 = uv2 != null && uv2.Length == v.Length;
            if (normals)
            {
                spec.Normals = new float[count * 3];
            }
            if (uvs)
            {
                spec.Uvs = new float[count * 2];
            }
            if (uvs2)
            {
                spec.Uvs2 = new float[count * 2];
            }
            for (int i = 0; i < v.Length; i++)
            {
                int o = remap != null ? remap[i] : i;
                if (o < 0)
                {
                    continue;
                }
                spec.Positions[o * 3] = -v[i].x;
                spec.Positions[o * 3 + 1] = v[i].y;
                spec.Positions[o * 3 + 2] = v[i].z;
                if (normals)
                {
                    spec.Normals[o * 3] = -n[i].x;
                    spec.Normals[o * 3 + 1] = n[i].y;
                    spec.Normals[o * 3 + 2] = n[i].z;
                }
                if (uvs)
                {
                    spec.Uvs[o * 2] = uv[i].x;
                    spec.Uvs[o * 2 + 1] = 1f - uv[i].y;
                }
                if (uvs2)
                {
                    spec.Uvs2[o * 2] = uv2[i].x * so.x + so.z;
                    spec.Uvs2[o * 2 + 1] = 1f - (uv2[i].y * so.y + so.w);
                }
            }

            var mats = r.sharedMaterials;
            int lightmap = uvs2 ? LightmapImage(r.lightmapIndex) : -1;
            for (int k = 0; k < triangles.Count; k++)
            {
                var src = triangles[k];
                var tris = new int[src.Length - src.Length % 3];
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int a = src[i], b = src[i + 1], c = src[i + 2];
                    if (remap != null)
                    {
                        a = remap[a];
                        b = remap[b];
                        c = remap[c];
                    }
                    tris[i] = a;
                    tris[i + 1] = c;
                    tris[i + 2] = b;
                }
                var mat = k < mats.Length ? mats[k] : null;
                spec.Primitives.Add(new GlbBuilder.Primitive { Indices = tris, Material = Material(mat, lightmap) });
            }
            glb.AddNode(spec, ColumnMajorGltf(place));
        }

        /// <summary>A Unity matrix as glTF's: mirrored in X on both sides, column-major.</summary>
        private static float[] ColumnMajorGltf(Matrix4x4 m)
        {
            var s = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            var g = s * m * s;
            var r = new float[16];
            for (int c = 0; c < 4; c++)
            {
                for (int row = 0; row < 4; row++)
                {
                    r[c * 4 + row] = g[row, c];
                }
            }
            return r;
        }

        /// <summary>A lightmap as a PNG holding Unity's encoding as is (RGBM: colour × alpha × 5).</summary>
        private int LightmapImage(int index)
        {
            int image;
            if (lightmapImageOf.TryGetValue(index, out image))
            {
                return image;
            }
            image = -1;
            var maps = LightmapSettings.lightmaps;
            if (maps != null && index < maps.Length && maps[index] != null && maps[index].lightmapColor != null)
            {
                var tex = ReadableRaw(maps[index].lightmapColor);
                image = glb.AddImage(tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
            lightmapImageOf[index] = image;
            return image;
        }

        private int Material(Material m)
        {
            return Material(m, -1);
        }

        private int Material(Material m, int lightmap)
        {
            if (m == null)
            {
                return -1;
            }
            int index;
            // The same material with and without a lightmap are two glTF materials.
            var cacheKey = lightmap >= 0 ? null : m;
            if (cacheKey != null && materialOf.TryGetValue(cacheKey, out index))
            {
                return index;
            }
            var spec = new GlbBuilder.MaterialSpec { Name = m.name, Lightmap = lightmap };
            string shader = m.shader != null ? m.shader.name.ToLowerInvariant() : "";
            if (shader.Contains("alpha") || shader.Contains("transparent") || m.renderQueue >= 3000)
            {
                spec.AlphaMode = "BLEND";
            }
            else if (m.HasProperty("_Cutoff") || shader.Contains("cutout"))
            {
                spec.AlphaMode = "MASK";
                spec.AlphaCutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
            }
            spec.DoubleSided = shader.Contains("cull") || shader.Contains("2side") || shader.Contains("doubleside");

            Texture main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            Texture mask = m.HasProperty("_ColorMask") ? m.GetTexture("_ColorMask") : null;
            Color c1 = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            if (main != null)
            {
                spec.Texture = Image(main, mask, m);
                if (mask == null)
                {
                    spec.BaseColor = new[] { c1.r, c1.g, c1.b, c1.a };
                }
            }
            else
            {
                spec.BaseColor = new[] { c1.r, c1.g, c1.b, c1.a };
            }
            if (m.HasProperty("_EmissionColor"))
            {
                var e = m.GetColor("_EmissionColor");
                float power = m.HasProperty("_EmissionPower") ? m.GetFloat("_EmissionPower") : 1f;
                if (e.maxColorComponent > 0.001f && power > 0f)
                {
                    spec.Emissive = new[] { Mathf.Clamp01(e.r * power), Mathf.Clamp01(e.g * power), Mathf.Clamp01(e.b * power) };
                }
            }
            index = glb.AddMaterial(spec);
            if (cacheKey != null)
            {
                materialOf[cacheKey] = index;
            }
            return index;
        }

        /// <summary>
        /// The main texture as a PNG; with a colour mask, tinted the way KK's item shaders colour it:
        /// the mask's red, green and blue choose how much of colours 1, 2 and 3 multiply the texture.
        /// </summary>
        private int Image(Texture main, Texture mask, Material m)
        {
            int key = main.GetInstanceID() * 31 + (mask != null ? mask.GetInstanceID() : 0) * 17 + m.GetInstanceID();
            int index;
            if (imageOf.TryGetValue(key, out index))
            {
                return index;
            }
            var readable = Readable(main, 0, 0);
            if (mask != null)
            {
                var maskTex = Readable(mask, readable.width, readable.height);
                var px = readable.GetPixels32();
                var mk = maskTex.GetPixels32();
                Color c1 = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                Color c2 = m.HasProperty("_Color2") ? m.GetColor("_Color2") : Color.white;
                Color c3 = m.HasProperty("_Color3") ? m.GetColor("_Color3") : Color.white;
                for (int i = 0; i < px.Length; i++)
                {
                    Color tint = Color.white;
                    tint = Color.Lerp(tint, c1, mk[i].r / 255f);
                    tint = Color.Lerp(tint, c2, mk[i].g / 255f);
                    tint = Color.Lerp(tint, c3, mk[i].b / 255f);
                    Color p = px[i];
                    px[i] = new Color(p.r * tint.r, p.g * tint.g, p.b * tint.b, p.a * c1.a);
                }
                readable.SetPixels32(px);
                readable.Apply();
                UnityEngine.Object.Destroy(maskTex);
            }
            index = glb.AddImage(readable.EncodeToPNG());
            UnityEngine.Object.Destroy(readable);
            imageOf[key] = index;
            return index;
        }

        /// <summary>A texture's stored values, unconverted (linear render texture), for encoded data such as lightmaps.</summary>
        private static Texture2D ReadableRaw(Texture source)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Any texture as a readable RGBA32 copy (through a render texture), optionally resized.</summary>
        private static Texture2D Readable(Texture source, int width, int height)
        {
            int w = width > 0 ? width : source.width;
            int h = height > 0 ? height : source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
