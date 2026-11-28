using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KK2Blender.Format;
using KK2Blender.Format.Bundle;
using Xunit;

namespace KK2Blender.Tests
{
    public class BundleTests
    {
        private static Affine Trs(double x, double y, double z, double angleY = 0, double scale = 1)
        {
            double h = angleY * Math.PI / 360;
            return Affine.FromTrs(new[] { x, y, z }, new[] { 0, Math.Sin(h), 0, Math.Cos(h) }, new[] { scale, scale, scale });
        }

        [Fact]
        public void DecomposeRoundTrips()
        {
            var a = Affine.FromTrs(new[] { 1.0, 2, 3 }, new[] { 0.1, 0.7, -0.2, 0.67 }, new[] { 1.5, 0.5, 2.0 });
            a.Decompose(out var t, out var q, out var s);
            var b = Affine.FromTrs(t, q, s);
            Assert.True(Affine.Distance(a, b, 1) < 1e-9);
            Assert.True(Affine.Distance(a * a.Inverse(), Affine.Identity, 1) < 1e-9);
        }

        [Fact]
        public void IdsFollowTheContract()
        {
            var ids = new IdTable("bone");
            Assert.Equal("cf_j_hips", ids.Make("a", "cf_j_hips"));
            Assert.Equal("cf_j_hips_2", ids.Make("b", "cf_j_hips"));
            Assert.Equal("cf_j_hips", ids.Make("a", "whatever"));
            Assert.Equal("bone", ids.Make("c", "髪"));
            Assert.Equal("a_b", ids.Make("d", "__a  b__"));
            Assert.Equal(63, ids.Make("e", new string('x', 100)).Length);
        }

        /// <summary>
        /// The capture is exact: a mesh skinned in the rest pose the solver finds, then posed by any
        /// bone matrices M_j as Blender poses it (M_j · rest_j⁻¹), lands where the game's own skinning
        /// (M_j · Bind_j) puts it, even when the character stood in another pose at export.
        /// </summary>
        [Fact]
        public void RestPoseCaptureIsExact()
        {
            var bindParent = Trs(0, 1, 0);
            var bindChild = Trs(0, 1.5, 0, 10);
            var mesh = Trs(0, 0, 0.1); // the renderer's transform at bind time
            var bind = new[] { bindParent.Inverse() * mesh, bindChild.Inverse() * mesh };
            // At export the bones are posed and scaled (a slider).
            var curParent = Trs(0.2, 0.9, 0, 30, 1.1);
            var curChild = curParent * Trs(0, 0.5, 0, 45);
            var solver = new RestSolver(new[]
            {
                new RestSolver.Bone { Key = "p", Current = curParent, Local = curParent },
                new RestSolver.Bone { Key = "c", ParentKey = "p", Current = curChild, Local = Trs(0, 0.5, 0, 45) },
                new RestSolver.Bone { Key = "r", ParentKey = "p", Current = curParent * Trs(0, -1, 0.1), Local = Trs(0, -1, 0.1) },
            });
            var skin = new RestSolver.Skin
            {
                Key = "m", BoneKeys = new[] { "p", "c" }, Bindposes = bind, RendererKey = "r", Renderer = curParent * Trs(0, -1, 0.1),
                Joints = new[] { 0, 1, 0, 0, 0, 1, 0, 0 }, Weights = new[] { 0.3f, 0.7f, 0, 0, 1f, 0, 0, 0 },
            };
            solver.Solve(new[] { skin });
            Assert.True(skin.Exact);
            float[] stored = { 0.1f, 1.2f, 0.05f, -0.1f, 1.6f, 0f };
            var rest = new Skinning(skin, 2).Positions(stored);

            var poseP = Trs(1, 2, 3, 70, 0.9);
            var poseC = poseP * Trs(0.1, 0.4, 0, -20, 1.2);
            var pose = new[] { poseP, poseC };
            var rests = new[] { solver.Rests["p"], solver.Rests["c"] };
            for (int v = 0; v < 2; v++)
            {
                double gx = 0, gy = 0, gz = 0, bx = 0, by = 0, bz = 0;
                for (int k = 0; k < 2; k++)
                {
                    int j = skin.Joints[v * 4 + k];
                    double w = skin.Weights[v * 4 + k];
                    (pose[j] * bind[j]).Point(stored[v * 3], stored[v * 3 + 1], stored[v * 3 + 2], out var x, out var y, out var z);
                    gx += w * x; gy += w * y; gz += w * z;
                    (pose[j] * rests[j].Inverse()).Point(rest[v * 3], rest[v * 3 + 1], rest[v * 3 + 2], out x, out y, out z);
                    bx += w * x; by += w * y; bz += w * z;
                }
                Assert.True(Math.Abs(gx - bx) < 1e-5 && Math.Abs(gy - by) < 1e-5 && Math.Abs(gz - bz) < 1e-5, $"vertex {v}");
            }
            // The rest pose is the bind pose, carried to where the renderer is.
            Assert.True(Affine.Distance(solver.Rests["p"], skin.K * mesh.Inverse() * bindParent, 1) < 1e-9);
        }

        [Fact]
        public void NormalMapsUnpackFromAlphaAndGreen()
        {
            var px = new byte[] { 0, 128, 0, 255 };
            Pixels.UnpackNormals(px);
            Assert.Equal(255, px[0]);
            Assert.InRange(px[1], 127, 129);
            Assert.InRange(px[2], 126, 129);
        }

        [Fact]
        public void WritesASampleBundle()
        {
            string root = Environment.GetEnvironmentVariable("K2T_SAMPLE_DIR");
            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(Path.GetTempPath(), "k2t");
            }
            string folder = Path.Combine(root, "bundle", BundleWriter.FolderName(new DateTime(2026, 11, 27, 12, 0, 0), "Sample Chara"));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
            var writer = new BundleWriter();
            writer.Write(SampleBundle.Make(), folder);
            Assert.Empty(writer.Warnings);
            Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
            string body = File.ReadAllText(Path.Combine(folder, "body.json"));
            Assert.Contains("\"path\": \"BodyTop/p_cf_body_00/cf_O_face\"", body);
            var glb = File.ReadAllBytes(Path.Combine(folder, "body.glb"));
            int len = BitConverter.ToInt32(glb, 12);
            string json = Encoding.UTF8.GetString(glb, 20, len);
            Assert.Contains("raw:eye_face.f00_def_op", json);
            // KKBP gives Body's outline the first material: the face's, so the face mesh comes first.
            Assert.True(body.IndexOf("cf_O_face", System.StringComparison.Ordinal) < body.IndexOf("o_body_a", System.StringComparison.Ordinal));
            Assert.True(json.IndexOf("cf_m_face_00", System.StringComparison.Ordinal) < json.IndexOf("cf_m_body", System.StringComparison.Ordinal));
        }
    }

    /// <summary>A small bundle shaped like a KK character: body and face, tears, gag eyes, one Outfit.</summary>
    public static class SampleBundle
    {
        private static byte[] Png(byte r, byte g, byte b)
        {
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
            }
            return Pixels.EncodePng(px, 4, 4);
        }

        private static BundleBone Bone(CharacterBundle c, string name, string parent, double x, double y, double z, string group = "deform", int? outfit = null)
        {
            string parentPath = parent == null ? null : c.Bones.First(b => b.Key == parent).Path;
            var bone = new BundleBone
            {
                Key = (outfit.HasValue ? "outfit" + outfit.Value.ToString("00") + ":" : "") + (parentPath == null ? name : parentPath + "/" + name),
                Name = name,
                ParentKey = parent,
                Path = parentPath == null ? name : parentPath + "/" + name,
                Outfit = outfit,
                Rest = Affine.FromTrs(new[] { x, y, z }, new[] { 0.0, 0, 0, 1 }, null),
                Role = BoneNames.Role(name),
                Group = group,
            };
            c.Bones.Add(bone);
            return bone;
        }

        /// <summary>A box of 8 vertices around (x, y, z), skinned fully to one bone.</summary>
        private static BundleMesh Box(string key, string name, string path, double x, double y, double z, double size, string bone, string material)
        {
            var m = new BundleMesh { Key = key, Name = name, Path = path };
            var p = new List<float>();
            var n = new List<float>();
            for (int i = 0; i < 8; i++)
            {
                double dx = (i & 1) == 0 ? -1 : 1, dy = (i & 2) == 0 ? -1 : 1, dz = (i & 4) == 0 ? -1 : 1;
                p.AddRange(new[] { (float)(x + dx * size), (float)(y + dy * size), (float)(z + dz * size) });
                double l = Math.Sqrt(3);
                n.AddRange(new[] { (float)(dx / l), (float)(dy / l), (float)(dz / l) });
            }
            m.Positions = p.ToArray();
            m.Normals = n.ToArray();
            m.Uvs.Add(Enumerable.Range(0, 16).Select(i => (float)((i * 0.37) % 1)).ToArray());
            m.BoneKeys = new[] { bone };
            m.Joints = new int[32];
            m.Weights = Enumerable.Range(0, 32).Select(i => i % 4 == 0 ? 1f : 0f).ToArray();
            m.Primitives.Add(new BundlePrimitive
            {
                MaterialKey = material,
                Indices = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 },
            });
            return m;
        }

        private static BundleMorph Morph(string name, float dy)
        {
            return new BundleMorph { Name = name, Positions = Enumerable.Range(0, 24).Select(i => i % 3 == 1 ? dy : 0f).ToArray(), Normals = new float[24] };
        }

        private static BundleMaterial Material(CharacterBundle c, string key, string name, string role, bool bake = true)
        {
            var m = new BundleMaterial { Key = key, Name = name, Shader = "Shader Forge/main_skin", Role = role };
            m.Colors.Add(new KeyValuePair<string, float[]>("_Color", new[] { 1f, 1f, 1f, 1f }));
            m.Floats.Add(new KeyValuePair<string, float>("_SpecularPower", 0.5f));
            m.Textures.Add(new BundleTexture { Role = "main", Raw = "_MainTex", Png = Png(200, 180, 170), Source = "tex:" + key });
            if (bake)
            {
                m.Textures.Add(new BundleTexture { Role = "main", Raw = "_MainTex", Variant = "light", Png = Png(230, 210, 200) });
                m.Textures.Add(new BundleTexture { Role = "main", Raw = "_MainTex", Variant = "dark", Png = Png(150, 120, 120) });
            }
            c.Materials.Add(m);
            return m;
        }

        public static CharacterBundle Make()
        {
            var c = new CharacterBundle { Name = "Sample Chara", ExporterVersion = "test", ExportedAt = new DateTime(2026, 11, 27, 4, 0, 0, DateTimeKind.Utc) };
            c.Card = Png(10, 20, 30);
            Bone(c, "BodyTop", null, 0, 0, 0, "helper");
            Bone(c, "cf_n_height", "BodyTop", 0, 0, 0);
            Bone(c, "cf_j_hips", "BodyTop/cf_n_height", 0, 0.9, 0);
            Bone(c, "cf_j_spine01", "BodyTop/cf_n_height/cf_j_hips", 0, 1.0, 0);
            var head = Bone(c, "cf_j_head", "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01", 0, 1.5, 0);
            head.Rest = Affine.FromTrs(new[] { 0.0, 1.5, 0 }, new[] { 0.0, 0, 0, 1 }, new[] { 1.05, 1.05, 1.05 });
            Bone(c, "cf_J_FaceRoot", head.Key, 0, 1.55, 0.02, "face");
            Bone(c, "cf_j_sk_00_00", "BodyTop/cf_n_height/cf_j_hips", 0, 0.85, 0.1, "skirt");

            Material(c, "body:o_body_a:0", "cf_m_body", "body");
            Material(c, "body:cf_O_face:0", "cf_m_face_00", "face");
            Material(c, "body:cf_O_namida_L:0", "c_m_namida", "tears", bake: false);
            Material(c, "body:cf_O_gag_eye_00:0", "c_m_gag_eye_00", "gag_eyes", bake: false);
            var gag = c.Materials.Last();
            gag.Textures.Add(new BundleTexture { Role = "main", Raw = "_MainTex", Pattern = "cf_t_gageye_00", Png = Png(0, 0, 0) });
            Material(c, "outfit00:top:0", "top_mat", null);
            Material(c, "outfit00:top_b:0", "top_mat_b", null);

            var bodyPass = BundlePass.Body();
            var bodyMesh = Box("body:o_body_a", "o_body_a", "BodyTop/p_cf_body_00/o_body_a", 0, 1.0, 0, 0.2, "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01", "body:o_body_a:0");
            bodyPass.Meshes.Add(bodyMesh);
            var face = Box("body:cf_O_face", "cf_O_face", "BodyTop/p_cf_body_00/cf_O_face", 0, 1.55, 0.05, 0.1, head.Key, "body:cf_O_face:0");
            face.BlendShapes.AddRange(new[] { "eye_face.f00_def_op", "eye_face.f00_def_cl" });
            face.Morphs.Add(Morph("Eye Default", 0.001f));
            face.Morphs.Add(Morph("Gag Circle Eyes 1", 0.002f));
            face.Morphs.Add(Morph("raw:eye_face.f00_def_op", 0.001f));
            face.Morphs.Add(Morph("raw:eye_face.f00_def_cl", -0.001f));
            bodyPass.Meshes.Add(face);
            var tears = Box("body:cf_O_namida_L", "cf_O_namida_L", "BodyTop/p_cf_body_00/cf_O_namida_L", 0, 1.55, -0.02, 0.02, head.Key, "body:cf_O_namida_L:0");
            tears.Morphs.Add(Morph("Tears big", 0.003f));
            bodyPass.Meshes.Add(tears);
            var gagMesh = Box("body:cf_O_gag_eye_00", "cf_O_gag_eye_00", "BodyTop/p_cf_body_00/cf_O_gag_eye_00", 0, 1.55, -0.02, 0.02, head.Key, "body:cf_O_gag_eye_00:0");
            gagMesh.Morphs.Add(Morph("Gag eye 00", 0.003f));
            bodyPass.Meshes.Add(gagMesh);
            c.Passes.Add(bodyPass);

            var outfit = BundlePass.ForOutfit(0);
            var top = Box("outfit00:top", "o_top_a", "BodyTop/ct_clothesTop/n_top_a/o_top_a", 0, 1.1, 0, 0.22, "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01", "outfit00:top:0");
            outfit.Meshes.Add(top);
            var topB = Box("outfit00:top_b", "o_top_b", "BodyTop/ct_clothesTop/n_top_b/o_top_b", 0, 1.1, 0, 0.22, "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01", "outfit00:top_b:0");
            topB.Alternate = true;
            topB.Cloth = true;
            topB.BlendShapes.Add("sk_open");
            topB.Morphs.Add(Morph("raw:sk_open", 0.01f));
            outfit.Meshes.Add(topB);
            outfit.BodyTextures.Add(new BundleBodyTexture
            {
                MaterialKey = "body:o_body_a:0",
                Texture = new BundleTexture { Role = "alpha_mask", Raw = "_AlphaMask", Png = Png(255, 0, 0) },
            });
            c.Passes.Add(outfit);

            void Raw(BundleMesh mesh, string shape)
            {
                c.Expressions.Add(new BundleExpression { MeshKey = mesh.Key, Target = "raw:" + shape, Category = "other", GameName = shape });
            }
            c.Expressions.Add(new BundleExpression { MeshKey = face.Key, Target = "Eye Default", Category = "eye", Name = "Eye Default", GameName = "Default" });
            c.Expressions.Add(new BundleExpression { MeshKey = face.Key, Target = "Gag Circle Eyes 1", Category = "gag", Name = "Gag Circle Eyes 1", GameName = "Circle Eyes 1" });
            Raw(face, "eye_face.f00_def_op");
            Raw(face, "eye_face.f00_def_cl");
            Raw(topB, "sk_open");
            c.Expressions.Add(new BundleExpression { MeshKey = tears.Key, Target = "Tears big", Category = "tears", Name = "Tears big" });
            c.Expressions.Add(new BundleExpression { MeshKey = gagMesh.Key, Target = "Gag eye 00", Category = "gag", Name = "Gag eye 00" });
            var fx = new BundleEffect { Target = "Gag Circle Eyes 1" };
            fx.Shows.Add(gagMesh.Key);
            var change = new BundleMaterialChange { MaterialKey = gag.Key };
            change.Textures.Add(new BundleTextureChange { Raw = "_MainTex", Pattern = "cf_t_gageye_00" });
            change.Floats.Add(new KeyValuePair<string, float>("_SizeSpeed", 1f));
            change.Vectors.Add(new KeyValuePair<string, float[]>("_TileAnimation", new[] { 1f, 1f, 0f, 0f }));
            fx.MaterialChanges.Add(change);
            c.Effects.Add(fx);

            var chain = new BundleChain { RootKey = "BodyTop/cf_n_height/cf_j_hips/cf_j_sk_00_00", Gravity = new[] { 0f, -0.01f, 0f } };
            chain.Particles.Add(new BundleParticle { BoneKey = chain.RootKey, Damping = 0.1f, Elasticity = 0.1f, Stiffness = 0.1f, Radius = 0.02f });
            chain.Colliders.Add("cf_hit_thigh_L");
            c.Chains.Add(chain);
            c.Colliders.Add(new BundleCollider { Name = "cf_hit_thigh_L", BoneKey = "BodyTop/cf_n_height/cf_j_hips", Radius = 0.05f, Height = 0.3f, Direction = "y" });

            BoneNames.SettleHelpers(c.Bones, new[] { "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01", head.Key }, b => "deform");
            return c;
        }
    
        [Fact]
        public static void SkinningBlendsPerVertexWhenBindPosesDisagree()
        {
            var skin = new RestSolver.Skin
            {
                Key = "m", BoneKeys = new[] { "a", "b" }, Exact = false,
                RestSkin = new[] { Affine.Identity, new Affine { M00 = 1, M11 = 1, M22 = 1, M03 = 1 } },
                Joints = new[] { 0, 1, 0, 0, 1, 0, 0, 0 }, Weights = new[] { 0.5f, 0.5f, 0, 0, 1f, 0, 0, 0 },
            };
            var p = new Skinning(skin, 2).Positions(new[] { 0f, 0f, 0f, 0f, 0f, 0f });
            Assert.Equal(0.5f, p[0], 5);
            Assert.Equal(1f, p[3], 5);
            Assert.Throws<ArgumentException>(() => new Skinning(skin, 3));
        }
}
}
