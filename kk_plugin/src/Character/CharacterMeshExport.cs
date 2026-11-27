using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KK2Blender.Format;
using KK2Blender.Format.Bundle;
using Studio;

namespace KK2Blender.Character
{
    /// <summary>
    /// Writes a KKBP Export Bundle for every Studio character into <c>&lt;export&gt;/meshes/</c>, inside
    /// CharaStudio, so the main game never has to run. Also writes <c>meshes/result.json</c> (the
    /// bundles in character order, as KKBP's batch result lists them) and returns the "kkbp" entry
    /// for scene.json. One character failing doesn't stop the others; it is listed under failures.
    /// </summary>
    internal sealed class CharacterMeshExport
    {
        private readonly string exportFolder;
        private readonly IList<OCIChar> characters;
        private readonly IList<PathTable> paths;
        private readonly Action<string> status;

        public CharacterMeshExport(string exportFolder, IList<OCIChar> characters, IList<PathTable> paths, Action<string> status)
        {
            this.exportFolder = exportFolder;
            this.characters = characters;
            this.paths = paths;
            this.status = status ?? (s => { });
        }

        public JsonObject Result { get; private set; }

        public IEnumerable Run()
        {
            string meshes = Path.Combine(exportFolder, "meshes");
            Directory.CreateDirectory(meshes);
            var bundles = new List<object>();
            var relative = new List<object>();
            var failures = new List<object>();
            var stamp = DateTime.Now;
            for (int i = 0; i < characters.Count; i++)
            {
                var oci = characters[i];
                string label = "Character meshes " + (i + 1) + "/" + characters.Count;
                status(label);
                var capture = new CharacterCapture(oci, paths != null && i < paths.Count ? paths[i] : null);
                capture.Status = s => status(label + ": " + s);
                IEnumerator steps = capture.Run();
                Exception error = null;
                while (true)
                {
                    object current;
                    try
                    {
                        if (!steps.MoveNext())
                        {
                            break;
                        }
                        current = steps.Current;
                    }
                    catch (Exception e)
                    {
                        error = e;
                        break;
                    }
                    yield return current;
                }
                string folder = null;
                if (error == null)
                {
                    try
                    {
                        status(label + ": writing");
                        var bundle = capture.Bundle;
                        // Same-named characters exported in one second get distinct folders.
                        string name = BundleWriter.FolderName(stamp.AddSeconds(i), bundle.Name);
                        folder = Path.Combine(meshes, name);
                        var writer = new BundleWriter();
                        writer.Write(bundle, folder);
                        foreach (var w in capture.Warnings)
                        {
                            Plugin.Log.LogWarning(bundle.Name + ": " + w);
                        }
                        foreach (var w in writer.Warnings)
                        {
                            Plugin.Log.LogWarning(bundle.Name + ": " + w);
                        }
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
                if (error != null)
                {
                    Plugin.Log.LogError("Character mesh export failed for " + SafeName(oci) + ": " + error);
                    failures.Add(new JsonObject().Set("index", i).Set("name", SafeName(oci)).Set("error", error.Message));
                    bundles.Add(null);
                    relative.Add(null);
                }
                else
                {
                    bundles.Add(folder);
                    relative.Add("meshes/" + Path.GetFileName(folder));
                }
                yield return null;
            }

            string resultPath = Path.Combine(meshes, "result.json");
            File.WriteAllText(resultPath, JsonWriter.Write(new JsonObject()
                .Set("exporter", Plugin.Name + " " + Plugin.Version)
                .Set("bundles", bundles)
                .Set("failures", failures)));
            Result = new JsonObject()
                .Set("exporter", "kk2blender")
                .Set("output", "meshes")
                .Set("result", resultPath)
                .Set("bundles", relative)
                .Set("state", failures.Count == 0 ? "exported" : "partly_exported")
                .Set("failures", failures);
        }

        private static string SafeName(OCIChar oci)
        {
            try
            {
                return oci.charInfo.chaFile.parameter.fullname;
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
