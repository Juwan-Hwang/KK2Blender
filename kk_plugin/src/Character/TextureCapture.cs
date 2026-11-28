using System;
using System.Collections.Generic;
using KK2Blender.Format.Bundle;
using UnityEngine;
using UnityEngine.Rendering;

namespace KK2Blender.Character
{
    /// <summary>
    /// Reads game textures into PNGs (any texture: compressed, unreadable or a render texture, through
    /// a blit), with normal maps unpacked from Unity's format, and caches them per texture so a texture
    /// used by several materials is written once.
    /// </summary>
    internal sealed class TextureCapture
    {
        private readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> normalCache = new Dictionary<int, byte[]>();

        public static bool Linear
        {
            get { return QualitySettings.activeColorSpace == ColorSpace.Linear; }
        }

        /// <summary>The texture as a PNG, or null if it can't be read.</summary>
        public byte[] Png(Texture tex, bool normalMap)
        {
            if (tex == null || tex.width <= 0 || tex.height <= 0)
            {
                return null;
            }
            var store = normalMap ? normalCache : cache;
            byte[] png;
            if (store.TryGetValue(tex.GetInstanceID(), out png))
            {
                return png;
            }
            Texture2D read = null;
            try
            {
                read = ReadBack(tex, tex.width, tex.height, normalMap);
                if (normalMap)
                {
                    var px = read.GetPixels32();
                    var bytes = ToBytes(px);
                    Pixels.UnpackNormals(bytes);
                    read.SetPixels32(ToColors(bytes));
                    read.Apply(false);
                }
                png = read.EncodeToPNG();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't read texture " + tex.name + ": " + e.Message);
                png = null;
            }
            finally
            {
                if (read != null)
                {
                    UnityEngine.Object.DestroyImmediate(read);
                }
            }
            store[tex.GetInstanceID()] = png;
            return png;
        }

        /// <summary>Draws the texture into a readable RGBA32 texture of the given size.</summary>
        public static Texture2D ReadBack(Texture tex, int width, int height, bool linearData)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                linearData ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(width, height, TextureFormat.RGBA32, false, linearData);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                read.Apply(false);
                return read;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public static byte[] ToBytes(Color32[] px)
        {
            var bytes = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                bytes[i * 4] = px[i].r;
                bytes[i * 4 + 1] = px[i].g;
                bytes[i * 4 + 2] = px[i].b;
                bytes[i * 4 + 3] = px[i].a;
            }
            return bytes;
        }

        public static Color32[] ToColors(byte[] bytes)
        {
            var px = new Color32[bytes.Length / 4];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
            }
            return px;
        }
    }

    /// <summary>
    /// Bakes a material's "light" and "dark" main texture Variants with the game's own shader: the
    /// material (a copy, with normal and alpha maps and specular cleared, outline and shadow passes off)
    /// is drawn on a quad covering UV space, and for materials with overlays placed by other UV sets
    /// (body, face) also the mesh flattened into UV space on top. One render is lit from the front
    /// (light), one from behind (dark). It renders with its own camera and light on a layer nothing else
    /// draws, and the scene's other lights are switched off only for the duration of each render call.
    /// </summary>
    internal sealed class LightBaker : IDisposable
    {
        public const int Layer = 31;

        private static readonly string[] ClearedTextures = { "_AlphaMask", "_NormalMap", "_NormalMapDetail", "_NormalMask" };
        private static readonly string[] ClearedFloats = { "_SpecularPower", "_SpecularPowerNail", "_nip_specular" };
        private static readonly string[] Overlays = { "_overtex1", "_overtex2", "_overtex3" };

        private readonly GameObject cameraObject;
        private readonly Camera camera;
        private readonly GameObject lightObject;
        private readonly Light light;
        private readonly Mesh quad;

        public LightBaker()
        {
            cameraObject = new GameObject("KK2Blender bake camera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 0.5f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0, 0, 0, 0);
            camera.cullingMask = 1 << Layer;
            camera.allowMSAA = false;
            camera.allowHDR = false;
            camera.rect = new Rect(0, 0, 1, 1);
            camera.renderingPath = RenderingPath.Forward;
            cameraObject.transform.position = new Vector3(0.5f, 0.5f, -1f);
            cameraObject.transform.rotation = Quaternion.identity;

            lightObject = new GameObject("KK2Blender bake light");
            lightObject.hideFlags = HideFlags.HideAndDontSave;
            light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1f;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << Layer;
            light.enabled = false;

            const float e = 0.001f;
            quad = new Mesh
            {
                vertices = new[] { new Vector3(-e, -e, 0), new Vector3(-e, 1 + e, 0), new Vector3(1 + e, 1 + e, 0), new Vector3(1 + e, -e, 0) },
                uv = new[] { new Vector2(-e, -e), new Vector2(-e, 1 + e), new Vector2(1 + e, 1 + e), new Vector2(1 + e, -e) },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            quad.RecalculateBounds();
            quad.hideFlags = HideFlags.HideAndDontSave;
        }

        public static bool HasOverlay(Material m)
        {
            foreach (var t in Overlays)
            {
                if (m.HasProperty(t) && m.GetTexture(t) != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Bakes one material slot; <paramref name="flat"/> is the mesh flattened into UV space, or null.</summary>
        /// <remarks>
        /// A <paramref name="blended"/> material is rendered twice, over black and over white, and the two
        /// are solved for straight colour and alpha (<see cref="Pixels.Unblend"/>): its shader blends into the
        /// target, which leaves premultiplied colour and a squared alpha in a single render.
        /// </remarks>
        public void Bake(Renderer renderer, Material source, int submesh, Mesh flat, int width, int height, bool blended, out byte[] lit, out byte[] dark)
        {
            var material = new Material(source);
            try
            {
                material.SetShaderPassEnabled("OUTLINE", false);
                material.SetShaderPassEnabled("SHADOWCASTER", false);
                foreach (var t in ClearedTextures)
                {
                    if (material.HasProperty(t))
                    {
                        material.SetTexture(t, null);
                    }
                }
                foreach (var f in ClearedFloats)
                {
                    if (material.HasProperty(f))
                    {
                        material.SetFloat(f, 0f);
                    }
                }
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                lit = Png(Quaternion.identity, material, block, flat, submesh, width, height, blended);
                dark = Png(Quaternion.Euler(0, 180, 0), material, block, flat, submesh, width, height, blended);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private byte[] Png(Quaternion lightRotation, Material material, MaterialPropertyBlock block, Mesh flat, int submesh, int width, int height, bool blended)
        {
            var rgba = Render(lightRotation, material, block, flat, submesh, width, height, new Color(0, 0, 0, 0));
            if (blended)
            {
                Pixels.Unblend(rgba, Render(lightRotation, material, block, flat, submesh, width, height, new Color(1, 1, 1, 1)));
            }
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(TextureCapture.ToColors(rgba));
                tex.Apply(false);
                return tex.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>One render of the material over <paramref name="background"/>, as bottom-up RGBA bytes.</summary>
        private byte[] Render(Quaternion lightRotation, Material material, MaterialPropertyBlock block, Mesh flat, int submesh, int width, int height, Color background)
        {
            lightObject.transform.rotation = lightRotation;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            rt.antiAliasing = 1;
            rt.filterMode = FilterMode.Point;
            var others = new List<Light>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l != light && l.enabled)
                {
                    others.Add(l);
                }
            }
            var previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                camera.targetTexture = rt;
                camera.aspect = 1f;
                camera.backgroundColor = background;
                Graphics.DrawMesh(quad, Matrix4x4.TRS(new Vector3(0, 0, 0.5f), Quaternion.identity, Vector3.one), material, Layer, camera, 0, block, ShadowCastingMode.Off, false);
                if (flat != null && submesh < flat.subMeshCount)
                {
                    Graphics.DrawMesh(flat, Matrix4x4.identity, material, Layer, camera, submesh, block, ShadowCastingMode.Off, false);
                }
                foreach (var l in others)
                {
                    l.enabled = false;
                }
                light.enabled = true;
                camera.Render();
                RenderTexture.active = rt;
                read = new Texture2D(width, height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                read.Apply(false);
                return TextureCapture.ToBytes(read.GetPixels32());
            }
            finally
            {
                light.enabled = false;
                foreach (var l in others)
                {
                    if (l != null)
                    {
                        l.enabled = true;
                    }
                }
                RenderTexture.active = previous;
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
                if (read != null)
                {
                    UnityEngine.Object.DestroyImmediate(read);
                }
            }
        }

        /// <summary>
        /// A copy of the mesh with each vertex at its first UV set's position (z = 0, facing the bake
        /// camera), keeping every UV set so overlays placed by the others land right. Null without UVs.
        /// </summary>
        public static Mesh Flatten(Mesh source)
        {
            if (source == null || !source.isReadable)
            {
                return null;
            }
            var uv = source.uv;
            if (uv == null || uv.Length != source.vertexCount)
            {
                return null;
            }
            var flat = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            if (uv.Length > 65000)
            {
                return null;
            }
            var vertices = new Vector3[uv.Length];
            var normals = new Vector3[uv.Length];
            var tangents = new Vector4[uv.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                vertices[i] = new Vector3(uv[i].x, uv[i].y, 0f);
                normals[i] = Vector3.back;
                tangents[i] = new Vector4(1, 0, 0, -1);
            }
            flat.vertices = vertices;
            flat.normals = normals;
            flat.tangents = tangents;
            flat.uv = uv;
            if (source.uv2 != null && source.uv2.Length == uv.Length)
            {
                flat.uv2 = source.uv2;
            }
            if (source.uv3 != null && source.uv3.Length == uv.Length)
            {
                flat.uv3 = source.uv3;
            }
            if (source.uv4 != null && source.uv4.Length == uv.Length)
            {
                flat.uv4 = source.uv4;
            }
            if (source.colors32 != null && source.colors32.Length == uv.Length)
            {
                flat.colors32 = source.colors32;
            }
            flat.subMeshCount = source.subMeshCount;
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var tris = source.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 a = vertices[tris[t]], b = vertices[tris[t + 1]], c = vertices[tris[t + 2]];
                    // Seen from -Z, clockwise is front-facing: keep each triangle facing the camera.
                    if (Vector3.Cross(b - a, c - a).z > 0)
                    {
                        int swap = tris[t + 1];
                        tris[t + 1] = tris[t + 2];
                        tris[t + 2] = swap;
                    }
                }
                flat.SetTriangles(tris, s);
            }
            flat.RecalculateBounds();
            return flat;
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(quad);
        }
    }
}
