using System.Collections.Generic;
using System.Text;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// Turns keys into contract Ids for one kind (meshes, materials or bones each have their own table):
    /// the game name reduced to <c>A-Z a-z 0-9 _ . -</c>, runs of '_' joined, no '_' at either end,
    /// the kind's prefix when nothing is left, and "_2", "_3", ... on a clash, at most 63 bytes. The same
    /// key always gives the same Id, and the Ids follow the order keys arrive in.
    /// </summary>
    public sealed class IdTable
    {
        public const int MaxLength = 63;

        private readonly string prefix;
        private readonly Dictionary<string, string> byKey = new Dictionary<string, string>();
        private readonly HashSet<string> used = new HashSet<string>();

        public IdTable(string prefix)
        {
            this.prefix = prefix;
        }

        public bool TryGet(string key, out string id)
        {
            return byKey.TryGetValue(key, out id);
        }

        public string Get(string key)
        {
            string id;
            if (!byKey.TryGetValue(key, out id))
            {
                throw new KeyNotFoundException("No Id was made for key " + key);
            }
            return id;
        }

        public string Make(string key, string name)
        {
            string id;
            if (byKey.TryGetValue(key, out id))
            {
                return id;
            }
            string stem = Clean(name);
            if (stem.Length == 0)
            {
                stem = prefix;
            }
            id = Fit(stem, "");
            for (int n = 2; used.Contains(id); n++)
            {
                id = Fit(stem, "_" + n);
            }
            used.Add(id);
            byKey[key] = id;
            return id;
        }

        private static string Fit(string stem, string counter)
        {
            int room = MaxLength - counter.Length;
            if (stem.Length > room)
            {
                stem = stem.Substring(0, room).TrimEnd('_');
            }
            return stem + counter;
        }

        public static string Clean(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-';
                char d = ok ? c : '_';
                if (d == '_' && (sb.Length == 0 || sb[sb.Length - 1] == '_'))
                {
                    continue;
                }
                sb.Append(d);
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == '_')
            {
                sb.Length--;
            }
            return sb.ToString();
        }
    }
}
