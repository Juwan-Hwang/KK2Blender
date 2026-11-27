using System;
using System.Collections.Generic;
using System.Globalization;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// KK's expression translation table: the contract's Expression Names for the game's eye-set,
    /// eyebrow and mouth patterns by position, and the recipes for expressions that are not game patterns.
    /// This is contract data (KKBP contract/expressions/kk.json, schema 1.3, MIT), kept as data rows.
    /// </summary>
    public static class KkExpressionTable
    {
        public sealed class Pattern
        {
            public string List;
            public int Index;
            public string Name;
            public string ClosedName;
        }

        public sealed class Recipe
        {
            public string Name;
            public readonly List<KeyValuePair<string, float>> Shapes = new List<KeyValuePair<string, float>>();
        }

        // list|index|name|closed name
        private static readonly string[] PatternRows =
        {
            "eye|0|Eye Default|",
            "eye|1|Eye Closed|",
            "eye|2|Eye Smiling|",
            "eye|3|Eye Happy, Closed|",
            "eye|4|Eye Happy|",
            "eye|5|Eye Winking L|",
            "eye|6|Eye Winking R|",
            "eye|7|Eye Pained|",
            "eye|8|Eye Bashful|",
            "eye|9|Eye Angry|",
            "eye|10|Eye Serious|",
            "eye|11|Eye Bored|",
            "eye|12|Eye Awkward|",
            "eye|13|Eye Hate|",
            "eye|14|Eye Thinking|",
            "eye|15|Eye Sad|",
            "eye|16|Eye Crying|",
            "eye|17|Eye Impatient|",
            "eye|18|Eye Disappointed|",
            "eye|19|Eye Worried|",
            "eye|20|Eye Smug|",
            "eye|21|Gag Circle Eyes 1|",
            "eye|22|Gag Circle Eyes 2|",
            "eye|23|Gag Spiral Eyes|",
            "eye|24|Eye Star Pupils|",
            "eye|25|Eye Heart Pupils|",
            "eye|26|Gag Heart Eyes|",
            "eye|27|Gag Fiery Eyes|",
            "eye|28|Gag Cartoony Wink|",
            "eye|29|Gag Vertical Line|",
            "eye|30|Gag Deformed Tsumuri|",
            "eye|31|Gag Horizontal Line|",
            "eye|32|Gag Cartoony Crying|",
            "eye|33|Eye Left Eye Shut Tight|",
            "eye|34|Eye Right Eye Shut Tight|",
            "eye|35|Eye Both Eyes Shut Tight|",
            "eye|36|Eye Left Eye Shut Tight 2|",
            "eye|37|Eye Right Eye Shut Tight 2|",
            "eye|38|Eye Both Eyes Shut Tight 2|",
            "eyebrow|0|Eyebrow Default|",
            "eyebrow|1|Eyebrow Angry|",
            "eyebrow|2|Eyebrow Worried|",
            "eyebrow|3|Eyebrow Bored|",
            "eyebrow|4|Eyebrow Doubt L|",
            "eyebrow|5|Eyebrow Doubt R|",
            "eyebrow|6|Eyebrow Thinking L|",
            "eyebrow|7|Eyebrow Thinking R|",
            "eyebrow|8|Eyebrow Furious L|",
            "eyebrow|9|Eyebrow Furious R|",
            "eyebrow|10|Eyebrow Serious|",
            "eyebrow|11|Eyebrow Anxious|",
            "eyebrow|12|Eyebrow Surprised|",
            "eyebrow|13|Eyebrow Disappointed|",
            "eyebrow|14|Eyebrow Smug|",
            "eyebrow|15|Eyebrow Winking L|",
            "eyebrow|16|Eyebrow Winking R|",
            "mouth|0|Mouth Default|Mouth Default, Closed",
            "mouth|1|Mouth Smiling|Mouth Smiling, Closed",
            "mouth|2|Mouth Happy (Broad)|Mouth Happy (Broad), Closed",
            "mouth|3|Mouth Happy (Moderate)|Mouth Happy (Moderate), Closed",
            "mouth|4|Mouth Happy (Slight)|Mouth Happy (Slight), Closed",
            "mouth|5|Mouth Excited (Broad)|Mouth Excited (Broad), Closed",
            "mouth|6|Mouth Excited (Moderate)|Mouth Excited (Moderate), Closed",
            "mouth|7|Mouth Excited (Slight)|Mouth Excited (Slight), Closed",
            "mouth|8|Mouth Angry|Mouth Angry, Closed",
            "mouth|9|Mouth Angry 2|Mouth Angry 2, Closed",
            "mouth|10|Mouth Serious 1|Mouth Serious 1, Closed",
            "mouth|11|Mouth Serious 2|Mouth Serious 2, Closed",
            "mouth|12|Mouth Hate|Mouth Hate, Closed",
            "mouth|13|Mouth Lonely|Mouth Lonely, Closed",
            "mouth|14|Mouth Impatient|Mouth Impatient, Closed",
            "mouth|15|Mouth Dissatisfied|Mouth Dissatisfied, Closed",
            "mouth|16|Mouth Amazed|Mouth Amazed, Closed",
            "mouth|17|Mouth Surprised|Mouth Surprised, Closed",
            "mouth|18|Mouth Surprised (Moderate)|Mouth Surprised (Moderate), Closed",
            "mouth|19|Mouth Smug|Mouth Smug, Closed",
            "mouth|20|Mouth Playful|Mouth Playful, Closed",
            "mouth|21|Mouth Eating|Mouth Eating, Closed",
            "mouth|22|Mouth Hold In Mouth|Mouth Hold In Mouth, Closed",
            "mouth|23|Mouth Kiss|Mouth Kiss, Closed",
            "mouth|24|Mouth Tongue Out|Mouth Tongue Out, Closed",
            "mouth|25|Mouth Small A|Mouth Small A, Closed",
            "mouth|26|Mouth Big A|Mouth Big A, Closed",
            "mouth|27|Mouth Small I|Mouth Small I, Closed",
            "mouth|28|Mouth Big I|Mouth Big I, Closed",
            "mouth|29|Mouth Small U|Mouth Small U, Closed",
            "mouth|30|Mouth Big U|Mouth Big U, Closed",
            "mouth|31|Mouth Small E|Mouth Small E, Closed",
            "mouth|32|Mouth Big E|Mouth Big E, Closed",
            "mouth|33|Mouth Small O|Mouth Small O, Closed",
            "mouth|34|Mouth Big O|Mouth Big O, Closed",
            "mouth|35|Mouth Small N|Mouth Small N, Closed",
            "mouth|36|Mouth Big N|Mouth Big N, Closed",
            "mouth|37|Mouth Catlike|Mouth Catlike, Closed",
            "mouth|38|Mouth Triangle|Mouth Triangle, Closed",
            "mouth|39|Mouth Cartoony Smile|Mouth Cartoony Smile, Closed",
        };

        // name|shape=weight;shape=weight
        private static readonly string[] RecipeRows =
        {
            "Mouth Chewing|kuti_face.f00_mogu_op=1",
            "Mouth Chewing, Closed|kuti_face.f00_mogu_cl=1",
            "Mouth Eating 2|kuti_face.f00_tabe_op=1;kuti_ha.ha00_def_op=1;kuti_yaeba.y00_def_op=1;kuti_sita.t00_def_op=1",
        };

        private static Dictionary<string, Pattern> patterns;
        private static List<Recipe> recipes;

        /// <summary>The table entry for a list ("eye", "eyebrow", "mouth") and 0-based position, or null.</summary>
        public static Pattern Find(string list, int index)
        {
            if (patterns == null)
            {
                var d = new Dictionary<string, Pattern>();
                foreach (var row in PatternRows)
                {
                    var f = row.Split('|');
                    var p = new Pattern
                    {
                        List = f[0],
                        Index = int.Parse(f[1], CultureInfo.InvariantCulture),
                        Name = f[2],
                        ClosedName = f[3].Length > 0 ? f[3] : null,
                    };
                    d[p.List + "|" + p.Index] = p;
                }
                patterns = d;
            }
            Pattern found;
            return patterns.TryGetValue(list + "|" + index, out found) ? found : null;
        }

        public static IList<Recipe> Recipes
        {
            get
            {
                if (recipes == null)
                {
                    var list = new List<Recipe>();
                    foreach (var row in RecipeRows)
                    {
                        var f = row.Split('|');
                        var r = new Recipe { Name = f[0] };
                        foreach (var s in f[1].Split(';'))
                        {
                            int eq = s.LastIndexOf('=');
                            r.Shapes.Add(new KeyValuePair<string, float>(s.Substring(0, eq), float.Parse(s.Substring(eq + 1), CultureInfo.InvariantCulture)));
                        }
                        list.Add(r);
                    }
                    recipes = list;
                }
                return recipes;
            }
        }

        /// <summary>The expression category of an Expression Name: its prefix.</summary>
        public static string CategoryOf(string name)
        {
            if (name == null)
            {
                return "other";
            }
            if (name.StartsWith("Eyebrow", StringComparison.Ordinal))
            {
                return "eyebrow";
            }
            if (name.StartsWith("Eye", StringComparison.Ordinal))
            {
                return "eye";
            }
            if (name.StartsWith("Mouth", StringComparison.Ordinal))
            {
                return "mouth";
            }
            if (name.StartsWith("Gag", StringComparison.Ordinal))
            {
                return "gag";
            }
            if (name.StartsWith("Tears", StringComparison.Ordinal))
            {
                return "tears";
            }
            return "other";
        }
    }
}
