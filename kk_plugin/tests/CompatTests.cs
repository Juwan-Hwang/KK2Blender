using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KK2Blender.Format;
using KK2Blender.Format.Bundle;
using Xunit;

namespace KK2Blender.Tests
{
    /// <summary>Card compatibility: shader property tables, shader families and their mapping, outfit
    /// roles, Flat bakes, skin checks, implanted bones and the export self-check.</summary>
    public class CompatTests
    {
        private static string DataFile
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "shader-properties.json")))
                {
                    dir = dir.Parent;
                }
                Assert.NotNull(dir);
                return Path.Combine(dir.FullName, "data", "shader-properties.json");
            }
        }

        // --- the JSON reader and the shader property table ---------------------------------------------

        [Fact]
        public void JsonReaderReadsTheTableShapes()
        {
            var o = (Dictionary<string, object>)JsonReader.Parse("{\"a\": {\"_X\": \"c\"}, \"b\": [1, -2.5e1, true, null, \"q\\\"\\u00e9\"]}");
            Assert.Equal("c", ((Dictionary<string, object>)o["a"])["_X"]);
            var list = (List<object>)o["b"];
            Assert.Equal(1.0, list[0]);
            Assert.Equal(-25.0, list[1]);
            Assert.Equal(true, list[2]);
            Assert.Null(list[3]);
            Assert.Equal("q\"é", list[4]);
            Assert.Throws<FormatException>(() => JsonReader.Parse("{\"a\": 1"));
        }

        [Fact]
        public void KnownShaderGetsItsOwnPropertiesPlusTheEverywhereOnes()
        {
            var t = new ShaderTable();
            t.Load("{\"S\": {\"_A\": \"c\", \"_B\": \"range\"}, \"T\": {\"_C\": \"t\"}, \"\": {\"_Me\": \"f\"}}");
            var s = t.For("S");
            Assert.Contains(s, p => p.Name == "_A" && p.Kind == 'c');
            Assert.Contains(s, p => p.Name == "_B" && p.Kind == 'f');
            Assert.Contains(s, p => p.Name == "_Me");
            Assert.DoesNotContain(s, p => p.Name == "_C"); // another shader's
            Assert.Contains(s, p => p.Name == "_MainTex" && p.Kind == 't'); // KK's builtin list
            Assert.Equal(s.Count, s.Select(p => p.Name).Distinct().Count());
        }

        [Fact]
        public void UnknownShaderTriesEveryPropertyWithItsMostCommonKind()
        {
            var t = new ShaderTable();
            t.Load("{\"S\": {\"_X\": \"c\"}, \"T\": {\"_X\": \"c\", \"_Y\": \"t\"}, \"U\": {\"_X\": \"f\"}}");
            var u = t.For("Never/Seen");
            Assert.Contains(u, p => p.Name == "_X" && p.Kind == 'c');
            Assert.Contains(u, p => p.Name == "_Y" && p.Kind == 't');
            Assert.False(t.Knows("Never/Seen"));
            Assert.True(t.Knows("T"));
            // A later table adds properties but doesn't change a listed kind.
            t.Load("{\"T\": {\"_X\": \"f\", \"_Z\": \"v\"}}");
            Assert.Contains(t.For("T"), p => p.Name == "_X" && p.Kind == 'c');
            Assert.Contains(t.For("T"), p => p.Name == "_Z" && p.Kind == 'v');
        }

        [Fact]
        public void TheShippedTableCoversTheMaterialEditorShaders()
        {
            var t = new ShaderTable();
            t.Load(File.ReadAllText(DataFile));
            foreach (var shader in new[] { "KKUTS", "KKUTShair", "KKUTSalpha", "KKUSS", "IBL_Shader_alpha", "Goo", "BetterMatcap", "Rinfu/StockingShader",
                "Fur/FurRimColorShader", "Standard", "UnityChanToonShader/Toon_DoubleShadeWithFeather" })
            {
                Assert.True(t.Knows(shader), shader);
            }
            var kkuts = t.For("KKUTS");
            Assert.Contains(kkuts, p => p.Name == "_1st_ShadeColor" && p.Kind == 'c');
            Assert.Contains(kkuts, p => p.Name == "_MatCap_Sampler" && p.Kind == 't');
            Assert.Contains(kkuts, p => p.Name == "_1st_ShadeColor_Step" && p.Kind == 'f');
            // Material Editor property names seen on cards are tried on every shader (xukmi's among them).
            var any = t.For("xukmi/MainOpaquePlus");
            foreach (var name in new[] { "_SpeclarHeight", "_AnotherRampFull", "_UseKKMetal", "_ReflectiveBlend", "_KKPRimColor" })
            {
                Assert.Contains(any, p => p.Name == name);
            }
        }

        // --- shader families --------------------------------------------------------------------------

        [Theory]
        [InlineData("Shader Forge/main_hair", "kk", true, true, BakeKind.Game)]
        [InlineData("Shader Forge/main_hair_low", "kk", true, true, BakeKind.Game)]
        [InlineData("Shader Forge/main_item_low", "kk", true, false, BakeKind.Game)]
        [InlineData("Shader Forge/main_opaque_low2", "kk", true, false, BakeKind.Game)]
        [InlineData("xukmi/HairFrontPlus", "xukmi", true, true, BakeKind.Game)]
        [InlineData("KKUTS", "kkuts", false, false, BakeKind.Game)]
        [InlineData("KKUTShair_Tess", "kkuts", false, true, BakeKind.Game)]
        [InlineData("KKUSShair", "kkuss", false, true, BakeKind.Game)]
        [InlineData("UnityChanToonShader/Toon_DoubleShadeWithFeather", "uts2", false, false, BakeKind.Game)]
        [InlineData("IBL_Shader_cutoff", "ibl", false, false, BakeKind.Game)]
        [InlineData("Standard", "standard", false, false, BakeKind.Game)]
        [InlineData("Az/StandardAlpha", "standard", false, false, BakeKind.Game)]
        [InlineData("Toony Colors Pro 2/Standard PBS Cull Off (specular)", "tcp2", false, false, BakeKind.Game)]
        [InlineData("Rinfu/StockingShader", "stocking", false, false, BakeKind.Game)]
        [InlineData("BetterMatcap", "matcap", false, false, BakeKind.Flat)]
        [InlineData("Goo", "goo", false, false, BakeKind.Flat)]
        [InlineData("Fur/FurRimColorShader", "fur", false, false, BakeKind.Flat)]
        [InlineData("DA/Chara/TextureScroll", "scroll", false, false, BakeKind.Flat)]
        [InlineData("Shader Forge/main_texture_studio", "kk_texture", false, false, BakeKind.Flat)]
        [InlineData("Xffect/heat_distortion_t2", "screen_effect", false, false, BakeKind.Invisible)]
        [InlineData("Custom/TwoPassGaussianBlur", "screen_effect", false, false, BakeKind.Invisible)]
        [InlineData("Unlit/Glitch3DProjection", "effect", false, false, BakeKind.Flat)]
        [InlineData("Particles/Additive", "effect", false, false, BakeKind.Flat)]
        [InlineData("Some/NewShader", "unknown", false, false, BakeKind.Game)]
        public void ShadersSortIntoFamilies(string shader, string family, bool modelled, bool hair, BakeKind bake)
        {
            var f = ShaderFamilies.Classify(shader);
            Assert.Equal(family, f.Id);
            Assert.Equal(modelled, f.Modelled);
            Assert.Equal(hair, f.Hair);
            Assert.Equal(bake, f.Bake);
        }

        [Fact]
        public void OutlinedFamiliesZeroTheirOutlineForTheBake()
        {
            Assert.True(ShaderFamilies.Classify("KKUTS").HasOutline);
            Assert.Contains("_Outline_Width", ShaderFamilies.Classify("KKUTS").BakeZeroFloats);
            Assert.Contains("_outline", ShaderFamilies.Classify("KKUSS").BakeZeroFloats);
            Assert.Contains("_OutlineWidth", ShaderFamilies.Classify("Rinfu/StockingShader").BakeZeroFloats);
            Assert.False(ShaderFamilies.Classify("KKUTSalpha").HasOutline); // its OUTLINE pass is unnamed (-)
            Assert.Contains("_Metallic", ShaderFamilies.Classify("Standard").BakeZeroFloats);
            Assert.Empty(ShaderFamilies.Classify("Shader Forge/main_item").BakeZeroFloats); // modelled: untouched
        }

        [Theory]
        [InlineData("Goo", 2450, float.NaN, "alpha")]
        [InlineData("IBL_Shader_alpha", 2450, float.NaN, "alpha")]
        [InlineData("IBL_Shader_cutoff", 2000, float.NaN, "cutout")]
        [InlineData("Standard", 2000, 0f, "opaque")]
        [InlineData("Standard", 2450, 1f, "cutout")]
        [InlineData("Standard", 3000, 2f, "alpha")]
        [InlineData("Standard", 3000, float.NaN, "alpha")]
        [InlineData("UnityChanToonShader/Toon_DoubleShadeWithFeather_TransClipping", 2000, float.NaN, "cutout")]
        [InlineData("Az/StandardCutoff", 2000, float.NaN, "cutout")]
        [InlineData("KKUTSalpha", 2450, float.NaN, "alpha")]
        [InlineData("KKUTS", 2450, float.NaN, "cutout")]
        [InlineData("Xffect/heat_distortion_t2", 3000, float.NaN, "alpha")]
        // Modelled shaders keep the 0.1.4 rules.
        [InlineData("Shader Forge/main_hair_front", 2475, float.NaN, "cutout")]
        [InlineData("Shader Forge/main_item_low", 2000, float.NaN, "opaque")]
        public void FamiliesDecideTheirBlendMode(string shader, int queue, float mode, string expected)
        {
            Assert.Equal(expected, MaterialRules.BlendMode(shader, queue, null, ShaderFamilies.Classify(shader), mode));
        }

        [Fact]
        public void MappedFamiliesGiveTheirMainAndNormalTexturesARole()
        {
            var uts = ShaderFamilies.Classify("KKUTS");
            var taken = new HashSet<string>();
            Assert.Equal("main", MaterialRules.TextureRole("_MainTex", uts, taken));
            Assert.Null(MaterialRules.TextureRole("_BaseMap", uts, taken)); // main is taken
            Assert.Equal("normal", MaterialRules.TextureRole("_NormalMap", uts, taken));
            Assert.Null(MaterialRules.TextureRole("_MatCap_Sampler", uts, taken));

            taken.Clear();
            Assert.Equal("main", MaterialRules.TextureRole("_BaseMap", ShaderFamilies.Classify("UnityChanToonShader/Toon_DoubleShadeWithFeather"), taken));
            taken.Clear();
            Assert.Equal("normal", MaterialRules.TextureRole("_BumpMap", ShaderFamilies.Classify("Standard"), taken));
            taken.Clear();
            Assert.Equal("main", MaterialRules.TextureRole("_Maintex", ShaderFamilies.Classify("Rinfu/StockingShader"), taken));
            Assert.Equal("normal", MaterialRules.TextureRole("_Normalmap", ShaderFamilies.Classify("Rinfu/StockingShader"), taken));
            // Overlays repeat; modelled shaders get KK's names only.
            Assert.Equal("overlay", MaterialRules.TextureRole("_overtex1", uts, taken));
            Assert.Equal("overlay", MaterialRules.TextureRole("_overtex2", uts, taken));
            Assert.Null(MaterialRules.TextureRole("_BumpMap", ShaderFamilies.Classify("Shader Forge/main_item"), new HashSet<string>()));
        }

        [Theory]
        [InlineData("hair:1", false, "Shader Forge/main_hair", "hair")]
        [InlineData("accessory:3", true, "Shader Forge/main_hair", "hair")] // ao_hair accessory (ChaCustomHairComponent)
        [InlineData("accessory:3", true, "Shader Forge/main_item", "hair")]
        [InlineData("accessory:3", false, "KKUTShair", "hair")] // a hair shader on an accessory
        [InlineData("accessory:3", false, "Shader Forge/main_item", null)]
        [InlineData("clothes:0", false, "KKUTShair", null)]
        [InlineData("accessory:extra", true, "Shader Forge/main_hair_low", "hair")]
        public void HairAccessoriesGetTheHairRole(string owner, bool hairComponent, string shader, string expected)
        {
            Assert.Equal(expected, MaterialRules.OutfitRole(owner, hairComponent, ShaderFamilies.Classify(shader)));
        }

        // --- Flat bakes and the empty-bake check ------------------------------------------------------

        [Fact]
        public void PlaceTilesAndOffsetsLikeAMaterial()
        {
            // 2x1 source: left red, right blue.
            var src = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
            var same = Pixels.Place(src, 2, 1, 2, 1, 1, 1, 0, 0);
            Assert.Equal(src, same);
            // Offset by half: the halves swap.
            var swapped = Pixels.Place(src, 2, 1, 2, 1, 1, 1, 0.5f, 0);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, swapped);
            // Tiled twice over 4 pixels: red blue red blue.
            var tiled = Pixels.Place(src, 2, 1, 4, 1, 2, 1, 0, 0);
            Assert.Equal(255, tiled[0]);
            Assert.Equal(255, tiled[6]);
            Assert.Equal(255, tiled[8]);
            Assert.Equal(255, tiled[14]);
        }

        [Fact]
        public void TintAverageAndCoverage()
        {
            var px = Pixels.Solid(4, 1f, 1f, 1f, 1f);
            Pixels.Tint(px, 0.5f, 0f, 1f, 0.5f);
            Assert.Equal(new byte[] { 128, 0, 255, 128 }, px.Take(4).ToArray());
            var avg = Pixels.Average(px);
            Assert.InRange(avg[0], 0.49f, 0.51f);
            Assert.InRange(avg[3], 0.49f, 0.51f);
            Assert.Equal(1.0, Pixels.Coverage(px));
            Assert.Equal(0.0, Pixels.Coverage(Pixels.Solid(4, 0f, 0f, 0f, 1f))); // black
            Assert.Equal(0.0, Pixels.Coverage(Pixels.Solid(4, 1f, 1f, 1f, 0f))); // transparent
            Assert.True(Pixels.GameBakeLooksEmpty(0.0, 0.8));
            Assert.False(Pixels.GameBakeLooksEmpty(0.0, 0.01)); // a black or empty texture: nothing to gain
            Assert.False(Pixels.GameBakeLooksEmpty(0.5, 0.8));
        }

        // --- skins: dropped weights and implanted bones -----------------------------------------------

        [Fact]
        public void DroppedWeightCountsWeightOnMissingBones()
        {
            var keys = new[] { "a", null, "c" };
            var joints = new[] { 0, 1, 0, 0, 2, 5, 0, 0 };
            var weights = new[] { 0.6f, 0.4f, 0f, 0f, 0.5f, 0.5f, 0f, 0f };
            Assert.Equal(0.9, SkinChecks.DroppedWeight(keys, joints, weights), 5);
            Assert.Equal(0.0, SkinChecks.DroppedWeight(new[] { "a" }, new[] { 0, 0, 0, 0 }, new[] { 1f, 0, 0, 0 }));
        }

        private static Affine Trs(double x, double y, double z, double angleY = 0)
        {
            double h = angleY * Math.PI / 360;
            return Affine.FromTrs(new[] { x, y, z }, new[] { 0, Math.Sin(h), 0, Math.Cos(h) }, null);
        }

        /// <summary>
        /// A clothing mod's bones implanted under a body bone (ModBoneImplantor) are keyed with the Outfit
        /// and parented to the body bone. The clothes mesh skinned to the body bone and to them rests
        /// exactly, and the implanted bones take the clothes' bind pose, not their current pose.
        /// </summary>
        [Fact]
        public void ImplantedBonesRestWithTheClothesTheyCameWith()
        {
            var spine = Trs(0, 1, 0);
            var implantRoot = spine * Trs(0, 0.1, 0.1);
            var implantTip = implantRoot * Trs(0, -0.1, 0, 20);
            // Bound in another pose than they stand in now (physics moved the tip).
            var bindRoot = Trs(0, 1.1, 0.1);
            var bindTip = bindRoot * Trs(0, -0.1, 0);
            var body = new RestSolver.Skin
            {
                Key = "body", BoneKeys = new[] { "spine" }, Bindposes = new[] { spine.Inverse() },
                Joints = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0, 0, 0 }, RendererKey = "spine", Renderer = Affine.Identity,
            };
            var clothes = new RestSolver.Skin
            {
                Key = "outfit00:skirt", BoneKeys = new[] { "spine", "outfit00:spine/implant", "outfit00:spine/implant/tip" },
                Bindposes = new[] { spine.Inverse(), bindRoot.Inverse(), bindTip.Inverse() },
                Joints = new[] { 0, 0, 0, 0, 1, 2, 0, 0 }, Weights = new[] { 1f, 0, 0, 0, 0.5f, 0.5f, 0, 0 },
                RendererKey = "spine", Renderer = Affine.Identity,
            };
            var solver = new RestSolver(new[]
            {
                new RestSolver.Bone { Key = "spine", Current = spine, Local = spine },
                new RestSolver.Bone { Key = "outfit00:spine/implant", ParentKey = "spine", Current = implantRoot, Local = Trs(0, 0.1, 0.1) },
                new RestSolver.Bone { Key = "outfit00:spine/implant/tip", ParentKey = "outfit00:spine/implant", Current = implantTip, Local = Trs(0, -0.1, 0, 20) },
            });
            solver.Solve(new[] { body, clothes });
            Assert.True(clothes.Exact);
            Assert.True(Affine.Distance(solver.Rests["outfit00:spine/implant/tip"], bindTip, 1) < 1e-9);
            Assert.Equal(0.0, SkinChecks.DroppedWeight(clothes.BoneKeys, clothes.Joints, clothes.Weights));
        }

        [Fact]
        public void ImplantedBonesAreWrittenIntoTheOutfitSkeleton()
        {
            var c = SampleBundle.Make();
            const string spine = "BodyTop/cf_n_height/cf_j_hips/cf_j_spine01";
            c.Bones.Add(new BundleBone
            {
                Key = "outfit00:" + spine + "/mod_skirt_root", Name = "mod_skirt_root", ParentKey = spine, Path = spine + "/mod_skirt_root",
                Outfit = 0, Rest = Affine.FromTrs(new[] { 0.0, 0.95, 0.1 }, new[] { 0.0, 0, 0, 1 }, null), Group = "accessory",
            });
            var top = c.Passes[1].Meshes[0];
            top.BoneKeys = new[] { spine, "outfit00:" + spine + "/mod_skirt_root" };
            for (int v = 0; v < 8; v++)
            {
                top.Joints[v * 4] = v < 4 ? 0 : 1;
            }
            string folder = Path.Combine(Path.GetTempPath(), "k2t-implant-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new BundleWriter();
                writer.Write(c, folder);
                Assert.Empty(writer.Warnings);
                var skeleton = (Dictionary<string, object>)JsonReader.Parse(File.ReadAllText(Path.Combine(folder, "skeleton.json")));
                var bones = ((List<object>)skeleton["bones"]).Cast<Dictionary<string, object>>().ToList();
                var implant = bones.Single(b => (string)b["name"] == "mod_skirt_root");
                var parent = bones.Single(b => (string)b["id"] == (string)implant["parent"]);
                Assert.Equal("cf_j_spine01", parent["name"]);
                // The outfit's glb skins the clothes to it.
                var glb = File.ReadAllBytes(Path.Combine(folder, "outfits", "00", "outfit.glb"));
                string gltf = System.Text.Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12));
                Assert.Contains("\"" + (string)implant["id"] + "\"", gltf);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        [Fact]
        public void NonFiniteMaterialValuesAreLeftOut()
        {
            var c = SampleBundle.Make();
            c.Materials[0].Floats.Add(new KeyValuePair<string, float>("_Broken", float.NaN));
            c.Materials[0].Colors.Add(new KeyValuePair<string, float[]>("_BrokenColor", new[] { 1f, float.PositiveInfinity, 0f, 1f }));
            string folder = Path.Combine(Path.GetTempPath(), "k2t-nan-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new BundleWriter();
                writer.Write(c, folder);
                string json = File.ReadAllText(Path.Combine(folder, "materials.json"));
                Assert.DoesNotContain("_Broken", json);
                Assert.Contains("_SpecularPower", json);
                Assert.Equal(2, writer.Warnings.Count(w => w.Contains("finite")));
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        // --- the export self-check --------------------------------------------------------------------

        [Fact]
        public void DiagnosticsListMappedShadersAndIssues()
        {
            var d = new Diagnostics { Exporter = "KK2Blender test" };
            var ch = d.Begin("Sample");
            ch.Shader("Shader Forge/main_item", ShaderFamilies.Classify("Shader Forge/main_item"), "opaque", "m1");
            ch.Shader("KKUTS", ShaderFamilies.Classify("KKUTS"), "cutout", "kkuts_mat");
            ch.Shader("KKUTS", ShaderFamilies.Classify("KKUTS"), "cutout", "kkuts_mat2");
            ch.Shader("Goo", ShaderFamilies.Classify("Goo"), "alpha", "goo");
            ch.Add("dropped_weights", "BodyTop/ct_top/o_top", "0.5 vertex weight on bone_x");
            ch.Add("me_copy", "BodyTop/ct_top/o_top", "slot 2");
            ch.Add("me_copy", "BodyTop/ct_top/o_bot", "slot 1");
            ch.OutfitMaterialsWithoutRole = 3;

            string text = d.Summary();
            Assert.Contains("KKUTS: family kkuts, game bake, cutout, 2 materials", text);
            Assert.Contains("Goo: family goo, flat bake, alpha, 1 material", text);
            Assert.DoesNotContain("main_item:", text); // modelled shaders aren't listed
            Assert.Contains("1 skin weights on bones that couldn't be found (dropped) (e.g. BodyTop/ct_top/o_top: 0.5 vertex weight on bone_x)", text);
            Assert.Contains("2 Material Editor material copies", text);

            var json = (Dictionary<string, object>)JsonReader.Parse(JsonWriter.Write(d.ToJson()));
            Assert.Equal("kk2blender-diagnostics", json["format"]);
            var c0 = (Dictionary<string, object>)((List<object>)json["characters"])[0];
            var counts = (Dictionary<string, object>)c0["counts"];
            Assert.Equal(2.0, counts["me_copy"]);
            var shaders = ((List<object>)c0["shaders"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal("KKUTS", shaders[0]["shader"]); // mapped first, most used first
            Assert.Equal(false, shaders[0]["modelled_by_kkbp"]);
            Assert.Equal(3.0, c0["outfit_materials_without_role"]);
        }

        [Fact]
        public void DiagnosticsOfACleanExportSayNoIssues()
        {
            var d = new Diagnostics();
            var ch = d.Begin("Clean");
            ch.Shader("Shader Forge/main_skin", ShaderFamilies.Classify("Shader Forge/main_skin"), "opaque", "skin");
            string text = d.Summary();
            Assert.Contains("all shaders are modelled by KKBP", text);
            Assert.Contains("no issues found", text);
        }
    }
}
