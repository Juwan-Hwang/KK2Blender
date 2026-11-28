using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Writes a <see cref="CharacterBundle"/> as a KKBP Export Bundle (schema 1.3) into
    /// <c>&lt;parent&gt;/&lt;yyyyMMddHHmmss&gt;_&lt;name&gt;/</c>: textures, one .glb and data file per Pass,
    /// skeleton, materials, expressions, physics, character, the card, our map file
    /// (<c>kk2blender.json</c>: bundle Ids to the game's transform paths), and the Manifest last.
    /// This is the one place where Unity's axes become glTF's (X negated).
    /// Contract rules the game data could break (a unique role twice, a repeated texture slot or
    /// target) are fixed here with a warning rather than written.
    /// </summary>
    public sealed class BundleWriter
    {
        public readonly List<string> Warnings = new List<string>();

        private readonly IdTable boneIds = new IdTable("bone");
        private readonly IdTable meshIds = new IdTable("mesh");
        private readonly IdTable materialIds = new IdTable("mat");
        private readonly Dictionary<string, int> ordinals = new Dictionary<string, int>();
        private readonly Dictionary<object, string> textureFiles = new Dictionary<object, string>();
        private string root;
        private CharacterBundle bundle;
        private Dictionary<string, BundleBone> bonesByKey;
        private Dictionary<string, Affine> rigidWorld; // glTF space, unscaled

        public static string FolderName(DateTime localTime, string characterName)
        {
            var sb = new StringBuilder();
            foreach (char c in characterName ?? "")
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == '/' || c == '\\' || c == ':' || c < 0x20 ? '_' : c);
            }
            string name = sb.ToString().Trim().TrimEnd('.');
            if (name.Length == 0)
            {
                name = "character";
            }
            return localTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + name;
        }

        /// <summary>Writes the bundle into <paramref name="folder"/> (created; must not hold another bundle).</summary>
        public void Write(CharacterBundle b, string folder)
        {
            bundle = b;
            root = folder;
            Directory.CreateDirectory(root);
            if (File.Exists(Path.Combine(root, "manifest.json")))
            {
                File.Delete(Path.Combine(root, "manifest.json"));
            }

            Prepare();
            MakeIds();
            var materialsJson = WriteMaterials();
            var map = new JsonObject().Set("format", "kk2blender-bundle-map").Set("version", 1);
            var mapMeshes = new JsonObject();
            var passEntries = new List<object>();
            foreach (var pass in bundle.Passes)
            {
                string dir = pass.Kind == "body" ? "" : "outfits/" + pass.Outfit.Value.ToString("00", CultureInfo.InvariantCulture) + "/";
                string glbName = pass.Kind == "body" ? "body.glb" : dir + "outfit.glb";
                string dataName = pass.Kind == "body" ? "body.json" : dir + "outfit.json";
                Directory.CreateDirectory(Path.Combine(root, dir.Length > 0 ? dir : "."));
                File.WriteAllBytes(Path.Combine(root, glbName), BuildGlb(pass));
                WriteJson(dataName, PassJson(pass, mapMeshes));
                passEntries.Add(new JsonObject().Set("kind", pass.Kind).Set("outfit", pass.Outfit).Set("mesh", glbName).Set("data", dataName));
            }
            WriteJson("materials.json", materialsJson);
            WriteJson("skeleton.json", SkeletonJson(map));
            WriteJson("expressions.json", ExpressionsJson());
            WriteJson("physics.json", PhysicsJson());
            WriteJson("character.json", new JsonObject().Set("name", bundle.Name));
            bool card = bundle.Card != null && bundle.Card.Length > 0;
            if (card)
            {
                File.WriteAllBytes(Path.Combine(root, "card.png"), bundle.Card);
            }
            map.Set("meshes", mapMeshes);
            var mapMaterials = new JsonObject();
            foreach (var m in bundle.Materials)
            {
                mapMaterials.Set(materialIds.Get(m.Key), new JsonObject().Set("path", m.Path).Set("slot", m.Slot).Set("name", m.Name));
            }
            map.Set("materials", mapMaterials);
            foreach (DictionaryEntryPair kv in Pairs(bundle.MapExtras))
            {
                map.Set(kv.Key, kv.Value);
            }
            map.Set("warnings", Warnings);
            WriteJson("kk2blender.json", map);

            var manifest = new JsonObject()
                .Set("game", bundle.Game)
                .Set("schema_version", CharacterBundle.SchemaVersion)
                .Set("exporter", new JsonObject().Set("name", bundle.ExporterName).Set("version", bundle.ExporterVersion))
                .Set("exported_at", bundle.ExportedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                .Set("character_name", bundle.Name)
                .Set("card", card ? "card.png" : null)
                .Set("options", new JsonObject()
                    .Set("all_outfits", bundle.AllOutfits)
                    .Set("all_variations", bundle.AllVariations)
                    .Set("physics", bundle.Physics)
                    .Set("enabled_shape_keys", bundle.EnabledShapeKeys)
                    .Set("current_pose", bundle.CurrentPose))
                .Set("passes", passEntries);
            WriteJson("manifest.json", manifest);
        }

        private struct DictionaryEntryPair
        {
            public string Key;
            public object Value;
        }

        private static IEnumerable<DictionaryEntryPair> Pairs(JsonObject o)
        {
            foreach (string k in o.Keys)
            {
                yield return new DictionaryEntryPair { Key = k, Value = o[k] };
            }
        }

        private void WriteJson(string relative, object value)
        {
            File.WriteAllText(Path.Combine(root, relative), JsonWriter.Write(value), new UTF8Encoding(false));
        }

        // --- checks and Ids ------------------------------------------------------------------

        private static readonly HashSet<string> UniqueMaterialRoles = new HashSet<string>
        {
            "body", "face", "eyebrow", "eyelash", "eyelid", "eyeline_upper", "eyeline_lower", "eyeline_shadow", "nose_line", "eye_l", "eye_r",
        };

        private static readonly HashSet<string> RepeatableTextureRoles = new HashSet<string> { "overlay" };

        /// <summary>Moves the mesh whose first material has the face role to the front, keeping the others' order.</summary>
        public static void FaceFirst(List<BundleMesh> meshes, Func<string, string> roleOfMaterial)
        {
            int at = meshes.FindIndex(m => m.Primitives.Count > 0 && roleOfMaterial(m.Primitives[0].MaterialKey) == "face");
            if (at > 0)
            {
                var face = meshes[at];
                meshes.RemoveAt(at);
                meshes.Insert(0, face);
            }
        }

        private void Prepare()
        {
            bonesByKey = new Dictionary<string, BundleBone>();
            var roles = new HashSet<string>();
            foreach (var bone in bundle.Bones)
            {
                if (bonesByKey.ContainsKey(bone.Key))
                {
                    throw new InvalidOperationException("Bone key " + bone.Key + " appears twice");
                }
                bonesByKey[bone.Key] = bone;
                if (bone.Role != null && !roles.Add(bone.Role))
                {
                    Warnings.Add("Bone Role " + bone.Role + " was on two bones; " + bone.Path + " lost it");
                    bone.Role = null;
                }
            }
            foreach (var bone in bundle.Bones)
            {
                if (bone.ParentKey != null && !bonesByKey.ContainsKey(bone.ParentKey))
                {
                    throw new InvalidOperationException("Bone " + bone.Key + " has unknown parent " + bone.ParentKey);
                }
            }

            var matRoles = new HashSet<string>();
            var materialKeys = new HashSet<string>();
            foreach (var m in bundle.Materials)
            {
                if (!materialKeys.Add(m.Key))
                {
                    throw new InvalidOperationException("Material key " + m.Key + " appears twice");
                }
                if (m.Role != null && UniqueMaterialRoles.Contains(m.Role) && !matRoles.Add(m.Role))
                {
                    Warnings.Add("Material Role " + m.Role + " was on two materials; " + m.Key + " lost it");
                    m.Role = null;
                }
                DropRepeatedTextures(m.Textures, "material " + m.Key);
            }

            // KKBP orders Body's material slots by the Body Pass's mesh order, and its outline needs the
            // face material first: the mesh with the face material leads the Body Pass.
            var roleOf = new Dictionary<string, string>();
            foreach (var m in bundle.Materials)
            {
                roleOf[m.Key] = m.Role;
            }
            foreach (var pass in bundle.Passes)
            {
                if (pass.Kind == "body")
                {
                    FaceFirst(pass.Meshes, key => { string r; return key != null && roleOf.TryGetValue(key, out r) ? r : null; });
                }
            }

            // Morph target names are unique per mesh.
            foreach (var pass in bundle.Passes)
            {
                foreach (var mesh in pass.Meshes)
                {
                    var seen = new HashSet<string>();
                    mesh.Morphs.RemoveAll(morph =>
                    {
                        if (seen.Add(morph.Name))
                        {
                            return false;
                        }
                        Warnings.Add("Mesh " + mesh.Path + " had two shape keys named " + morph.Name + "; the second was left out");
                        return true;
                    });
                }
            }
        }

        private void DropRepeatedTextures(List<BundleTexture> textures, string what)
        {
            var slots = new HashSet<string>();
            var roleSlots = new HashSet<string>();
            textures.RemoveAll(t =>
            {
                string slot = t.Raw + "|" + t.Variant + "|" + t.Pattern;
                string roleSlot = t.Role + "|" + t.Variant + "|" + t.Pattern;
                bool roleClash = t.Role != null && !RepeatableTextureRoles.Contains(t.Role) && roleSlots.Contains(roleSlot);
                if (!slots.Add(slot) || roleClash)
                {
                    Warnings.Add(what + " had texture slot " + slot + " twice; one was left out");
                    return true;
                }
                if (t.Role != null)
                {
                    roleSlots.Add(roleSlot);
                }
                return false;
            });
        }

        private void MakeIds()
        {
            foreach (var bone in bundle.Bones)
            {
                boneIds.Make(bone.Key, bone.Name);
            }
            foreach (var m in bundle.Materials)
            {
                materialIds.Make(m.Key, m.Name);
            }
            foreach (var pass in bundle.Passes)
            {
                foreach (var mesh in pass.Meshes)
                {
                    if (meshIds.TryGet(mesh.Key, out _))
                    {
                        throw new InvalidOperationException("Mesh key " + mesh.Key + " appears twice");
                    }
                    meshIds.Make(mesh.Key, mesh.Name);
                    ordinals[mesh.Key] = ordinals.Count;
                }
            }

            rigidWorld = new Dictionary<string, Affine>();
            foreach (var bone in bundle.Bones)
            {
                rigidWorld[bone.Key] = bone.Rest.Rigid().MirrorX();
            }
        }

        // --- textures and materials ------------------------------------------------------------

        private string TextureFile(BundleTexture t)
        {
            object key = (object)t.Source ?? t.Png;
            string file;
            if (textureFiles.TryGetValue(key, out file))
            {
                return file;
            }
            if (t.Png == null)
            {
                throw new InvalidOperationException("Texture " + t.Raw + " has no image");
            }
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            file = "textures/" + (textureFiles.Count + 1).ToString("0000", CultureInfo.InvariantCulture) + ".png";
            File.WriteAllBytes(Path.Combine(root, file), t.Png);
            textureFiles[key] = file;
            return file;
        }

        private JsonObject TextureJson(BundleTexture t)
        {
            var o = new JsonObject();
            if (t.Role != null)
            {
                o.Set("role", t.Role);
            }
            o.Set("raw", t.Raw);
            if (t.Variant != null)
            {
                o.Set("variant", t.Variant);
            }
            if (t.Pattern != null)
            {
                o.Set("pattern", t.Pattern);
            }
            return o.Set("file", TextureFile(t)).Set("offset", t.Offset).Set("scale", t.Scale);
        }

        private static JsonObject Map<T>(List<KeyValuePair<string, T>> list)
        {
            var o = new JsonObject();
            foreach (var kv in list)
            {
                o.Set(kv.Key, kv.Value);
            }
            return o;
        }

        private JsonObject WriteMaterials()
        {
            var list = new List<object>();
            foreach (var m in bundle.Materials)
            {
                var o = new JsonObject().Set("id", materialIds.Get(m.Key)).Set("name", m.Name ?? "").Set("shader", m.Shader ?? "");
                if (m.Role != null)
                {
                    o.Set("role", m.Role);
                }
                var textures = new List<object>();
                foreach (var t in m.Textures)
                {
                    textures.Add(TextureJson(t));
                }
                o.Set("blend_mode", m.BlendMode).Set("colors", Map(m.Colors)).Set("floats", Map(m.Floats)).Set("vectors", Map(m.Vectors)).Set("textures", textures);
                list.Add(o);
            }
            return new JsonObject().Set("materials", list);
        }

        // --- passes ----------------------------------------------------------------------------

        private List<BundleBone> PassBones(BundlePass pass)
        {
            var list = new List<BundleBone>();
            foreach (var bone in bundle.Bones)
            {
                if (bone.Outfit == null || (pass.Kind == "outfit" && bone.Outfit == pass.Outfit))
                {
                    list.Add(bone);
                }
            }
            return list;
        }

        private byte[] BuildGlb(BundlePass pass)
        {
            var glb = new PassGlb();
            var bones = PassBones(pass);
            var index = new Dictionary<string, int>();
            for (int i = 0; i < bones.Count; i++)
            {
                index[bones[i].Key] = i;
            }
            foreach (var bone in bones)
            {
                if (bone.ParentKey != null && !index.ContainsKey(bone.ParentKey))
                {
                    throw new InvalidOperationException("Bone " + bone.Key + " of Outfit " + bone.Outfit + " has a parent from another Outfit");
                }
                var world = rigidWorld[bone.Key];
                var local = bone.ParentKey == null ? world : rigidWorld[bone.ParentKey].Inverse() * world;
                double[] t, q, s;
                local.Decompose(out t, out q, out s);
                glb.Nodes.Add(new PassGlb.Node
                {
                    Id = boneIds.Get(bone.Key),
                    Parent = bone.ParentKey == null ? -1 : index[bone.ParentKey],
                    Translation = t,
                    Rotation = q,
                    World = world,
                });
            }

            var materialIndex = new Dictionary<string, int>();
            foreach (var mesh in pass.Meshes)
            {
                var gm = new PassGlb.Mesh { Id = meshIds.Get(mesh.Key) };
                int n = mesh.VertexCount;
                gm.Positions = MirrorVectors(mesh.Positions, 3);
                gm.Normals = mesh.Normals != null ? UnitNormals(MirrorVectors(mesh.Normals, 3)) : null;
                if (mesh.Tangents != null && gm.Normals != null)
                {
                    gm.Tangents = UnitTangents(MirrorTangents(mesh.Tangents), gm.Normals);
                }
                foreach (var uv in mesh.Uvs)
                {
                    var flipped = new float[uv.Length];
                    for (int i = 0; i < n; i++)
                    {
                        flipped[i * 2] = Finite(uv[i * 2]);
                        flipped[i * 2 + 1] = 1f - Finite(uv[i * 2 + 1]);
                    }
                    gm.Uvs.Add(flipped);
                }
                gm.Colors = mesh.Colors;
                var source = SourceVertices(mesh);
                gm.Ordinal = ordinals[mesh.Key];
                gm.SourceIndex = new float[n];
                for (int i = 0; i < n; i++)
                {
                    gm.SourceIndex[i] = source[i];
                }
                Skin(mesh, index, gm);
                foreach (var p in mesh.Primitives)
                {
                    if (p.Indices == null || p.Indices.Length < 3)
                    {
                        continue;
                    }
                    int mat;
                    if (!materialIndex.TryGetValue(p.MaterialKey, out mat))
                    {
                        mat = glb.Materials.Count;
                        glb.Materials.Add(materialIds.Get(p.MaterialKey));
                        materialIndex[p.MaterialKey] = mat;
                    }
                    // Mirroring flips the winding.
                    var idx = new int[p.Indices.Length - p.Indices.Length % 3];
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        idx[i] = p.Indices[i];
                        idx[i + 1] = p.Indices[i + 2];
                        idx[i + 2] = p.Indices[i + 1];
                    }
                    gm.Primitives.Add(new PassGlb.Primitive { Indices = idx, Material = mat });
                }
                foreach (var morph in mesh.Morphs)
                {
                    gm.Morphs.Add(new PassGlb.Morph
                    {
                        Name = morph.Name,
                        Positions = MirrorVectors(morph.Positions, 3),
                        Normals = morph.Normals != null ? MirrorVectors(morph.Normals, 3) : null,
                    });
                }
                if (gm.Primitives.Count > 0)
                {
                    glb.Meshes.Add(gm);
                }
                else
                {
                    Warnings.Add("Mesh " + mesh.Path + " has no triangles with a material");
                }
            }
            return glb.Build();
        }

        private void Skin(BundleMesh mesh, Dictionary<string, int> index, PassGlb.Mesh gm)
        {
            int n = mesh.VertexCount;
            gm.Joints = new int[n * 4];
            gm.Weights = new float[n * 4];
            int fallback = -1;
            foreach (var key in mesh.BoneKeys)
            {
                if (key != null && index.ContainsKey(key))
                {
                    fallback = index[key];
                    break;
                }
            }
            if (fallback < 0)
            {
                fallback = 0;
                Warnings.Add("Mesh " + mesh.Path + " has no bone in its Pass; it was skinned to the root");
            }
            for (int v = 0; v < n; v++)
            {
                var js = new int[4];
                var ws = new double[4];
                double total = 0;
                int count = 0;
                for (int k = 0; k < 4; k++)
                {
                    int j = mesh.Joints[v * 4 + k];
                    float w = mesh.Weights[v * 4 + k];
                    if (!(w > 0) || j < 0 || j >= mesh.BoneKeys.Length || mesh.BoneKeys[j] == null || !index.ContainsKey(mesh.BoneKeys[j]))
                    {
                        continue;
                    }
                    int joint = index[mesh.BoneKeys[j]];
                    int at = Array.IndexOf(js, joint, 0, count);
                    if (at >= 0)
                    {
                        ws[at] += w;
                    }
                    else
                    {
                        js[count] = joint;
                        ws[count] = w;
                        count++;
                    }
                    total += w;
                }
                if (count == 0)
                {
                    js[0] = fallback;
                    ws[0] = 1;
                    total = 1;
                    count = 1;
                }
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    gm.Joints[v * 4 + k] = k < count ? js[k] : 0;
                    float w = k < count ? (float)(ws[k] / total) : 0f;
                    gm.Weights[v * 4 + k] = w;
                    sum += w;
                }
                gm.Weights[v * 4] += 1f - sum; // exact unit sum in float
            }
        }

        private static float Finite(float f)
        {
            return float.IsNaN(f) || float.IsInfinity(f) ? 0f : f;
        }

        private static float[] MirrorVectors(float[] a, int width)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                r[i] = Finite(i % width == 0 ? -a[i] : a[i]);
            }
            return r;
        }

        private static float[] MirrorTangents(float[] a)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                int c = i % 4;
                r[i] = Finite(c == 0 || c == 3 ? -a[i] : a[i]);
            }
            return r;
        }

        /// <summary>glTF wants unit normals; a broken one (zero, not finite) becomes +Y.</summary>
        private static float[] UnitNormals(float[] n)
        {
            for (int v = 0; v < n.Length / 3; v++)
            {
                double x = n[v * 3], y = n[v * 3 + 1], z = n[v * 3 + 2];
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (!(len > 1e-8))
                {
                    x = 0; y = 1; z = 0; len = 1;
                }
                n[v * 3] = (float)(x / len);
                n[v * 3 + 1] = (float)(y / len);
                n[v * 3 + 2] = (float)(z / len);
            }
            return n;
        }

        /// <summary>Unit tangents with w = ±1; a broken one becomes any direction across its normal.</summary>
        private static float[] UnitTangents(float[] t, float[] n)
        {
            for (int v = 0; v < t.Length / 4; v++)
            {
                double x = t[v * 4], y = t[v * 4 + 1], z = t[v * 4 + 2];
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (!(len > 1e-8))
                {
                    double nx = n[v * 3], ny = n[v * 3 + 1], nz = n[v * 3 + 2];
                    // Cross with the axis least like the normal.
                    if (Math.Abs(nx) < 0.9) { x = 0; y = -nz; z = ny; } else { x = nz; y = 0; z = -nx; }
                    len = Math.Sqrt(x * x + y * y + z * z);
                }
                t[v * 4] = (float)(x / len);
                t[v * 4 + 1] = (float)(y / len);
                t[v * 4 + 2] = (float)(z / len);
                t[v * 4 + 3] = t[v * 4 + 3] < 0 ? -1f : 1f;
            }
            return t;
        }

        private JsonObject PassJson(BundlePass pass, JsonObject mapMeshes)
        {
            var meshes = new List<object>();
            foreach (var mesh in pass.Meshes)
            {
                bool hasTriangles = false;
                var mats = new List<object>();
                foreach (var p in mesh.Primitives)
                {
                    if (p.Indices != null && p.Indices.Length >= 3)
                    {
                        mats.Add(materialIds.Get(p.MaterialKey));
                        hasTriangles = true;
                    }
                }
                if (!hasTriangles)
                {
                    continue;
                }
                string id = meshIds.Get(mesh.Key);
                var kk = new JsonObject().Set("path", mesh.Path);
                if (mesh.Cloth)
                {
                    kk.Set("cloth", true);
                    kk.Set("source_vertices", SourceVertices(mesh));
                }
                meshes.Add(new JsonObject().Set("id", id).Set("name", mesh.Name ?? "").Set("materials", mats).Set("alternate", mesh.Alternate)
                    .Set("extensions", new JsonObject().Set("kk", kk)));

                var info = new JsonObject()
                    .Set("path", mesh.Path)
                    .Set("name", mesh.Name)
                    .Set("pass", pass.Kind == "body" ? "body" : "outfit" + pass.Outfit.Value.ToString("00", CultureInfo.InvariantCulture))
                    .Set("vertices", mesh.VertexCount)
                    .Set("ordinal", ordinals[mesh.Key])
                    .Set("blend_shapes", mesh.BlendShapes);
                foreach (string k in mesh.Info.Keys)
                {
                    info.Set(k, mesh.Info[k]);
                }
                if (mesh.Cloth)
                {
                    info.Set("cloth", true);
                }
                mapMeshes.Set(id, info);
            }
            var o = new JsonObject().Set("kind", pass.Kind).Set("outfit", pass.Outfit).Set("meshes", meshes);
            if (pass.Kind == "outfit" && pass.BodyTextures.Count > 0)
            {
                var list = new List<object>();
                var filled = new HashSet<string>();
                foreach (var bt in pass.BodyTextures)
                {
                    string slot = bt.MaterialKey + "|" + bt.Texture.Raw + "|" + bt.Texture.Variant + "|" + bt.Texture.Pattern;
                    if (!filled.Add(slot))
                    {
                        Warnings.Add("Outfit " + pass.Outfit + " set body texture " + slot + " twice");
                        continue;
                    }
                    list.Add(new JsonObject().Set("material", materialIds.Get(bt.MaterialKey)).Set("texture", TextureJson(bt.Texture)));
                }
                o.Set("body_textures", list);
            }
            return o;
        }

        private static bool HasTriangles(BundleMesh mesh)
        {
            return mesh.Primitives.Exists(p => p.Indices != null && p.Indices.Length >= 3);
        }

        private static int[] SourceVertices(BundleMesh mesh)
        {
            if (mesh.SourceVertices != null)
            {
                return mesh.SourceVertices;
            }
            var r = new int[mesh.VertexCount];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = i;
            }
            return r;
        }

        // --- skeleton, expressions, physics -------------------------------------------------------

        private JsonObject SkeletonJson(JsonObject map)
        {
            var list = new List<object>();
            var paths = new JsonObject();
            foreach (var bone in bundle.Bones)
            {
                string id = boneIds.Get(bone.Key);
                var world = rigidWorld[bone.Key];
                var local = bone.ParentKey == null ? world : rigidWorld[bone.ParentKey].Inverse() * world;
                double[] t, q, s;
                local.Decompose(out t, out q, out s);
                var o = new JsonObject().Set("id", id).Set("name", bone.Name ?? "")
                    .Set("parent", bone.ParentKey == null ? null : boneIds.Get(bone.ParentKey));
                if (bone.Role != null)
                {
                    o.Set("role", bone.Role);
                }
                o.Set("group", bone.Group).Set("translation", t).Set("rotation", q).Set("scale", new[] { 1.0, 1.0, 1.0 });
                double[] gt, gq, gs;
                bone.Rest.Decompose(out gt, out gq, out gs);
                var scale = new[] { Math.Abs(gs[0]), Math.Abs(gs[1]), Math.Abs(gs[2]) };
                if (Math.Abs(scale[0] - 1) > 1e-5 || Math.Abs(scale[1] - 1) > 1e-5 || Math.Abs(scale[2] - 1) > 1e-5)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        scale[i] = Math.Max(scale[i], 1e-6);
                    }
                    o.Set("game_scale", scale);
                }
                if (gs[0] < 0)
                {
                    Warnings.Add("Bone " + bone.Path + " is mirrored in its capture pose; Blender can't show the mirror");
                }
                list.Add(o);
                paths.Set(id, new JsonObject().Set("path", bone.Path).Set("outfit", bone.Outfit));
            }
            map.Set("bones", paths);
            return new JsonObject().Set("bones", list);
        }

        private JsonObject ExpressionsJson()
        {
            var meshPass = new Dictionary<string, BundlePass>();
            var meshByKey = new Dictionary<string, BundleMesh>();
            foreach (var pass in bundle.Passes)
            {
                foreach (var mesh in pass.Meshes)
                {
                    if (HasTriangles(mesh))
                    {
                        meshPass[mesh.Key] = pass;
                        meshByKey[mesh.Key] = mesh;
                    }
                }
            }
            var records = new List<object>();
            var seen = new HashSet<string>();
            var named = new HashSet<string>();
            var targetsWithRecords = new Dictionary<string, BundlePass>();
            var showingKeys = new HashSet<string>();
            foreach (var e in bundle.Expressions)
            {
                BundleMesh mesh;
                if (!meshByKey.TryGetValue(e.MeshKey, out mesh) || !mesh.Morphs.Exists(m => m.Name == e.Target))
                {
                    Warnings.Add("Expression " + e.Target + " names a shape key mesh " + e.MeshKey + " doesn't have");
                    continue;
                }
                if (!seen.Add(e.MeshKey + "|" + e.Target) || (e.Name != null && !named.Add(e.MeshKey + "|" + e.Name)))
                {
                    continue;
                }
                if (e.Name != null && (e.Target != e.Name || KkExpressionTable.CategoryOf(e.Name) != e.Category))
                {
                    Warnings.Add("Expression " + e.Target + " had a name or category that doesn't fit; its name was dropped");
                    e.Name = null;
                }
                records.Add(new JsonObject().Set("mesh", meshIds.Get(e.MeshKey)).Set("target", e.Target).Set("category", e.Category)
                    .Set("name", e.Name).Set("game_name", e.GameName));
                if (!targetsWithRecords.ContainsKey(e.Target))
                {
                    targetsWithRecords[e.Target] = meshPass[e.MeshKey];
                }
                if (e.GameName == null && (e.Category == "tears" || e.Category == "gag"))
                {
                    showingKeys.Add(e.MeshKey);
                }
            }

            var effects = new List<object>();
            var materialsByKey = new Dictionary<string, BundleMaterial>();
            foreach (var m in bundle.Materials)
            {
                materialsByKey[m.Key] = m;
            }
            var effectTargets = new HashSet<string>();
            foreach (var fx in bundle.Effects)
            {
                BundlePass pass;
                if (!targetsWithRecords.TryGetValue(fx.Target, out pass) || !effectTargets.Add(fx.Target))
                {
                    continue;
                }
                var o = new JsonObject().Set("target", fx.Target);
                var shows = new List<object>();
                foreach (var key in fx.Shows)
                {
                    if (meshPass.ContainsKey(key) && meshPass[key] == pass && showingKeys.Contains(key) && !shows.Contains(meshIds.Get(key)))
                    {
                        shows.Add(meshIds.Get(key));
                    }
                }
                var hides = new List<object>();
                foreach (var key in fx.Hides)
                {
                    if (meshPass.ContainsKey(key) && meshPass[key] == pass && !hides.Contains(meshIds.Get(key)))
                    {
                        hides.Add(meshIds.Get(key));
                    }
                }
                var changes = new List<object>();
                foreach (var c in fx.MaterialChanges)
                {
                    BundleMaterial mat;
                    if (!materialsByKey.TryGetValue(c.MaterialKey, out mat))
                    {
                        continue;
                    }
                    var textures = new List<object>();
                    foreach (var t in c.Textures)
                    {
                        if (mat.Textures.Exists(x => x.Raw == t.Raw && x.Pattern == t.Pattern))
                        {
                            textures.Add(new JsonObject().Set("raw", t.Raw).Set("pattern", t.Pattern));
                        }
                    }
                    changes.Add(new JsonObject().Set("material", materialIds.Get(c.MaterialKey)).Set("textures", textures)
                        .Set("floats", Map(c.Floats)).Set("vectors", Map(c.Vectors)));
                }
                if (shows.Count > 0)
                {
                    o.Set("shows", shows);
                }
                if (hides.Count > 0)
                {
                    o.Set("hides", hides);
                }
                if (changes.Count > 0)
                {
                    o.Set("material_changes", changes);
                }
                if (o.Count > 1)
                {
                    effects.Add(o);
                }
            }
            return new JsonObject().Set("expressions", records).Set("effects", effects);
        }

        private static float[] MirrorVector(float[] v)
        {
            return new[] { -v[0], v[1], v[2] };
        }

        private JsonObject PhysicsJson()
        {
            var colliders = new List<object>();
            var names = new HashSet<string>();
            foreach (var c in bundle.Colliders)
            {
                if (!bonesByKey.ContainsKey(c.BoneKey) || !names.Add(c.Name))
                {
                    continue;
                }
                colliders.Add(new JsonObject().Set("name", c.Name).Set("outfit", c.Outfit).Set("bone", boneIds.Get(c.BoneKey))
                    .Set("center", MirrorVector(c.Center))
                    .Set("rotation", new[] { c.Rotation[0], -c.Rotation[1], -c.Rotation[2], c.Rotation[3] })
                    .Set("radius", c.Radius).Set("height", c.Height).Set("direction", c.Direction).Set("bound", c.Bound));
            }
            var chains = new List<object>();
            foreach (var ch in bundle.Chains)
            {
                if (!bonesByKey.ContainsKey(ch.RootKey))
                {
                    continue;
                }
                var particles = new List<object>();
                foreach (var p in ch.Particles)
                {
                    if (bonesByKey.ContainsKey(p.BoneKey))
                    {
                        particles.Add(new JsonObject().Set("bone", boneIds.Get(p.BoneKey)).Set("damping", p.Damping).Set("elasticity", p.Elasticity)
                            .Set("stiffness", p.Stiffness).Set("inert", p.Inert).Set("radius", p.Radius));
                    }
                }
                if (particles.Count == 0)
                {
                    continue;
                }
                var exclusions = new List<object>();
                foreach (var e in ch.Exclusions)
                {
                    if (bonesByKey.ContainsKey(e))
                    {
                        exclusions.Add(boneIds.Get(e));
                    }
                }
                var used = new List<object>();
                foreach (var c in ch.Colliders)
                {
                    if (names.Contains(c) && !used.Contains(c))
                    {
                        used.Add(c);
                    }
                }
                chains.Add(new JsonObject().Set("root", boneIds.Get(ch.RootKey)).Set("outfit", ch.Outfit)
                    .Set("gravity", MirrorVector(ch.Gravity)).Set("force", MirrorVector(ch.Force)).Set("end_length", ch.EndLength)
                    .Set("end_offset", MirrorVector(ch.EndOffset)).Set("weight", ch.Weight).Set("colliders", used)
                    .Set("exclusions", exclusions).Set("particles", particles));
            }
            return new JsonObject().Set("chains", chains).Set("colliders", colliders);
        }
    }
}
