using System.Collections.Generic;
using KK2Blender.Format;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Records, every frame, the material values the game changes while a scene plays: blush
    /// (_overcolor2 alpha on the face), skin gloss, gag-eye texture frames and their animation,
    /// eye highlight and pupil offsets, colours set by Timeline or plugins, and which texture each
    /// swappable slot holds. Unity 5.6 can't list a shader's properties, so a fixed list of the
    /// properties KK's character shaders use is read where the material has them. Channel
    /// "mat:&lt;renderer path&gt;:&lt;slot&gt;"; its layout is in the track header ("materials").
    /// </summary>
    internal sealed class MaterialSampler
    {
        public static readonly string[] Colors =
        {
            "_Color", "_Color2", "_Color3", "_overcolor1", "_overcolor2", "_overcolor3", "_ShadowColor",
            "_shadowcolor", "_SpecularColor", "_EmissionColor", "_LineColor", "_LineColorG", "_GlossColor",
        };

        public static readonly string[] Floats =
        {
            "_alpha", "_Alpha", "_AngleSpeed", "_SizeSpeed", "_SpecularPower", "_SpecularPowerNail", "_rimpower",
            "_rimV", "_ShadowExtend", "_linetexon", "_tex1mask", "_nip", "_nipsize", "_DetailNormalMapScale",
            "_EmissionPower", "_LineWidthS", "_isHighLight", "_exppower", "_rotation", "_Cutoff",
        };

        public static readonly string[] Vectors = { "_TileAnimation", "_ColorMask", "_HighLight" };

        public static readonly string[] Textures = { "_MainTex", "_overtex1", "_overtex2", "_overtex3", "_expression" };

        private sealed class Layout
        {
            public readonly List<string> colors = new List<string>();
            public readonly List<string> floats = new List<string>();
            public readonly List<string> vectors = new List<string>();
            public readonly List<string> textures = new List<string>();
            public int width;
            public Channel channel;
            public readonly List<string>[] textureNames;

            public Layout(Material m)
            {
                foreach (var p in Colors) if (m.HasProperty(p)) colors.Add(p);
                foreach (var p in Floats) if (m.HasProperty(p)) floats.Add(p);
                foreach (var p in Vectors) if (m.HasProperty(p)) vectors.Add(p);
                foreach (var p in Textures) if (m.HasProperty(p)) textures.Add(p);
                // colour 4 + float 1 + vector 4 + texture (index of its name, offset xy, scale xy) 5
                width = colors.Count * 4 + floats.Count + vectors.Count * 4 + textures.Count * 5;
                textureNames = new List<string>[textures.Count];
                for (int i = 0; i < textureNames.Length; i++)
                {
                    textureNames[i] = new List<string>();
                }
            }
        }

        private readonly Dictionary<Material, Layout> layouts = new Dictionary<Material, Layout>();
        private readonly JsonObject header = new JsonObject();

        public void Sample(TrackSet tracks, int frame, Renderer r, string path)
        {
            var mats = r.sharedMaterials;
            for (int slot = 0; slot < mats.Length; slot++)
            {
                var m = mats[slot];
                if (m == null)
                {
                    continue;
                }
                Layout layout;
                if (!layouts.TryGetValue(m, out layout))
                {
                    layout = new Layout(m);
                    layouts[m] = layout;
                }
                if (layout.width == 0)
                {
                    continue;
                }
                string name = "mat:" + path + ":" + slot;
                if (layout.channel == null || layout.channel.Name != name)
                {
                    layout.channel = tracks.Get(name, "material", layout.width);
                }
                var record = new float[layout.width];
                int at = 0;
                foreach (var p in layout.colors)
                {
                    var c = m.GetColor(p);
                    record[at++] = c.r;
                    record[at++] = c.g;
                    record[at++] = c.b;
                    record[at++] = c.a;
                }
                foreach (var p in layout.floats)
                {
                    record[at++] = m.GetFloat(p);
                }
                foreach (var p in layout.vectors)
                {
                    var v = m.GetVector(p);
                    record[at++] = v.x;
                    record[at++] = v.y;
                    record[at++] = v.z;
                    record[at++] = v.w;
                }
                for (int t = 0; t < layout.textures.Count; t++)
                {
                    var p = layout.textures[t];
                    var tex = m.GetTexture(p);
                    string texName = tex != null ? tex.name : "";
                    int index = layout.textureNames[t].IndexOf(texName);
                    if (index < 0)
                    {
                        layout.textureNames[t].Add(texName);
                        index = layout.textureNames[t].Count - 1;
                    }
                    var o = m.GetTextureOffset(p);
                    var s = m.GetTextureScale(p);
                    record[at++] = index;
                    record[at++] = o.x;
                    record[at++] = o.y;
                    record[at++] = s.x;
                    record[at++] = s.y;
                }
                if (layout.channel.Count > 0 && layout.channel.Start + layout.channel.Count > frame)
                {
                    continue; // a material shared by two renderers at this path is recorded once
                }
                layout.channel.Put(frame, record);
                if (!header.Contains(name))
                {
                    header.Add(name, new JsonObject()
                        .Set("material", m.name)
                        .Set("shader", m.shader != null ? m.shader.name : "")
                        .Set("colors", layout.colors)
                        .Set("floats", layout.floats)
                        .Set("vectors", layout.vectors)
                        .Set("textures", layout.textures)
                        .Set("texture_names", layout.textureNames));
                }
            }
        }

        public JsonObject Header()
        {
            return header;
        }
    }
}
