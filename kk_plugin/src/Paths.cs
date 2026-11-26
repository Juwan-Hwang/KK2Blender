using System.Collections.Generic;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Stable names for the transforms under a root, kept across frames. A transform's name is its
    /// path below the root; siblings with the same path get "#2", "#3" in the order first seen, so two
    /// accessories with identical bone names stay apart. Names are cached per Transform, because
    /// building strings for every bone every frame would dominate the bake.
    /// </summary>
    internal sealed class PathTable
    {
        private readonly Transform root;
        private readonly Dictionary<Transform, string> names = new Dictionary<Transform, string>();
        private readonly Dictionary<string, int> uses = new Dictionary<string, int>();

        public PathTable(Transform root)
        {
            this.root = root;
        }

        public Transform Root
        {
            get { return root; }
        }

        public string Name(Transform t)
        {
            string name;
            if (names.TryGetValue(t, out name))
            {
                return name;
            }
            string path = Space.PathBelow(t, root);
            int n;
            uses.TryGetValue(path, out n);
            // A transform that replaced a destroyed one with the same path (an outfit reload) takes
            // over its name, so the channel continues instead of starting a "#2" copy.
            bool replaced = false;
            if (n > 0)
            {
                foreach (var kv in names)
                {
                    if (kv.Key == null && kv.Value == path)
                    {
                        replaced = true;
                        break;
                    }
                }
            }
            if (replaced)
            {
                name = path;
                RemoveDestroyed(path);
            }
            else
            {
                uses[path] = n + 1;
                name = n == 0 ? path : path + "#" + (n + 1);
            }
            names[t] = name;
            return name;
        }

        private void RemoveDestroyed(string name)
        {
            Transform dead = null;
            foreach (var kv in names)
            {
                // Unity's == null is true for destroyed objects.
                if (kv.Key == null && kv.Value == name)
                {
                    dead = kv.Key;
                    break;
                }
            }
            if (!ReferenceEquals(dead, null))
            {
                names.Remove(dead);
            }
        }
    }
}
