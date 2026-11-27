using System;
using System.Collections.Generic;
using System.IO;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Writes the loaded Studio map from the running game: one .glb per top-level part of the map,
    /// in the map root's space, with its baked lightmaps (Unity RGBM) on a second UV set. The map's
    /// own lights are in the bake (scene tracks, owner "map"). Only what is shown is written, which
    /// follows the map's option toggle and time of day as they are at export.
    /// </summary>
    internal static class MapExporter
    {
        public static List<object> Export(string folder, List<string> warnings)
        {
            var parts = new List<object>();
            var map = Singleton<Map>.Instance;
            if (map == null || map.mapRoot == null)
            {
                return parts;
            }
            string dir = Path.Combine(folder, "map");
            Directory.CreateDirectory(dir);
            var root = map.mapRoot.transform;
            var exporter = new PropExporter();
            // The map root's own renderers, then each child as its own file, so a big map isn't one huge file.
            var groups = new List<Transform>();
            foreach (Transform child in root)
            {
                if (child.gameObject.activeInHierarchy)
                {
                    groups.Add(child);
                }
            }
            if (groups.Count == 0)
            {
                groups.Add(root);
            }
            for (int i = 0; i < groups.Count; i++)
            {
                byte[] glb;
                try
                {
                    glb = exporter.Export(groups[i], true, false);
                }
                catch (Exception e)
                {
                    warnings.Add("map part " + groups[i].name + ": " + e.Message);
                    continue;
                }
                if (glb == null)
                {
                    continue;
                }
                string name = "part_" + i.ToString("000") + ".glb";
                File.WriteAllBytes(Path.Combine(dir, name), glb);
                // The part's file is in the map root's space only when the group is the root; otherwise
                // it is in the child's space, placed by this matrix (relative to the map root).
                var m = root.worldToLocalMatrix * groups[i].localToWorldMatrix;
                parts.Add(new JsonObject()
                    .Set("name", groups[i].name)
                    .Set("file", "map/" + name)
                    .Set("matrix", Space.Matrix(m)));
            }
            warnings.AddRange(exporter.Warnings);
            return parts;
        }
    }
}
