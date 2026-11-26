using System;
using System.Collections.Generic;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Writes each Studio item's look as a .glb, in the item's own space: its meshes as they are now
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

        /// <summary>The .glb bytes, or null when the item has no mesh to write.</summary>
        public byte[] Export(OCIItem item)
        {
            glb = new GlbBuilder();
            materialOf = new Dictionary<Material, int>();
            imageOf.Clear();
            var root = item.objectItem.transform;
            Matrix4x4 toItem = root.worldToLocalMatrix;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                try
                {
                    AddRenderer(r, toItem);
                }
                catch (Exception e)
                {
                    warnings.Add(item.objectItem.name + "/" + r.name + ": " + e.Message);
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

        private void AddMesh(Renderer r, Mesh mesh, Matrix4x4 place)
        {
            var v = mesh.vertices;
            if (v == null || v.Length == 0)
            {
                return;
            }
            var n = mesh.normals;
            var uv = mesh.uv;
            var spec = new GlbBuilder.MeshSpec { Name = r.name };
            // glTF space: Unity mirrored in X (as KKBP does); triangles reverse their winding.
            spec.Positions = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++)
            {
                spec.Positions[i * 3] = -v[i].x;
                spec.Positions[i * 3 + 1] = v[i].y;
                spec.Positions[i * 3 + 2] = v[i].z;
            }
            if (n != null && n.Length == v.Length)
            {
                spec.Normals = new float[n.Length * 3];
                for (int i = 0; i < n.Length; i++)
                {
                    spec.Normals[i * 3] = -n[i].x;
                    spec.Normals[i * 3 + 1] = n[i].y;
                    spec.Normals[i * 3 + 2] = n[i].z;
                }
            }
            if (uv != null && uv.Length == v.Length)
            {
                spec.Uvs = new float[uv.Length * 2];
                for (int i = 0; i < uv.Length; i++)
                {
                    spec.Uvs[i * 2] = uv[i].x;
                    spec.Uvs[i * 2 + 1] = 1f - uv[i].y;
                }
            }
            var mats = r.sharedMaterials;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int t = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = t;
                }
                var mat = s < mats.Length ? mats[s] : null;
                spec.Primitives.Add(new GlbBuilder.Primitive { Indices = tris, Material = Material(mat) });
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

        private int Material(Material m)
        {
            if (m == null)
            {
                return -1;
            }
            int index;
            if (materialOf.TryGetValue(m, out index))
            {
                return index;
            }
            var spec = new GlbBuilder.MaterialSpec { Name = m.name };
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
            materialOf[m] = index;
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
