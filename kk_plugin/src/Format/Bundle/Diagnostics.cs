using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// The per-export self-check: what the exporter had to guess, map or leave out for each character,
    /// so a card that exports wrong shows why without a debugging session. Written as
    /// <c>diagnostics.json</c> and <c>diagnostics.txt</c> in the export folder and summarised in the
    /// BepInEx log.
    /// </summary>
    public sealed class Diagnostics
    {
        /// <summary>Issue kinds, in the order the summary lists them.</summary>
        public static readonly string[] Kinds =
        {
            "export_failed", "renderer_skipped", "dropped_weights", "late_bone", "implanted_bones", "bind_mismatch", "bone_count_mismatch",
            "bake_failed", "bake_fallback", "unreadable_texture", "missing_main_texture", "no_role", "me_copy",
            "property_block_texture", "blend_shape_weights",
        };

        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            ["export_failed"] = "export failed",
            ["renderer_skipped"] = "renderers left out",
            ["dropped_weights"] = "skin weights on bones that couldn't be found (dropped)",
            ["late_bone"] = "skin bones outside the recorded skeleton (added)",
            ["implanted_bones"] = "bones a mod implanted into the body skeleton (kept with their outfit)",
            ["bind_mismatch"] = "meshes whose bind pose differs from the body's",
            ["bone_count_mismatch"] = "renderers with more bones than bind poses",
            ["bake_failed"] = "light/dark bakes that failed",
            ["bake_fallback"] = "game bakes that came out empty (replaced by texture x colour)",
            ["unreadable_texture"] = "textures that couldn't be read",
            ["missing_main_texture"] = "mapped materials without a main texture",
            ["no_role"] = "head/body materials without a Material Role",
            ["me_copy"] = "Material Editor material copies (drawn over the last submesh)",
            ["property_block_texture"] = "textures set by a plugin through a property block",
            ["blend_shape_weights"] = "meshes exported with blend shapes set by a plugin",
        };

        public sealed class Issue
        {
            public string Kind, Path, Detail;
        }

        public sealed class ShaderUse
        {
            public string Shader, Family, Bake, BlendMode;
            public bool Modelled;
            public int Materials;
            public readonly List<string> Examples = new List<string>();
        }

        public sealed class Character
        {
            public string Name;
            public readonly List<Issue> Issues = new List<Issue>();
            public readonly Dictionary<string, ShaderUse> Shaders = new Dictionary<string, ShaderUse>(StringComparer.Ordinal);
            public int OutfitMaterialsWithoutRole;

            public void Add(string kind, string path, string detail)
            {
                Issues.Add(new Issue { Kind = kind, Path = path, Detail = detail });
            }

            public void Shader(string shader, ShaderFamily family, string blendMode, string material)
            {
                string key = (shader ?? "") + "|" + blendMode;
                ShaderUse use;
                if (!Shaders.TryGetValue(key, out use))
                {
                    use = new ShaderUse
                    {
                        Shader = shader ?? "",
                        Family = family.Id,
                        Modelled = family.Modelled,
                        Bake = family.Bake.ToString().ToLowerInvariant(),
                        BlendMode = blendMode,
                    };
                    Shaders[key] = use;
                }
                use.Materials++;
                if (use.Examples.Count < 5 && material != null && !use.Examples.Contains(material))
                {
                    use.Examples.Add(material);
                }
            }

            public int Count(string kind)
            {
                int n = 0;
                foreach (var i in Issues)
                {
                    if (i.Kind == kind)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public readonly List<Character> Characters = new List<Character>();
        public string Exporter;

        public Character Begin(string name)
        {
            var c = new Character { Name = name ?? "" };
            Characters.Add(c);
            return c;
        }

        public JsonObject ToJson()
        {
            var chars = new List<object>();
            foreach (var c in Characters)
            {
                var shaders = new List<object>();
                foreach (var u in Sorted(c))
                {
                    shaders.Add(new JsonObject()
                        .Set("shader", u.Shader).Set("family", u.Family).Set("modelled_by_kkbp", u.Modelled)
                        .Set("bake", u.Bake).Set("blend_mode", u.BlendMode).Set("materials", u.Materials)
                        .Set("examples", new List<object>(u.Examples.ConvertAll(x => (object)x))));
                }
                var counts = new JsonObject();
                foreach (var k in Kinds)
                {
                    int n = c.Count(k);
                    if (n > 0)
                    {
                        counts.Set(k, n);
                    }
                }
                var issues = new List<object>();
                foreach (var i in c.Issues)
                {
                    issues.Add(new JsonObject().Set("kind", i.Kind).Set("path", i.Path).Set("detail", i.Detail));
                }
                chars.Add(new JsonObject()
                    .Set("name", c.Name)
                    .Set("counts", counts)
                    .Set("outfit_materials_without_role", c.OutfitMaterialsWithoutRole)
                    .Set("shaders", shaders)
                    .Set("issues", issues));
            }
            return new JsonObject()
                .Set("format", "kk2blender-diagnostics")
                .Set("version", 1)
                .Set("exporter", Exporter ?? "")
                .Set("characters", chars);
        }

        private static List<ShaderUse> Sorted(Character c)
        {
            var list = new List<ShaderUse>(c.Shaders.Values);
            list.Sort((a, b) =>
            {
                int m = a.Modelled.CompareTo(b.Modelled);
                if (m != 0)
                {
                    return m; // mapped (not modelled) first
                }
                int n = b.Materials.CompareTo(a.Materials);
                return n != 0 ? n : string.CompareOrdinal(a.Shader, b.Shader);
            });
            return list;
        }

        /// <summary>A short plain-text summary: per character, the mapped shaders and the issue counts.</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("KK2Blender export self-check");
            if (!string.IsNullOrEmpty(Exporter))
            {
                sb.Append(" (").Append(Exporter).Append(')');
            }
            sb.Append('\n');
            if (Characters.Count == 0)
            {
                sb.Append("No characters exported.\n");
            }
            foreach (var c in Characters)
            {
                sb.Append('\n').Append(c.Name.Length > 0 ? c.Name : "(unnamed)").Append('\n');
                var mapped = Sorted(c).FindAll(u => !u.Modelled);
                if (mapped.Count == 0)
                {
                    sb.Append("  all shaders are modelled by KKBP\n");
                }
                else
                {
                    sb.Append("  shaders KKBP doesn't model, and how they were mapped:\n");
                    foreach (var u in mapped)
                    {
                        sb.Append("    ").Append(u.Shader.Length > 0 ? u.Shader : "(none)").Append(": family ").Append(u.Family)
                            .Append(", ").Append(u.Bake).Append(" bake, ").Append(u.BlendMode).Append(", ")
                            .Append(u.Materials.ToString(CultureInfo.InvariantCulture)).Append(u.Materials == 1 ? " material\n" : " materials\n");
                    }
                }
                bool any = false;
                foreach (var k in Kinds)
                {
                    int n = c.Count(k);
                    if (n == 0)
                    {
                        continue;
                    }
                    any = true;
                    sb.Append("  ").Append(n.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Titles[k]);
                    var first = c.Issues.Find(i => i.Kind == k);
                    if (first != null && !string.IsNullOrEmpty(first.Path))
                    {
                        sb.Append(" (e.g. ").Append(first.Path);
                        if (!string.IsNullOrEmpty(first.Detail))
                        {
                            sb.Append(": ").Append(first.Detail);
                        }
                        sb.Append(')');
                    }
                    sb.Append('\n');
                }
                if (!any)
                {
                    sb.Append("  no issues found\n");
                }
            }
            return sb.ToString();
        }
    }
}
