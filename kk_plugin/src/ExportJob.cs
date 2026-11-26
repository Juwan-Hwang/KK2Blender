using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// One Export All, as a coroutine:
    /// 1. make the export folder and save each character's card;
    /// 2. describe the scene (scene.json, written last, once the bake is complete);
    /// 3. bake: hold frame 0 while physics settles, then play the Timeline with a fixed frame rate
    ///    (Time.captureFramerate) and sample every object at the end of each frame;
    /// 4. write the track files, and ask KKBP to export the character meshes.
    /// Everything it changes (frame rate, physics update rates, Timeline state) is put back.
    /// </summary>
    internal sealed class ExportJob
    {
        public const int FormatVersion = 1;

        private readonly Plugin plugin;
        private bool cancel;
        private int savedCaptureFramerate;
        private readonly List<KeyValuePair<DynamicBone, float>> savedRates = new List<KeyValuePair<DynamicBone, float>>();
        private readonly List<KeyValuePair<DynamicBone_Ver02, float>> savedRates2 = new List<KeyValuePair<DynamicBone_Ver02, float>>();

        public ExportJob(Plugin plugin)
        {
            this.plugin = plugin;
            Status = "Starting";
        }

        public string Status { get; private set; }
        public bool Finished { get; private set; }
        public string Folder { get; private set; }
        public bool KkbpPending { get; private set; }

        public void Cancel()
        {
            cancel = true;
        }

        public IEnumerator Run()
        {
            IEnumerator inner = null;
            try
            {
                inner = Steps();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            while (inner != null)
            {
                object current;
                try
                {
                    if (!inner.MoveNext())
                    {
                        break;
                    }
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    Fail(e);
                    break;
                }
                yield return current;
            }
            Restore();
            Finished = true;
        }

        private void Fail(Exception e)
        {
            Plugin.Log.LogError("Export failed: " + e);
            Status = "Failed: " + e.Message;
        }

        private IEnumerator Steps()
        {
            var studio = Singleton<Studio.Studio>.Instance;
            if (studio == null)
            {
                throw new InvalidOperationException("Studio isn't loaded.");
            }
            var objects = studio.dicObjectCtrl.Values.ToList();
            var characters = objects.OfType<OCIChar>().Where(c => c.charInfo != null).OrderBy(c => c.objectInfo.dicKey).ToList();

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            Folder = Path.Combine(plugin.OutputRoot.Value, "kk2b_" + stamp);
            string cardsDir = Path.Combine(Folder, "cards");
            string tracksDir = Path.Combine(Folder, "tracks");
            Directory.CreateDirectory(cardsDir);
            Directory.CreateDirectory(tracksDir);
            Plugin.Log.LogInfo("Exporting to " + Folder);

            // 1. Cards.
            Status = "Saving character cards";
            var charEntries = new List<object>();
            for (int i = 0; i < characters.Count; i++)
            {
                var oci = characters[i];
                string id = "c" + i.ToString("00", CultureInfo.InvariantCulture);
                string card = Path.Combine(cardsDir, id + ".png");
                SaveCard(oci.charInfo, card);
                charEntries.Add(Describe(oci, id));
            }
            yield return null;

            // 1b. Props: each item's meshes and materials as a .glb.
            var propFiles = new Dictionary<int, string>();
            if (plugin.ExportProps.Value)
            {
                string propsDir = Path.Combine(Folder, "props");
                Directory.CreateDirectory(propsDir);
                var exporter = new PropExporter();
                var items = objects.OfType<OCIItem>().Where(o => o.objectItem != null).ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    Status = "Writing props " + (i + 1) + "/" + items.Count;
                    byte[] glb = null;
                    try
                    {
                        glb = exporter.Export(items[i]);
                    }
                    catch (Exception e)
                    {
                        exporter.Warnings.Add(items[i].objectItem.name + ": " + e.Message);
                    }
                    if (glb != null)
                    {
                        string name = "item_" + items[i].objectInfo.dicKey + ".glb";
                        File.WriteAllBytes(Path.Combine(propsDir, name), glb);
                        propFiles[items[i].objectInfo.dicKey] = "props/" + name;
                    }
                    if (i % 10 == 9)
                    {
                        yield return null;
                    }
                }
                foreach (var w in exporter.Warnings)
                {
                    Plugin.Log.LogWarning("Prop: " + w);
                }
            }

            // 2. Static description (completed after the bake).
            var scene = new JsonObject()
                .Set("format", "kk2blender-scene")
                .Set("version", FormatVersion)
                .Set("exporter", new JsonObject().Set("name", Plugin.Name).Set("version", Plugin.Version).Set("game", "KK"))
                .Set("created", DateTime.Now.ToString("s", CultureInfo.InvariantCulture))
                .Set("space", "unity: left-handed, +Y up, metres; matrices are 3x4 row-major")
                .Set("screen", new object[] { Screen.width, Screen.height })
                .Set("characters", charEntries)
                .Set("items", objects.OfType<OCIItem>().Where(o => o.objectItem != null).OrderBy(o => o.objectInfo.dicKey).Select(o => (object)Describe(o, propFiles)).ToList())
                .Set("folders", objects.OfType<OCIFolder>().OrderBy(o => o.objectInfo.dicKey).Select(o => (object)Describe(o)).ToList())
                .Set("studio_lights", objects.OfType<OCILight>().OrderBy(o => o.objectInfo.dicKey).Select(o => (object)Describe(o)).ToList())
                .Set("map", DescribeMap(studio))
                .Set("effects", DescribeEffects(studio.sceneInfo));

            // 3. Bake.
            var charSamplers = characters.Select(c => new CharacterSampler(c)).ToList();
            var sceneSampler = new SceneSampler(objects);
            var time = new TrackSet();

            int fps = plugin.Fps.Value;
            bool useTimeline = TimelineLink.Available && TimelineLink.RealDuration > 0f;
            float duration = useTimeline ? TimelineLink.RealDuration : plugin.FallbackSeconds.Value;
            // Timeline wraps its time at its duration, so a clip that fills the whole Timeline ends
            // one frame early (its last frame would show frame 0 again).
            bool fillsTimeline = useTimeline && Mathf.Abs(TimelineLink.Duration - duration) < 0.5f / fps;
            int frames = Mathf.Max(1, Mathf.RoundToInt(duration * fps) + (fillsTimeline ? 0 : 1));

            savedCaptureFramerate = Time.captureFramerate;
            touched = true;
            Time.captureFramerate = fps;
            PhysicsEveryFrame();

            if (useTimeline)
            {
                TimelineLink.Stop();
            }

            int settle = Mathf.RoundToInt(plugin.SettleSeconds.Value * fps);
            for (int i = 0; i < settle; i++)
            {
                if (cancel)
                {
                    Status = "Cancelled";
                    yield break;
                }
                Status = "Letting physics settle " + (i + 1) + "/" + settle;
                if (useTimeline)
                {
                    // Keep animations that Timeline doesn't drive at their start, too.
                    TimelineLink.Seek(0f);
                }
                yield return new WaitForEndOfFrame();
            }

            var started = DateTime.Now;
            for (int f = 0; f < frames; f++)
            {
                if (f > 0)
                {
                    yield return new WaitForEndOfFrame();
                }
                else
                {
                    // Frame 0 is the settled state at time 0; the next frames follow Play.
                    yield return new WaitForEndOfFrame();
                }
                if (cancel)
                {
                    Status = "Cancelled";
                    yield break;
                }
                foreach (var s in charSamplers)
                {
                    s.Sample(f);
                }
                sceneSampler.Sample(f);
                time.BeginFrame(f);
                time.Put("time", "seconds", useTimeline ? TimelineLink.PlaybackTime : (float)f / fps);
                if (f == 0 && useTimeline)
                {
                    TimelineLink.Play();
                }
                var elapsed = DateTime.Now - started;
                double left = elapsed.TotalSeconds / (f + 1) * (frames - f - 1);
                Status = "Recording frame " + (f + 1) + "/" + frames + ", about " + Math.Ceiling(left) + " s left";
            }
            if (useTimeline)
            {
                TimelineLink.Stop();
            }
            Restore();

            // 4. Files.
            Status = "Writing tracks";
            yield return null;
            for (int i = 0; i < charSamplers.Count; i++)
            {
                string id = "c" + i.ToString("00", CultureInfo.InvariantCulture);
                charSamplers[i].Tracks.Save(Path.Combine(tracksDir, id + ".k2t"), charSamplers[i].Header());
            }
            sceneSampler.Tracks.Save(Path.Combine(tracksDir, "scene.k2t"), sceneSampler.Header());
            time.Save(Path.Combine(tracksDir, "time.k2t"), new JsonObject().Set("subject", "time"));

            scene.Set("bake", new JsonObject()
                .Set("fps", fps)
                .Set("frame_count", frames)
                .Set("duration", duration)
                .Set("source", useTimeline ? "timeline" : "fixed_length")
                .Set("settle_seconds", plugin.SettleSeconds.Value)
                .Set("tracks", new JsonObject().Set("scene", "tracks/scene.k2t").Set("time", "tracks/time.k2t")));

            if (plugin.ExportCharacterMeshes.Value && characters.Count > 0)
            {
                scene.Set("kkbp", RequestKkbp(cardsDir));
            }
            scene.Set("complete", true);
            File.WriteAllText(Path.Combine(Folder, "scene.json"), JsonWriter.Write(scene));

            Status = "Done: " + characters.Count + " character(s), " + frames + " frames" +
                (KkbpPending ? ". Start the main game once to export the character meshes." : ".");
            Plugin.Log.LogInfo(Status);
        }

        private static void SaveCard(ChaControl cha, string path)
        {
            var file = cha.chaFile;
            byte[] png = file.pngData;
            if (png == null || png.Length == 0)
            {
                // Cards need a picture in front; a character made in Studio may have none.
                var tex = new Texture2D(252, 352, TextureFormat.RGB24, false);
                file.pngData = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
            }
            try
            {
                using (var stream = File.Create(path))
                {
                    if (!file.SaveCharaFile(stream, true))
                    {
                        throw new IOException("The game refused to save the card " + path);
                    }
                }
            }
            finally
            {
                file.pngData = png;
            }
        }

        private static JsonObject Describe(OCIChar oci, string id)
        {
            var cha = oci.charInfo;
            return new JsonObject()
                .Set("id", id)
                .Set("key", oci.objectInfo.dicKey)
                .Set("name", cha.chaFile.parameter.fullname)
                .Set("sex", (int)cha.sex)
                .Set("parent", Parent(oci))
                .Set("card", "cards/" + id + ".png")
                .Set("tracks", "tracks/" + id + ".k2t")
                .Set("coordinates", cha.chaFile.coordinate != null ? cha.chaFile.coordinate.Length : 0)
                .Set("coordinate", cha.fileStatus.coordinateType);
        }

        private static JsonObject Describe(OCIItem item, Dictionary<int, string> propFiles)
        {
            var info = item.itemInfo;
            var o = new JsonObject()
                .Set("key", item.objectInfo.dicKey)
                .Set("name", item.treeNodeObject != null ? item.treeNodeObject.textName : item.objectItem.name)
                .Set("parent", Parent(item))
                .Set("track", "item:" + item.objectInfo.dicKey)
                .Set("group", info.group)
                .Set("category", info.category)
                .Set("no", info.no)
                .Set("alpha", info.alpha)
                .Set("colors", info.color != null ? info.color.Select(c => (object)Space.Color(c)).ToList() : new List<object>());
            string model;
            if (propFiles.TryGetValue(item.objectInfo.dicKey, out model))
            {
                o.Set("model", model);
            }
            Info.ItemLoadInfo load;
            if (TryItemLoad(info.group, info.category, info.no, out load))
            {
                o.Set("asset", new JsonObject()
                    .Set("bundle", load.bundlePath)
                    .Set("file", load.fileName)
                    .Set("manifest", load.manifest)
                    .Set("name", load.name));
            }
            return o;
        }

        private static bool TryItemLoad(int group, int category, int no, out Info.ItemLoadInfo load)
        {
            load = null;
            var info = Singleton<Info>.Instance;
            Dictionary<int, Dictionary<int, Info.ItemLoadInfo>> byCategory;
            Dictionary<int, Info.ItemLoadInfo> byNo;
            return info != null
                && info.dicItemLoadInfo.TryGetValue(group, out byCategory)
                && byCategory.TryGetValue(category, out byNo)
                && byNo.TryGetValue(no, out load);
        }

        private static JsonObject Describe(OCIFolder folder)
        {
            return new JsonObject()
                .Set("key", folder.objectInfo.dicKey)
                .Set("name", folder.name)
                .Set("parent", Parent(folder))
                .Set("track", "folder:" + folder.objectInfo.dicKey);
        }

        private static JsonObject Describe(OCILight light)
        {
            var i = light.lightInfo;
            return new JsonObject()
                .Set("key", light.objectInfo.dicKey)
                .Set("parent", Parent(light))
                .Set("type", light.lightType.ToString().ToLowerInvariant())
                .Set("color", Space.Color(i.color))
                .Set("intensity", i.intensity)
                .Set("range", i.range)
                .Set("spot_angle", i.spotAngle)
                .Set("shadow", i.shadow)
                .Set("enable", i.enable);
        }

        private static object Parent(ObjectCtrlInfo o)
        {
            if (o.parentInfo == null || o.parentInfo.objectInfo == null)
            {
                return null;
            }
            return o.parentInfo.objectInfo.dicKey;
        }

        private static JsonObject DescribeMap(Studio.Studio studio)
        {
            var si = studio.sceneInfo;
            if (si == null || si.map < 0)
            {
                return null;
            }
            var o = new JsonObject()
                .Set("no", si.map)
                .Set("option", si.mapOption)
                .Set("sun", si.sunLightType)
                .Set("position", Space.Vector(si.caMap.pos))
                .Set("rotation", Space.Vector(si.caMap.rot))
                .Set("track", "map");
            var info = Singleton<Info>.Instance;
            Info.MapLoadInfo load;
            if (info != null && info.dicMapLoadInfo.TryGetValue(si.map, out load))
            {
                o.Set("name", load.name);
                o.Set("bundle", load.bundlePath);
                o.Set("file", load.fileName);
            }
            return o;
        }

        private static JsonObject DescribeEffects(SceneInfo si)
        {
            if (si == null)
            {
                return null;
            }
            return new JsonObject()
                .Set("ace", new JsonObject().Set("no", si.aceNo).Set("blend", si.aceBlend))
                .Set("ao", new JsonObject().Set("enable", si.enableAOE).Set("color", Space.Color(si.aoeColor)).Set("radius", si.aoeRadius))
                .Set("bloom", new JsonObject().Set("enable", si.enableBloom).Set("intensity", si.bloomIntensity).Set("blur", si.bloomBlur).Set("threshold", si.bloomThreshold))
                .Set("dof", new JsonObject().Set("enable", si.enableDepth).Set("focal_size", si.depthFocalSize).Set("aperture", si.depthAperture))
                .Set("vignette", new JsonObject().Set("enable", si.enableVignette))
                .Set("fog", new JsonObject().Set("enable", si.enableFog).Set("color", Space.Color(si.fogColor)).Set("height", si.fogHeight).Set("start", si.fogStartDistance))
                .Set("sun_shafts", new JsonObject().Set("enable", si.enableSunShafts).Set("color", Space.Color(si.sunColor)).Set("threshold", Space.Color(si.sunThresholdColor)))
                .Set("shadow", si.enableShadow)
                .Set("ambient_shadow", Space.Color(si.ambientShadow))
                .Set("line_width", si.lineWidthG)
                .Set("line_color", si.lineColorG)
                .Set("ramp", si.rampG)
                .Set("chara_light", new JsonObject()
                    .Set("color", Space.Color(si.charaLight.color))
                    .Set("intensity", si.charaLight.intensity)
                    .Set("rotation", new object[] { si.charaLight.rot[0], si.charaLight.rot[1] })
                    .Set("shadow", si.charaLight.shadow));
        }

        /// <summary>
        /// Writes KKBP's auto-export request (its ADR 0012): the next time the main game starts, the
        /// KKBP exporter loads every card in <paramref name="cardsDir"/> in the maker, exports each with
        /// all outfits into meshes/, writes its result and quits.
        /// </summary>
        private JsonObject RequestKkbp(string cardsDir)
        {
            string meshes = Path.Combine(Folder, "meshes");
            string request = Path.Combine(BepInEx.Paths.ConfigPath, "kkbp-autoexport.json");
            string result = Path.Combine(BepInEx.Paths.ConfigPath, "kkbp-autoexport.result.json");
            var info = new JsonObject()
                .Set("cards", "cards")
                .Set("output", "meshes")
                .Set("request", request)
                .Set("result", result);

            bool installed = Directory.GetFiles(BepInEx.Paths.PluginPath, "KKBP.Exporter.KK.dll", SearchOption.AllDirectories).Length > 0;
            info.Set("exporter_installed", installed);
            if (!installed)
            {
                Plugin.Log.LogWarning("The KKBP exporter isn't installed; character meshes weren't requested.");
                info.Set("state", "exporter_missing");
                return info;
            }
            if (File.Exists(request))
            {
                Plugin.Log.LogWarning("A KKBP auto-export request is already waiting (" + request + "); left it alone.");
                info.Set("state", "another_request_waiting");
                return info;
            }
            Directory.CreateDirectory(meshes);
            var body = new JsonObject()
                .Set("folder", cardsDir)
                .Set("output", meshes)
                .Set("options", new JsonObject().Set("all_outfits", true));
            File.WriteAllText(request, JsonWriter.Write(body));
            if (File.Exists(result))
            {
                File.Delete(result);
            }
            KkbpPending = true;
            info.Set("state", "requested");
            return info;
        }

        private void PhysicsEveryFrame()
        {
            // DynamicBone can update at its own rate; at a fixed capture rate that would skip or
            // repeat steps. -1 means once per frame (as VideoExport does).
            foreach (var b in Resources.FindObjectsOfTypeAll<DynamicBone>())
            {
                savedRates.Add(new KeyValuePair<DynamicBone, float>(b, b.m_UpdateRate));
                b.m_UpdateRate = -1f;
            }
            foreach (var b in Resources.FindObjectsOfTypeAll<DynamicBone_Ver02>())
            {
                savedRates2.Add(new KeyValuePair<DynamicBone_Ver02, float>(b, b.UpdateRate));
                b.UpdateRate = -1f;
            }
        }

        private bool restored;
        private bool touched;

        private void Restore()
        {
            if (restored || !touched)
            {
                return;
            }
            restored = true;
            Time.captureFramerate = savedCaptureFramerate;
            foreach (var kv in savedRates)
            {
                if (kv.Key != null)
                {
                    kv.Key.m_UpdateRate = kv.Value;
                }
            }
            foreach (var kv in savedRates2)
            {
                if (kv.Key != null)
                {
                    kv.Key.UpdateRate = kv.Value;
                }
            }
        }
    }
}
