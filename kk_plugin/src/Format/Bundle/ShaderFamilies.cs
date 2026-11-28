using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    /// <summary>How the light/dark bake of a material is made.</summary>
    public enum BakeKind
    {
        /// <summary>The game's own shader draws the material on a quad in UV space (the default).</summary>
        Game,
        /// <summary>The main texture times the main colour (and a shade for the dark Variant): for shaders
        /// a UV-space render can't draw (screen effects, shells, view-dependent matcaps, scrolling).</summary>
        Flat,
        /// <summary>Fully transparent: screen-space effects with nothing of their own to show (heat
        /// distortion, blur).</summary>
        Invisible,
    }

    /// <summary>A shader family and how the exporter treats its materials (docs/shader-mapping.md).</summary>
    public sealed class ShaderFamily
    {
        public string Id;
        /// <summary>KKBP models this shader (KK's own and xukmi's): its values mean what KKBP expects.</summary>
        public bool Modelled;
        /// <summary>A hair shader: its materials take the "hair" Material Role in an Outfit.</summary>
        public bool Hair;
        public BakeKind Bake = BakeKind.Game;
        /// <summary>The Blend Mode the family always draws with, or null to decide by name and queue.</summary>
        public string BlendMode;
        /// <summary>Texture properties that hold the main (albedo) texture, first found wins.</summary>
        public string[] MainTextures = { "_MainTex" };
        /// <summary>Colour properties the main texture is multiplied by, first found wins.</summary>
        public string[] MainColors = { "_Color" };
        /// <summary>Colour properties of the shaded side, first found wins (Flat bakes' dark Variant).</summary>
        public string[] ShadeColors = { "_ShadowColor", "_shadowcolor" };
        /// <summary>Normal map properties, in order; the first found takes the "normal" Texture Role.</summary>
        public string[] NormalMaps = { "_NormalMap", "_BumpMap", "_Normalmap" };
        /// <summary>For a Flat bake of a material without a main texture: textures whose average colour
        /// stands in for it (a matcap's).</summary>
        public string[] SolidFrom = new string[0];
        /// <summary>Floats set to 0 on the bake's copy of the material (metal and gloss a lone light
        /// can't show, outline widths).</summary>
        public string[] BakeZeroFloats = new string[0];
        /// <summary>Textures cleared on the bake's copy (normal maps and other view-dependent maps).</summary>
        public string[] BakeClearTextures = new string[0];
        /// <summary>The family draws an outline pass of its own; KKBP's Outlines hook gives it one in
        /// Blender, so the bake draws none.</summary>
        public bool HasOutline;

        public override string ToString()
        {
            return Id;
        }
    }

    /// <summary>
    /// Sorts shaders into families by name (KK's own, xukmi, KKUTS, KKUSS, IBL, Unity Standard, UTS2,
    /// Toony Colors Pro 2, Material Editor effect shaders...), so each gets the right main texture,
    /// colour, normal map, Blend Mode and bake. KKBP's KK Template Set picks a material's template by
    /// its Material Role and Blend Mode and draws its light/dark bake, so getting those three right is
    /// what makes a shader KKBP doesn't model render plausibly. Game-independent and unit tested.
    /// </summary>
    public static class ShaderFamilies
    {
        /// <summary>KK's own shaders (and Koikano's, KKS's names for them) and xukmi's, which KKBP models.</summary>
        public static readonly HashSet<string> Vanilla = new HashSet<string>(StringComparer.Ordinal)
        {
            "Koikano/create_eye", "Koikano/create_eyew", "Koikano/create_skinbody", "Koikano/create_skinface", "Koikano/create_topN",
            "Koikano/hair_main_low_sun", "Koikano/hair_main_sun", "Koikano/hair_main_sun_front", "Koikano/low_clothes_alpha",
            "Koikano/low_clothes_opaque", "Koikano/main_add", "Koikano/main_clothes_alpha", "Koikano/main_clothes_emblem",
            "Koikano/main_clothes_item", "Koikano/main_clothes_item_glasses", "Koikano/main_clothes_opaque", "Koikano/main_color",
            "Koikano/main_eye", "Koikano/main_eyew", "Koikano/main_nose", "Koikano/main_skin", "Koikano/sub_sp_yousei_butterfly",
            "Koikano/sub_texture_animation",
            "Shader Forge/AlphaMaskMultiply", "Shader Forge/FlatColorTransparent", "Shader Forge/create_body", "Shader Forge/create_eye",
            "Shader Forge/create_eyewhite", "Shader Forge/create_hair", "Shader Forge/create_head", "Shader Forge/create_topN",
            "Shader Forge/hair_07", "Shader Forge/hair_08", "Shader Forge/main_StandardMDK_studio", "Shader Forge/main_alpha",
            "Shader Forge/main_color", "Shader Forge/main_emblem", "Shader Forge/main_emblem_clothes", "Shader Forge/main_hair",
            "Shader Forge/main_hair_front", "Shader Forge/main_item", "Shader Forge/main_item_ditherd", "Shader Forge/main_item_emission",
            "Shader Forge/main_item_glasses", "Shader Forge/main_item_studio", "Shader Forge/main_item_studio_add",
            "Shader Forge/main_item_studio_alpha", "Shader Forge/main_opaque", "Shader Forge/main_opaque2", "Shader Forge/main_opaque_low",
            "Shader Forge/main_skin", "Shader Forge/main_skin_low", "Shader Forge/main_texture", "Shader Forge/mnpb",
            "Shader Forge/shadowcast", "Shader Forge/studio_hair_07", "Shader Forge/toon_eye_lod0", "Shader Forge/toon_eyew_lod0",
            "Shader Forge/toon_glasses_lod0", "Shader Forge/toon_nose_lod0", "Shader Forge/toon_textureanimation",
            // The low-detail variants mod items ship with (yamadamod.nohairB, clothVariation...): same
            // properties and look as their full-detail shaders.
            "Shader Forge/main_hair_low", "Shader Forge/main_item_low", "Shader Forge/main_opaque_low2",
            "Shader Forge/main_opaque_culloff",
        };

        private static readonly string[] Metal = { "_Metallic", "_Glossiness", "_GlossMapScale", "_Smoothness", "_SpecularHighlights", "_GlossyReflections" };
        private static readonly string[] MetalMaps = { "_MetallicGlossMap", "_SpecGlossMap", "_BumpMap", "_NormalMap", "_DetailNormalMap", "_ParallaxMap" };
        private static readonly string[] UtsOutline = { "_Outline_Width", "_OutlineWidth", "_Outline", "_Farthest_Distance" };
        private static readonly string[] UtsView = { "_MatCap_Sampler", "_NormalMapForMatCap", "_NormalMap", "_BumpMap", "_AngelRing_Sampler" };

        private static readonly Dictionary<string, ShaderFamily> cache = new Dictionary<string, ShaderFamily>(StringComparer.Ordinal);

        public static ShaderFamily Classify(string shader)
        {
            shader = shader ?? "";
            ShaderFamily f;
            lock (cache)
            {
                if (!cache.TryGetValue(shader, out f))
                {
                    f = Make(shader);
                    cache[shader] = f;
                }
            }
            return f;
        }

        private static bool Has(string s, string part)
        {
            return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ShaderFamily Make(string shader)
        {
            string leaf = shader.Substring(shader.LastIndexOf('/') + 1);
            if (Vanilla.Contains(shader))
            {
                return new ShaderFamily { Id = "kk", Modelled = true, Hair = Has(leaf, "hair") };
            }
            if (shader.StartsWith("xukmi/", StringComparison.Ordinal))
            {
                return new ShaderFamily { Id = "xukmi", Modelled = true, Hair = Has(leaf, "Hair") };
            }
            if (leaf.StartsWith("KKUTS", StringComparison.Ordinal) || shader.StartsWith("UnityChanToonShader/", StringComparison.Ordinal))
            {
                return new ShaderFamily
                {
                    Id = leaf.StartsWith("KKUTS", StringComparison.Ordinal) ? "kkuts" : "uts2",
                    Hair = Has(leaf, "hair"),
                    MainTextures = new[] { "_MainTex", "_BaseMap" },
                    MainColors = new[] { "_BaseColor", "_Color" },
                    ShadeColors = new[] { "_1st_ShadeColor", "_ShadowColor" },
                    NormalMaps = new[] { "_NormalMap", "_BumpMap" },
                    BakeZeroFloats = UtsOutline,
                    BakeClearTextures = UtsView,
                    HasOutline = !Has(leaf, "alpha") && !Has(leaf, "eye"),
                };
            }
            if (leaf.StartsWith("KKUSS", StringComparison.Ordinal))
            {
                return new ShaderFamily
                {
                    Id = "kkuss",
                    Hair = Has(leaf, "hair"),
                    MainColors = new[] { "_Color", "_BaseColor" },
                    BakeZeroFloats = new[] { "_outline", "_OutlineWidth", "_Outline_Width", "_Outline" },
                    BakeClearTextures = new[] { "_NormalMap", "_BumpMap" },
                    HasOutline = !Has(leaf, "alpha") && !Has(leaf, "item"),
                };
            }
            if (leaf.StartsWith("IBL_Shader", StringComparison.Ordinal))
            {
                return new ShaderFamily
                {
                    Id = "ibl",
                    BakeZeroFloats = new[] { "_Metallic", "_Glossiness", "_Outline" },
                    BakeClearTextures = MetalMaps,
                    BlendMode = Has(leaf, "alpha") ? "alpha" : Has(leaf, "cutoff") ? "cutout" : null,
                };
            }
            if (shader == "Standard" || shader.StartsWith("Standard ", StringComparison.Ordinal) || shader.StartsWith("Az/Standard", StringComparison.Ordinal) ||
                shader.StartsWith("Legacy Shaders/", StringComparison.Ordinal) || shader == "Autodesk Interactive" || shader.StartsWith("AIT/", StringComparison.Ordinal))
            {
                return new ShaderFamily { Id = "standard", BakeZeroFloats = Metal, BakeClearTextures = MetalMaps };
            }
            if (shader.StartsWith("Toony Colors Pro 2/", StringComparison.Ordinal) || shader.StartsWith("arktoon/", StringComparison.Ordinal))
            {
                return new ShaderFamily
                {
                    Id = "tcp2",
                    ShadeColors = new[] { "_SColor", "_ShadowColor", "_ShadeColor" },
                    BakeZeroFloats = new[] { "_Metallic", "_Glossiness", "_Outline", "_OutlineWidth" },
                    BakeClearTextures = MetalMaps,
                };
            }
            if (shader == "Rinfu/StockingShader")
            {
                return new ShaderFamily
                {
                    Id = "stocking",
                    HasOutline = true,
                    MainTextures = new[] { "_Maintex", "_MainTex" },
                    MainColors = new[] { "_StockingColorTint", "_Color" },
                    BakeZeroFloats = new[] { "_OutlineWidth" },
                };
            }
            if (shader == "Shader Forge/main_texture_studio")
            {
                return new ShaderFamily { Id = "kk_texture", Bake = BakeKind.Flat };
            }
            if (leaf == "heat_distortion_t2" || Has(leaf, "distortion") || Has(leaf, "Blur") || shader == "LTZ/RGB_Disturbance")
            {
                return new ShaderFamily { Id = "screen_effect", Bake = BakeKind.Invisible, BlendMode = "alpha" };
            }
            if (shader == "BetterMatcap" || shader.StartsWith("MatCap", StringComparison.Ordinal))
            {
                return new ShaderFamily { Id = "matcap", Bake = BakeKind.Flat, SolidFrom = new[] { "_Matcap", "_MatCap", "_MatCap_Sampler" } };
            }
            if (shader == "Goo")
            {
                return new ShaderFamily
                {
                    Id = "goo",
                    Bake = BakeKind.Flat,
                    BlendMode = "alpha",
                    MainTextures = new[] { "_Albedo", "_MainTex" },
                    MainColors = new[] { "_Color", "_Albedo" },
                    SolidFrom = new[] { "_Matcap" },
                };
            }
            if (shader.StartsWith("Fur/", StringComparison.Ordinal) || shader == "Az/Fur")
            {
                return new ShaderFamily { Id = "fur", Bake = BakeKind.Flat, MainTextures = new[] { "_MainTex", "_FurTex" } };
            }
            if (Has(leaf, "TextureScroll") || leaf == "DualScroll")
            {
                return new ShaderFamily { Id = "scroll", Bake = BakeKind.Flat };
            }
            if (shader.StartsWith("Particles/", StringComparison.Ordinal) || shader.StartsWith("Xffect/", StringComparison.Ordinal) ||
                shader.StartsWith("DA/", StringComparison.Ordinal) || shader.StartsWith("HOTATE/", StringComparison.Ordinal) ||
                shader.StartsWith("Unlit/Glitch", StringComparison.Ordinal) || shader == "FXHologram" || shader == "Space" ||
                shader == "Custom/AlphaDependingDistance" || shader.StartsWith("KriptoFX/", StringComparison.Ordinal))
            {
                return new ShaderFamily
                {
                    Id = "effect",
                    Bake = BakeKind.Flat,
                    BlendMode = "alpha",
                    MainColors = new[] { "_TintColor", "_Color", "_MainColor" },
                };
            }
            if (shader.StartsWith("Unlit/", StringComparison.Ordinal))
            {
                return new ShaderFamily { Id = "unlit", Bake = BakeKind.Flat };
            }
            return new ShaderFamily { Id = "unknown", Hair = Has(leaf, "hair") };
        }

        /// <summary>Whether materials of this family are reported as mapped (not modelled by KKBP).</summary>
        public static bool IsMapped(ShaderFamily f)
        {
            return !f.Modelled;
        }
    }
}
