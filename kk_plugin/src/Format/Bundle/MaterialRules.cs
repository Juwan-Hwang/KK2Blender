using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Game-independent rules for how a renderer's materials go into the bundle: Blend Modes, the
    /// Material Roles of slots past the first, and which renderers are never exported. Kept off Unity
    /// so they are unit tested.
    /// </summary>
    public static class MaterialRules
    {
        /// <summary>Unity's first alpha-tested and transparent render queues.</summary>
        public const int AlphaTestQueue = 2450;
        public const int TransparentQueue = 3000;

        private static readonly HashSet<string> GlassesShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shader Forge/toon_glasses_lod0", "Koikano/main_clothes_item_glasses",
        };

        /// <summary>Face overlays the game draws blended over the face (as KKBP's exporter marks them).</summary>
        private static readonly HashSet<string> OverlayRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            "eyebrow", "eyeline_upper", "eyeline_lower", "eyeline_shadow", "nose_line", "eye_l", "eye_r",
        };

        /// <summary>Renderers that are never part of the picture, by name: the shadow proxy and the "o_Mask"
        /// quad (AlphaMaskMultiply) the game puts on the head. (Mosaic and male-part stand-ins are left out by
        /// the game's own references.)</summary>
        private static readonly HashSet<string> NeverExported = new HashSet<string>(StringComparer.Ordinal)
        {
            "o_shadowcaster",
        };

        public static bool NeverExport(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName))
            {
                return false;
            }
            // Unity names spawned copies "o_Mask(Clone)".
            return NeverExported.Contains(rendererName) || rendererName == "o_Mask" || rendererName.StartsWith("o_Mask(", StringComparison.Ordinal);
        }

        /// <summary>
        /// A material's Blend Mode. Glasses shaders first, then shaders that blend by their name (every KK
        /// shader named "*alpha*" draws with alpha blending, whatever its queue: main_alpha,
        /// main_item_studio_alpha, KKUTSalpha...), then the face overlays, then the render queue.
        /// </summary>
        public static string BlendMode(string shader, int renderQueue, string role)
        {
            shader = shader ?? "";
            if (GlassesShaders.Contains(shader) || shader.IndexOf("glasses", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "glasses";
            }
            if (IsAlphaShader(shader) || renderQueue >= TransparentQueue)
            {
                return "alpha";
            }
            if (role != null && OverlayRoles.Contains(role))
            {
                return "alpha";
            }
            if (renderQueue >= AlphaTestQueue)
            {
                return "cutout";
            }
            return "opaque";
        }

        /// <summary>
        /// A material's Blend Mode with its shader family's rules first: a family that always draws one
        /// way (Goo, effect shaders, IBL_Shader_alpha), Unity Standard's rendering mode (<paramref name="standardMode"/>,
        /// its _Mode float: 0 opaque, 1 cutout, 2 fade, 3 transparent; NaN when the material has none), and
        /// clipping in the shader's name (UTS2 "_TransClipping", "Cutout", "Cutoff") as alpha-tested
        /// whatever its queue; then <see cref="BlendMode(string, int, string)"/>.
        /// </summary>
        public static string BlendMode(string shader, int renderQueue, string role, ShaderFamily family, float standardMode)
        {
            shader = shader ?? "";
            if (GlassesShaders.Contains(shader) || shader.IndexOf("glasses", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "glasses";
            }
            if (family != null && !family.Modelled)
            {
                if (family.BlendMode != null)
                {
                    return family.BlendMode;
                }
                if (family.Id == "standard" && !float.IsNaN(standardMode))
                {
                    int mode = (int)Math.Round(standardMode);
                    return mode == 1 ? "cutout" : mode >= 2 ? "alpha" : renderQueue >= TransparentQueue ? "alpha" : "opaque";
                }
                string leaf = shader.Substring(shader.LastIndexOf('/') + 1);
                if (renderQueue < TransparentQueue && !IsAlphaShader(shader) &&
                    (leaf.IndexOf("Clipping", StringComparison.OrdinalIgnoreCase) >= 0 || leaf.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     leaf.IndexOf("Cutoff", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return "cutout";
                }
                if (leaf.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 || leaf.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    leaf.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "alpha";
                }
            }
            return BlendMode(shader, renderQueue, role);
        }

        /// <summary>Shaders that blend by name. AlphaMask shaders only multiply a mask and are opaque.</summary>
        public static bool IsAlphaShader(string shader)
        {
            if (string.IsNullOrEmpty(shader))
            {
                return false;
            }
            string leaf = shader.Substring(shader.LastIndexOf('/') + 1);
            if (leaf.IndexOf("alphamask", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            return leaf.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static readonly Dictionary<string, string> KkTextureRoles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_MainTex"] = "main", ["_ColorMask"] = "color_mask", ["_NormalMap"] = "normal", ["_NormalMapDetail"] = "detail_normal",
            ["_AlphaMask"] = "alpha_mask", ["_LineMask"] = "line_mask", ["_DetailMask"] = "detail_mask",
            ["_overtex1"] = "overlay", ["_overtex2"] = "overlay", ["_overtex3"] = "overlay",
        };

        /// <summary>
        /// The Texture Role of a material's texture property, or null for a slot KKBP has no role for (it
        /// keeps its raw name). KK's own property names first; then, for a family KKBP doesn't model, its
        /// main texture (UTS's _BaseMap, Goo's _Albedo, Rinfu's _Maintex) and normal map (_BumpMap) when
        /// no earlier property took that role. <paramref name="taken"/> holds the roles already given on
        /// this material and is updated.
        /// </summary>
        public static string TextureRole(string property, ShaderFamily family, ICollection<string> taken)
        {
            string role;
            if (!KkTextureRoles.TryGetValue(property ?? "", out role))
            {
                role = null;
                if (family != null && !family.Modelled)
                {
                    if (Array.IndexOf(family.MainTextures, property) >= 0)
                    {
                        role = "main";
                    }
                    else if (Array.IndexOf(family.NormalMaps, property) >= 0)
                    {
                        role = "normal";
                    }
                }
            }
            if (role == null || role == "overlay")
            {
                return role;
            }
            if (taken != null)
            {
                if (taken.Contains(role))
                {
                    return null;
                }
                taken.Add(role);
            }
            return role;
        }

        /// <summary>
        /// The Material Role of an Outfit material: "hair" for the card's hair, and for an accessory that
        /// is hair (it carries a ChaCustomHairComponent, or a hair shader such as KKUTShair); none for
        /// the rest, which KKBP draws by Blend Mode.
        /// </summary>
        public static string OutfitRole(string owner, bool hairComponent, ShaderFamily family)
        {
            if (owner == null)
            {
                return null;
            }
            if (owner.StartsWith("hair", StringComparison.Ordinal))
            {
                return "hair";
            }
            if (owner.StartsWith("accessory", StringComparison.Ordinal) && (hairComponent || (family != null && family.Hair)))
            {
                return "hair";
            }
            return null;
        }

        /// <summary>
        /// Unity draws a renderer's material slot <paramref name="slot"/> with submesh min(slot, count - 1):
        /// materials past the last submesh draw the last submesh again, on top (Material Editor's copies).
        /// </summary>
        public static int Submesh(int slot, int submeshCount)
        {
            return Math.Max(0, Math.Min(slot, submeshCount - 1));
        }

        public static bool IsOverlayCopy(int slot, int submeshCount)
        {
            return submeshCount > 0 && slot >= submeshCount;
        }
    }
}
