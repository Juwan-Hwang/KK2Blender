using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKAPI.Studio;
using KKAPI.Studio.UI;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// KK2Blender for CharaStudio: one "Export All" that writes the open scene (characters, their
    /// per-frame bones, faces and outfits, props, lights, camera and the map) to one folder for the
    /// Blender add-on. See docs/design.md and docs/bake-format.md.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("CharaStudio")]
    [BepInDependency(KKAPI.KoikatuAPI.GUID, KKAPI.KoikatuAPI.VersionConst)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.juwanhwang.kk2blender";
        public const string Name = "KK2Blender";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal ConfigEntry<string> OutputRoot;
        internal ConfigEntry<int> Fps;
        internal ConfigEntry<float> SettleSeconds;
        internal ConfigEntry<float> FallbackSeconds;
        internal ConfigEntry<bool> ExportCharacterMeshes;
        internal ConfigEntry<KeyboardShortcut> WindowKey;

        private ExportJob job;
        private Window window;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            OutputRoot = Config.Bind("Export", "Output folder", Path.Combine(Paths.GameRootPath, Path.Combine("UserData", "kk2blender")),
                "Each export goes into a new subfolder here, named after the scene and the time.");
            Fps = Config.Bind("Export", "Frames per second", 30,
                new ConfigDescription("Frame rate of the bake. Physics is stepped at this rate, so use the rate you will render at.",
                    new AcceptableValueRange<int>(1, 120)));
            SettleSeconds = Config.Bind("Export", "Physics settle time", 2f,
                new ConfigDescription("Seconds the scene is held at frame 0 before recording, so hair and skirts come to rest.",
                    new AcceptableValueRange<float>(0f, 30f)));
            FallbackSeconds = Config.Bind("Export", "Length without Timeline", 10f,
                new ConfigDescription("How long to record when the scene has no Timeline animation (Studio poses and animations still play).",
                    new AcceptableValueRange<float>(0.1f, 600f)));
            ExportCharacterMeshes = Config.Bind("Export", "Prepare character meshes", true,
                "Save each character's card and ask the KKBP exporter to export them (it runs the next time the main game starts).");
            WindowKey = Config.Bind("General", "Window key", new KeyboardShortcut(KeyCode.E, KeyCode.LeftControl, KeyCode.LeftShift),
                "Shows or hides the KK2Blender window.");

            window = new Window(this);
            StudioAPI.StudioLoadedChanged += (s, e) => AddToolbarButton();
        }

        private void AddToolbarButton()
        {
            try
            {
                CustomToolbarButtons.AddLeftToolbarButton(Icon.Make(), () => window.Visible = !window.Visible);
            }
            catch (Exception e)
            {
                Log.LogWarning("Couldn't add the toolbar button (use " + WindowKey.Value + "): " + e.Message);
            }
        }

        internal bool Busy
        {
            get { return job != null && !job.Finished; }
        }

        internal ExportJob Job
        {
            get { return job; }
        }

        internal void StartExport()
        {
            if (Busy)
            {
                return;
            }
            job = new ExportJob(this);
            StartCoroutine(job.Run());
        }

        internal void CancelExport()
        {
            if (Busy)
            {
                job.Cancel();
            }
        }

        private void Update()
        {
            if (WindowKey.Value.IsDown())
            {
                window.Visible = !window.Visible;
            }
        }

        private void OnGUI()
        {
            window.Draw();
        }
    }
}
