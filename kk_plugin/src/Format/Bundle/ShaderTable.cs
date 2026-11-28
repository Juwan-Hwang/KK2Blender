using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Which properties to read from a material. KK's Unity 5.6 can't list a shader's properties, so
    /// they come from tables: per shader name, each property's kind ('c' colour, 'f' float, 't'
    /// texture, 'v' vector). The capture asks the material whether it has each one (HasProperty), so a
    /// table only has to name enough; a property the material lacks costs nothing.
    /// <para>
    /// For a shader the tables list, its own properties are read; for one they don't, the union of
    /// every property any table names (a property listed with different kinds takes its most common
    /// kind). Either way the "anywhere" properties (the table entry "": the Material Editor property
    /// names seen on cards) and <see cref="Builtin"/> (KK's character shader properties) are added.
    /// </para>
    /// </summary>
    public sealed class ShaderTable
    {
        public struct Property
        {
            public string Name;
            public char Kind;

            public override string ToString()
            {
                return Name + ":" + Kind;
            }
        }

        /// <summary>KK's character shader properties, read wherever the material has them.</summary>
        public static readonly Property[] Builtin = MakeBuiltin();

        private readonly Dictionary<string, Dictionary<string, char>> shaders = new Dictionary<string, Dictionary<string, char>>(StringComparer.Ordinal);
        private readonly Dictionary<string, char> anywhere = new Dictionary<string, char>(StringComparer.Ordinal);
        private List<Property> union;
        private readonly Dictionary<string, List<Property>> cache = new Dictionary<string, List<Property>>(StringComparer.Ordinal);

        public int ShaderCount
        {
            get { return shaders.Count; }
        }

        public static char Kind(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "c": case "color": case "colour": return 'c';
                case "t": case "texture": case "tex": case "2d": case "cube": case "3d": return 't';
                case "v": case "vector": return 'v';
                default: return 'f';
            }
        }

        /// <summary>Adds a table in the KKBP shader-properties.json format: {"shader": {"_Prop": "c"}}.
        /// A shader already listed keeps the kinds it has and gains the new properties.</summary>
        public void Load(string json)
        {
            var root = JsonReader.Parse(json) as Dictionary<string, object>;
            if (root == null)
            {
                throw new FormatException("A shader property table must be a JSON object");
            }
            foreach (var shader in root)
            {
                var props = shader.Value as Dictionary<string, object>;
                if (props == null)
                {
                    continue;
                }
                foreach (var p in props)
                {
                    Add(shader.Key, p.Key, Kind(p.Value as string));
                }
            }
        }

        public void Add(string shader, string property, char kind)
        {
            if (string.IsNullOrEmpty(property))
            {
                return;
            }
            Dictionary<string, char> props;
            if (string.IsNullOrEmpty(shader))
            {
                props = anywhere;
            }
            else if (!shaders.TryGetValue(shader, out props))
            {
                props = new Dictionary<string, char>(StringComparer.Ordinal);
                shaders[shader] = props;
            }
            if (!props.ContainsKey(property))
            {
                props[property] = kind;
            }
            union = null;
            cache.Clear();
        }

        public bool Knows(string shader)
        {
            return shader != null && shaders.ContainsKey(shader);
        }

        /// <summary>The properties to try on a material with this shader, each name once, sorted by name.</summary>
        public List<Property> For(string shader)
        {
            shader = shader ?? "";
            List<Property> result;
            if (cache.TryGetValue(shader, out result))
            {
                return result;
            }
            var kinds = new Dictionary<string, char>(StringComparer.Ordinal);
            Dictionary<string, char> own;
            if (shaders.TryGetValue(shader, out own))
            {
                foreach (var kv in own)
                {
                    kinds[kv.Key] = kv.Value;
                }
            }
            else
            {
                foreach (var p in Union())
                {
                    kinds[p.Name] = p.Kind;
                }
            }
            foreach (var kv in anywhere)
            {
                if (!kinds.ContainsKey(kv.Key))
                {
                    kinds[kv.Key] = kv.Value;
                }
            }
            foreach (var p in Builtin)
            {
                if (!kinds.ContainsKey(p.Name))
                {
                    kinds[p.Name] = p.Kind;
                }
            }
            result = new List<Property>(kinds.Count);
            foreach (var kv in kinds)
            {
                result.Add(new Property { Name = kv.Key, Kind = kv.Value });
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            cache[shader] = result;
            return result;
        }

        private List<Property> Union()
        {
            if (union != null)
            {
                return union;
            }
            var votes = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var shader in shaders.Values)
            {
                foreach (var kv in shader)
                {
                    int[] v;
                    if (!votes.TryGetValue(kv.Key, out v))
                    {
                        v = new int[4];
                        votes[kv.Key] = v;
                    }
                    v[Index(kv.Value)]++;
                }
            }
            union = new List<Property>(votes.Count);
            foreach (var kv in votes)
            {
                int best = 0;
                for (int i = 1; i < 4; i++)
                {
                    if (kv.Value[i] > kv.Value[best])
                    {
                        best = i;
                    }
                }
                union.Add(new Property { Name = kv.Key, Kind = "fctv"[best] });
            }
            return union;
        }

        private static int Index(char kind)
        {
            switch (kind)
            {
                case 'c': return 1;
                case 't': return 2;
                case 'v': return 3;
                default: return 0;
            }
        }

        private static Property[] MakeBuiltin()
        {
            var list = new List<Property>();
            foreach (var p in new[]
            {
                "_MainTex", "_ColorMask", "_NormalMap", "_NormalMapDetail", "_AlphaMask", "_LineMask", "_DetailMask",
                "_overtex1", "_overtex2", "_overtex3", "_expression", "_HairGloss", "_AnotherRamp", "_liquidmask",
                "_Texture2", "_Texture3", "_PatternMask1", "_PatternMask2", "_PatternMask3", "_PatternMask4", "_GlassRamp",
            })
            {
                list.Add(new Property { Name = p, Kind = 't' });
            }
            foreach (var p in new[]
            {
                "_Color", "_Color2", "_Color3", "_Color4", "_Color1_2", "_Color2_2", "_Color3_2", "_ShadowColor", "_shadowcolor",
                "_SpecularColor", "_LineColor", "_EmissionColor", "_overcolor1", "_overcolor2", "_overcolor3", "_GlossColor",
                "_LiquidTiling", "_Color5", "_Color6", "_Color7",
            })
            {
                list.Add(new Property { Name = p, Kind = 'c' });
            }
            foreach (var p in new[]
            {
                "_SpecularPower", "_SpecularPowerNail", "_alpha_a", "_alpha_b", "_rimpower", "_rimV", "_ShadowExtend",
                "_ShadowExtendAnother", "_DetailBLineG", "_DetailRLineR", "_notusetexspecular", "_nip", "_nipsize", "_nip_specular",
                "_exppower", "_isHighLight", "_linetexon", "_liquidftop", "_liquidfbot", "_liquidbtop", "_liquidbbot", "_liquidface",
                "_SizeSpeed", "_SizeWidth", "_AngleSpeed", "_yurayura", "_rotation", "_Cutoff", "_tex1mask", "_LineWidthS",
                "_DetailNormalMapScale", "_EmissionPower", "_alpha", "_Alpha", "_reverse", "_hokuro", "_Blend",
            })
            {
                list.Add(new Property { Name = p, Kind = 'f' });
            }
            foreach (var p in new[] { "_TileAnimation", "_HighLight", "_paint1", "_paint2" })
            {
                list.Add(new Property { Name = p, Kind = 'v' });
            }
            return list.ToArray();
        }
    }
}
