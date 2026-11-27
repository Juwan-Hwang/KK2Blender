using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Writes one Pass's .glb as the bundle contract wants it: the bones as the node tree (rest pose,
    /// no scale), every mesh skinned to one skin with all of the Pass's bones and placed at the scene root
    /// with no transform, morph targets named through <c>extras.targetNames</c>, placeholder materials,
    /// <c>extras.id</c> equal to the name on every node, mesh and material, no textures. Everything handed
    /// in is already in glTF's space.
    /// </summary>
    internal sealed class PassGlb
    {
        public sealed class Node
        {
            public string Id;
            public int Parent = -1;
            public double[] Translation; // xyz
            public double[] Rotation; // xyzw
            public Affine World; // unscaled rest, for the inverse bind matrix
        }

        public sealed class Primitive
        {
            public int[] Indices;
            public int Material;
        }

        public sealed class Morph
        {
            public string Name;
            public float[] Positions;
            public float[] Normals;
        }

        public sealed class Mesh
        {
            public string Id;
            public float[] Positions;
            public float[] Normals;
            public float[] Tangents;
            public List<float[]> Uvs = new List<float[]>();
            public float[] Colors;
            /// <summary>Optional: each vertex's index in the game mesh, as the custom attribute _KK_VERTEX.</summary>
            public float[] SourceIndex;
            /// <summary>Optional: the mesh's number in the bundle, as _KK_MESH (meshes KKBP joins stay apart).</summary>
            public int Ordinal = -1;
            public int[] Joints; // 4 per vertex, skin joint indices
            public float[] Weights; // 4 per vertex
            public List<Primitive> Primitives = new List<Primitive>();
            public List<Morph> Morphs = new List<Morph>();
        }

        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<string> Materials = new List<string>();

        private MemoryStream bin;
        private List<object> views;
        private List<object> accessors;

        public byte[] Build()
        {
            bin = new MemoryStream();
            views = new List<object>();
            accessors = new List<object>();

            var nodes = new List<object>();
            var children = new List<List<int>>();
            for (int i = 0; i < Nodes.Count; i++)
            {
                children.Add(new List<int>());
            }
            var roots = new List<object>();
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Parent >= 0)
                {
                    children[Nodes[i].Parent].Add(i);
                }
                else
                {
                    roots.Add(i);
                }
            }
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                var o = new JsonObject().Set("name", n.Id);
                if (children[i].Count > 0)
                {
                    o.Set("children", children[i]);
                }
                o.Set("translation", n.Translation).Set("rotation", n.Rotation).Set("extras", new JsonObject().Set("id", n.Id));
                nodes.Add(o);
            }

            var gltfMeshes = new List<object>();
            if (Meshes.Count > 0)
            {
                var joints = new List<object>();
                var ibm = new float[Nodes.Count * 16];
                for (int i = 0; i < Nodes.Count; i++)
                {
                    joints.Add(i);
                    var inv = Nodes[i].World.Inverse();
                    // Column-major 4x4.
                    double[] r = inv.ToRows();
                    for (int row = 0; row < 3; row++)
                    {
                        for (int col = 0; col < 4; col++)
                        {
                            ibm[i * 16 + col * 4 + row] = (float)r[row * 4 + col];
                        }
                    }
                    ibm[i * 16 + 15] = 1f;
                }
                int ibmAccessor = Floats(ibm, 16, "MAT4", false, null);
                for (int m = 0; m < Meshes.Count; m++)
                {
                    gltfMeshes.Add(WriteMesh(Meshes[m]));
                    var node = new JsonObject().Set("name", Meshes[m].Id).Set("mesh", m).Set("skin", 0)
                        .Set("extras", new JsonObject().Set("id", Meshes[m].Id));
                    roots.Add(nodes.Count);
                    nodes.Add(node);
                }
                skins = new List<object> { new JsonObject().Set("inverseBindMatrices", ibmAccessor).Set("joints", joints) };
            }

            var mats = new List<object>();
            foreach (var id in Materials)
            {
                mats.Add(new JsonObject().Set("name", id).Set("extras", new JsonObject().Set("id", id)));
            }

            var gltf = new JsonObject()
                .Set("asset", new JsonObject().Set("version", "2.0").Set("generator", "KK2Blender"))
                .Set("scene", 0)
                .Set("scenes", new object[] { new JsonObject().Set("nodes", roots) })
                .Set("nodes", nodes);
            if (gltfMeshes.Count > 0)
            {
                gltf.Set("meshes", gltfMeshes).Set("skins", skins);
            }
            if (mats.Count > 0)
            {
                gltf.Set("materials", mats);
            }
            if (bin.Length > 0)
            {
                Pad(bin, 0);
                gltf.Set("buffers", new object[] { new JsonObject().Set("byteLength", bin.Length) });
                gltf.Set("bufferViews", views);
                gltf.Set("accessors", accessors);
            }
            return Pack(Encoding.UTF8.GetBytes(JsonWriter.Write(gltf, false)), bin.ToArray());
        }

        private List<object> skins;

        private JsonObject WriteMesh(Mesh mesh)
        {
            int n = mesh.Positions.Length / 3;
            var attributes = new JsonObject().Set("POSITION", Floats(mesh.Positions, 3, "VEC3", true, 34962));
            if (mesh.Normals != null)
            {
                attributes.Set("NORMAL", Floats(mesh.Normals, 3, "VEC3", false, 34962));
            }
            if (mesh.Tangents != null)
            {
                attributes.Set("TANGENT", Floats(mesh.Tangents, 4, "VEC4", false, 34962));
            }
            for (int u = 0; u < mesh.Uvs.Count; u++)
            {
                attributes.Set("TEXCOORD_" + u, Floats(mesh.Uvs[u], 2, "VEC2", false, 34962));
            }
            if (mesh.Colors != null)
            {
                attributes.Set("COLOR_0", Floats(mesh.Colors, 4, "VEC4", false, 34962));
            }
            if (mesh.SourceIndex != null)
            {
                attributes.Set("_KK_VERTEX", Floats(mesh.SourceIndex, 1, "SCALAR", false, 34962));
            }
            if (mesh.Ordinal >= 0)
            {
                var ordinal = new float[n];
                for (int i = 0; i < n; i++)
                {
                    ordinal[i] = mesh.Ordinal;
                }
                attributes.Set("_KK_MESH", Floats(ordinal, 1, "SCALAR", false, 34962));
            }
            int maxJoint = 0;
            foreach (int j in mesh.Joints)
            {
                maxJoint = Math.Max(maxJoint, j);
            }
            attributes.Set("JOINTS_0", Joints(mesh.Joints, maxJoint > 255));
            attributes.Set("WEIGHTS_0", Floats(mesh.Weights, 4, "VEC4", false, 34962));

            var targets = new List<object>();
            var names = new List<object>();
            foreach (var morph in mesh.Morphs)
            {
                var t = new JsonObject().Set("POSITION", Floats(morph.Positions, 3, "VEC3", true, 34962));
                if (morph.Normals != null && mesh.Normals != null)
                {
                    t.Set("NORMAL", Floats(morph.Normals, 3, "VEC3", false, 34962));
                }
                targets.Add(t);
                names.Add(morph.Name);
            }

            var prims = new List<object>();
            foreach (var p in mesh.Primitives)
            {
                int max = 0;
                foreach (int i in p.Indices)
                {
                    max = Math.Max(max, i);
                }
                var prim = new JsonObject().Set("attributes", attributes).Set("indices", Indices(p.Indices, max)).Set("material", p.Material).Set("mode", 4);
                if (targets.Count > 0)
                {
                    prim.Set("targets", targets);
                }
                prims.Add(prim);
            }
            var extras = new JsonObject().Set("id", mesh.Id);
            if (names.Count > 0)
            {
                extras.Set("targetNames", names);
            }
            return new JsonObject().Set("name", mesh.Id).Set("primitives", prims).Set("extras", extras);
        }

        private static void Pad(MemoryStream s, byte with)
        {
            while (s.Length % 4 != 0)
            {
                s.WriteByte(with);
            }
        }

        private int View(byte[] data, int? target, int? stride)
        {
            Pad(bin, 0);
            long offset = bin.Length;
            bin.Write(data, 0, data.Length);
            var v = new JsonObject().Set("buffer", 0).Set("byteOffset", offset).Set("byteLength", data.Length);
            if (stride.HasValue)
            {
                v.Set("byteStride", stride.Value);
            }
            if (target.HasValue)
            {
                v.Set("target", target.Value);
            }
            views.Add(v);
            return views.Count - 1;
        }

        private int Floats(float[] values, int components, string type, bool bounds, int? target)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            int view = View(bytes, target, target.HasValue && components > 1 && type != "MAT4" ? components * 4 : (int?)null);
            var a = new JsonObject().Set("bufferView", view).Set("componentType", 5126).Set("count", values.Length / components).Set("type", type);
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
                if (values.Length == 0)
                {
                    for (int c = 0; c < components; c++)
                    {
                        min[c] = max[c] = 0;
                    }
                }
                a.Set("min", min).Set("max", max);
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        private int Joints(int[] joints, bool wide)
        {
            byte[] bytes;
            if (wide)
            {
                bytes = new byte[joints.Length * 2];
                for (int i = 0; i < joints.Length; i++)
                {
                    bytes[i * 2] = (byte)(joints[i] & 0xff);
                    bytes[i * 2 + 1] = (byte)(joints[i] >> 8);
                }
            }
            else
            {
                bytes = new byte[joints.Length];
                for (int i = 0; i < joints.Length; i++)
                {
                    bytes[i] = (byte)joints[i];
                }
            }
            int view = View(bytes, 34962, wide ? 8 : 4);
            accessors.Add(new JsonObject().Set("bufferView", view).Set("componentType", wide ? 5123 : 5121).Set("count", joints.Length / 4).Set("type", "VEC4"));
            return accessors.Count - 1;
        }

        private int Indices(int[] indices, int max)
        {
            byte[] bytes;
            int type;
            if (max < 65535)
            {
                type = 5123;
                bytes = new byte[indices.Length * 2];
                for (int i = 0; i < indices.Length; i++)
                {
                    bytes[i * 2] = (byte)(indices[i] & 0xff);
                    bytes[i * 2 + 1] = (byte)(indices[i] >> 8);
                }
            }
            else
            {
                type = 5125;
                bytes = new byte[indices.Length * 4];
                Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            }
            int view = View(bytes, 34963, null);
            accessors.Add(new JsonObject().Set("bufferView", view).Set("componentType", type).Set("count", indices.Length).Set("type", "SCALAR"));
            return accessors.Count - 1;
        }

        private static byte[] Pack(byte[] json, byte[] binary)
        {
            int jsonPad = (4 - json.Length % 4) % 4;
            var outStream = new MemoryStream();
            var w = new BinaryWriter(outStream);
            int total = 12 + 8 + json.Length + jsonPad + (binary.Length > 0 ? 8 + binary.Length : 0);
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
            if (binary.Length > 0)
            {
                w.Write((uint)binary.Length);
                w.Write(0x004E4942u); // "BIN\0"
                w.Write(binary);
            }
            w.Flush();
            return outStream.ToArray();
        }
    }
}
