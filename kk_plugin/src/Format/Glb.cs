using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KK2Blender.Format
{
    /// <summary>
    /// A small glTF 2.0 binary (.glb) writer: static triangle meshes as nodes, PBR materials with an
    /// optional base colour PNG. Unity-free; the caller has already converted to glTF's space.
    /// </summary>
    public sealed class GlbBuilder
    {
        public sealed class MaterialSpec
        {
            public string Name;
            public float[] BaseColor = { 1f, 1f, 1f, 1f };
            public int Texture = -1;
            public string AlphaMode = "OPAQUE"; // OPAQUE, MASK, BLEND
            public float AlphaCutoff = 0.5f;
            public bool DoubleSided;
            public float[] Emissive;
        }

        public sealed class Primitive
        {
            public int[] Indices;
            public int Material = -1;
        }

        public sealed class MeshSpec
        {
            public string Name;
            public float[] Positions; // xyz per vertex
            public float[] Normals; // optional
            public float[] Uvs; // optional, uv per vertex (glTF: origin top left)
            public float[] Colors; // optional rgba per vertex
            public List<Primitive> Primitives = new List<Primitive>();
        }

        private readonly List<byte[]> images = new List<byte[]>();
        private readonly List<MaterialSpec> materials = new List<MaterialSpec>();
        private readonly List<KeyValuePair<MeshSpec, float[]>> nodes = new List<KeyValuePair<MeshSpec, float[]>>();
        private readonly MemoryStream bin = new MemoryStream();
        private readonly List<object> bufferViews = new List<object>();
        private readonly List<object> accessors = new List<object>();

        public int AddImage(byte[] png)
        {
            images.Add(png);
            return images.Count - 1;
        }

        public int AddMaterial(MaterialSpec m)
        {
            materials.Add(m);
            return materials.Count - 1;
        }

        /// <summary>A node with a mesh; matrix is a column-major 4x4 (glTF's order) or null.</summary>
        public void AddNode(MeshSpec mesh, float[] matrix)
        {
            nodes.Add(new KeyValuePair<MeshSpec, float[]>(mesh, matrix));
        }

        public int NodeCount { get { return nodes.Count; } }

        private int View(byte[] data, int? target)
        {
            while (bin.Length % 4 != 0)
            {
                bin.WriteByte(0);
            }
            long offset = bin.Length;
            bin.Write(data, 0, data.Length);
            var v = new JsonObject().Set("buffer", 0).Set("byteOffset", offset).Set("byteLength", data.Length);
            if (target.HasValue)
            {
                v.Set("target", target.Value);
            }
            bufferViews.Add(v);
            return bufferViews.Count - 1;
        }

        private int FloatAccessor(float[] values, int components, string type, bool bounds)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            int view = View(bytes, 34962);
            var a = new JsonObject()
                .Set("bufferView", view)
                .Set("componentType", 5126)
                .Set("count", values.Length / components)
                .Set("type", type);
            if (bounds)
            {
                var min = new float[components];
                var max = new float[components];
                for (int c = 0; c < components; c++)
                {
                    min[c] = float.MaxValue;
                    max[c] = float.MinValue;
                }
                for (int i = 0; i < values.Length; i++)
                {
                    int c = i % components;
                    min[c] = Math.Min(min[c], values[i]);
                    max[c] = Math.Max(max[c], values[i]);
                }
                a.Set("min", min).Set("max", max);
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        private int IndexAccessor(int[] indices)
        {
            var bytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            int view = View(bytes, 34963);
            accessors.Add(new JsonObject()
                .Set("bufferView", view)
                .Set("componentType", 5125)
                .Set("count", indices.Length)
                .Set("type", "SCALAR"));
            return accessors.Count - 1;
        }

        public byte[] Build()
        {
            var meshes = new List<object>();
            var nodeList = new List<object>();
            for (int n = 0; n < nodes.Count; n++)
            {
                var mesh = nodes[n].Key;
                var attributes = new JsonObject().Set("POSITION", FloatAccessor(mesh.Positions, 3, "VEC3", true));
                if (mesh.Normals != null)
                {
                    attributes.Set("NORMAL", FloatAccessor(mesh.Normals, 3, "VEC3", false));
                }
                if (mesh.Uvs != null)
                {
                    attributes.Set("TEXCOORD_0", FloatAccessor(mesh.Uvs, 2, "VEC2", false));
                }
                if (mesh.Colors != null)
                {
                    attributes.Set("COLOR_0", FloatAccessor(mesh.Colors, 4, "VEC4", false));
                }
                var prims = new List<object>();
                foreach (var p in mesh.Primitives)
                {
                    if (p.Indices == null || p.Indices.Length == 0)
                    {
                        continue;
                    }
                    var prim = new JsonObject().Set("attributes", attributes).Set("indices", IndexAccessor(p.Indices));
                    if (p.Material >= 0)
                    {
                        prim.Set("material", p.Material);
                    }
                    prims.Add(prim);
                }
                if (prims.Count == 0)
                {
                    continue;
                }
                meshes.Add(new JsonObject().Set("name", mesh.Name ?? "mesh").Set("primitives", prims));
                var node = new JsonObject().Set("name", mesh.Name ?? "node").Set("mesh", meshes.Count - 1);
                if (nodes[n].Value != null)
                {
                    node.Set("matrix", nodes[n].Value);
                }
                nodeList.Add(node);
            }

            var imageList = new List<object>();
            var textures = new List<object>();
            for (int i = 0; i < images.Count; i++)
            {
                int view = View(images[i], null);
                imageList.Add(new JsonObject().Set("bufferView", view).Set("mimeType", "image/png"));
                textures.Add(new JsonObject().Set("source", i));
            }
            var mats = new List<object>();
            foreach (var m in materials)
            {
                var pbr = new JsonObject().Set("baseColorFactor", m.BaseColor).Set("metallicFactor", 0f).Set("roughnessFactor", 0.8f);
                if (m.Texture >= 0)
                {
                    pbr.Set("baseColorTexture", new JsonObject().Set("index", m.Texture));
                }
                var mat = new JsonObject().Set("name", m.Name ?? "material").Set("pbrMetallicRoughness", pbr).Set("alphaMode", m.AlphaMode);
                if (m.AlphaMode == "MASK")
                {
                    mat.Set("alphaCutoff", m.AlphaCutoff);
                }
                if (m.DoubleSided)
                {
                    mat.Set("doubleSided", true);
                }
                if (m.Emissive != null)
                {
                    mat.Set("emissiveFactor", m.Emissive);
                }
                mats.Add(mat);
            }

            var roots = new List<object>();
            for (int i = 0; i < nodeList.Count; i++)
            {
                roots.Add(i);
            }
            var gltf = new JsonObject()
                .Set("asset", new JsonObject().Set("version", "2.0").Set("generator", "KK2Blender"))
                .Set("scene", 0)
                .Set("scenes", new object[] { new JsonObject().Set("nodes", roots) })
                .Set("nodes", nodeList)
                .Set("meshes", meshes);
            if (mats.Count > 0)
            {
                gltf.Set("materials", mats);
            }
            if (imageList.Count > 0)
            {
                gltf.Set("images", imageList).Set("textures", textures);
            }
            while (bin.Length % 4 != 0)
            {
                bin.WriteByte(0);
            }
            gltf.Set("buffers", new object[] { new JsonObject().Set("byteLength", bin.Length) });
            gltf.Set("bufferViews", bufferViews);
            gltf.Set("accessors", accessors);

            byte[] json = Encoding.UTF8.GetBytes(JsonWriter.Write(gltf, false));
            int jsonPad = (4 - json.Length % 4) % 4;
            byte[] binary = bin.ToArray();
            var outStream = new MemoryStream();
            var w = new BinaryWriter(outStream);
            int total = 12 + 8 + json.Length + jsonPad + 8 + binary.Length;
            w.Write(0x46546C67u); // "glTF"
            w.Write(2u);
            w.Write((uint)total);
            w.Write((uint)(json.Length + jsonPad));
            w.Write(0x4E4F534Au); // "JSON"
            w.Write(json);
            for (int i = 0; i < jsonPad; i++)
            {
                w.Write((byte)0x20);
            }
            w.Write((uint)binary.Length);
            w.Write(0x004E4942u); // "BIN\0"
            w.Write(binary);
            w.Flush();
            return outStream.ToArray();
        }
    }
}
