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
