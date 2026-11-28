using System.Collections.Generic;
using System.Linq;
using KK2Blender.Format.Bundle;
using Xunit;

namespace KK2Blender.Tests
{
    public class MaterialRulesTests
    {
        [Theory]
        [InlineData("Shader Forge/toon_glasses_lod0", 3000, null, "glasses")]
        [InlineData("Shader Forge/main_alpha", 2000, null, "alpha")]
        // Material Editor's studio shaders blend whatever queue they were left at (a mod face's eyeline).
        [InlineData("Shader Forge/main_item_studio_alpha", 2450, null, "alpha")]
        [InlineData("KKUTSalpha", 2000, null, "alpha")]
        [InlineData("Shader Forge/main_item", 3100, null, "alpha")]
        [InlineData("Shader Forge/main_item", 2450, null, "cutout")]
        [InlineData("Shader Forge/main_item", 2000, null, "opaque")]
        // The face overlays are blended, as KKBP's exporter marks them.
        [InlineData("Shader Forge/toon_eyew_lod0", 2450, "eyebrow", "alpha")]
        [InlineData("Shader Forge/toon_eye_lod0", 2450, "eye_l", "alpha")]
        [InlineData("Shader Forge/toon_eyew_lod0", 2450, "eye_white", "cutout")]
        // AlphaMask shaders multiply a mask; they don't blend.
        [InlineData("Shader Forge/AlphaMaskMultiply", 2000, null, "opaque")]
        [InlineData(null, 2000, null, "opaque")]
        public void BlendModeFromShaderRoleAndQueue(string shader, int queue, string role, string expected)
        {
            Assert.Equal(expected, MaterialRules.BlendMode(shader, queue, role));
        }

        [Theory]
        [InlineData("o_Mask", true)]
        [InlineData("o_Mask(Clone)", true)]
        [InlineData("o_shadowcaster", true)]
        [InlineData("o_body_a", false)]
        [InlineData("o_Maskot", false)]
        public void NeverExportsTheMaskAndStandIns(string name, bool expected)
        {
            Assert.Equal(expected, MaterialRules.NeverExport(name));
        }

        [Fact]
        public void ExtraMaterialsDrawTheLastSubmeshAgain()
        {
            Assert.Equal(0, MaterialRules.Submesh(0, 1));
            Assert.Equal(0, MaterialRules.Submesh(1, 1));
            Assert.Equal(2, MaterialRules.Submesh(5, 3));
            Assert.False(MaterialRules.IsOverlayCopy(0, 1));
            Assert.True(MaterialRules.IsOverlayCopy(1, 1));
        }

        [Fact]
        public void UnblendSolvesStraightColourAndAlpha()
        {
            // A blended shader with colour (200, 100, 50) at alpha 0.78 over black and over white.
            double a = 0.78;
            byte[] black = { (byte)(200 * a), (byte)(100 * a), (byte)(50 * a), (byte)(255 * a * a) };
            byte[] white = { (byte)(200 * a + 255 * (1 - a)), (byte)(100 * a + 255 * (1 - a)), (byte)(50 * a + 255 * (1 - a)), 255 };
            Pixels.Unblend(black, white);
            Assert.InRange(black[0], 198, 202);
            Assert.InRange(black[1], 98, 102);
            Assert.InRange(black[2], 48, 52);
            Assert.InRange(black[3], 197, 201); // 0.78, not 0.78² as the render target's alpha says

            // Opaque: the same on both. Nothing drawn: black over black, white over white.
            byte[] opaque = { 10, 20, 30, 255 };
            Pixels.Unblend(opaque, new byte[] { 10, 20, 30, 255 });
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, opaque);
            byte[] empty = { 0, 0, 0, 0 };
            Pixels.Unblend(empty, new byte[] { 255, 255, 255, 255 });
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, empty);
        }

        [Fact]
        public void TheFaceMeshLeadsTheBodyPass()
        {
            var body = new BundleMesh { Key = "body" };
            body.Primitives.Add(new BundlePrimitive { MaterialKey = "m:body" });
            var canine = new BundleMesh { Key = "canine" };
            canine.Primitives.Add(new BundlePrimitive { MaterialKey = "m:canine" });
            var face = new BundleMesh { Key = "face" };
            face.Primitives.Add(new BundlePrimitive { MaterialKey = "m:face" });
            var meshes = new List<BundleMesh> { body, canine, face };
            var roles = new Dictionary<string, string> { { "m:body", "body" }, { "m:canine", "teeth" }, { "m:face", "face" } };
            BundleWriter.FaceFirst(meshes, k => roles[k]);
            Assert.Equal(new[] { "face", "body", "canine" }, meshes.Select(m => m.Key).ToArray());
        }
    }
}
