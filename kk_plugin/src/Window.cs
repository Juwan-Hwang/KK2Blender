using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>The plugin's small IMGUI window: settings, Export All, progress.</summary>
    internal sealed class Window
    {
        private readonly Plugin plugin;
        private Rect rect = new Rect(80, 80, 380, 10);
        private string fpsText;
        private string settleText;
        private string fallbackText;

        public Window(Plugin plugin)
        {
            this.plugin = plugin;
        }

        public bool Visible { get; set; }

        public void Draw()
        {
            var job = plugin.Job;
            if (!Visible && (job == null || job.Finished))
            {
                return;
            }
            rect = GUILayout.Window(0x4b32424c, rect, Contents, "KK2Blender " + Plugin.Version);
        }

        private void Contents(int id)
        {
            var job = plugin.Job;
            bool busy = plugin.Busy;

            GUI.enabled = !busy;
            GUILayout.Label("Output folder");
            plugin.OutputRoot.Value = GUILayout.TextField(plugin.OutputRoot.Value);

            GUILayout.BeginHorizontal();
            GUILayout.Label("FPS", GUILayout.Width(150));
            plugin.Fps.Value = IntField(ref fpsText, plugin.Fps.Value, 1, 120);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Physics settle (s)", GUILayout.Width(150));
            plugin.SettleSeconds.Value = FloatField(ref settleText, plugin.SettleSeconds.Value, 0f, 30f);
            GUILayout.EndHorizontal();

            if (!TimelineLink.Available)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Length (s), no Timeline", GUILayout.Width(150));
                plugin.FallbackSeconds.Value = FloatField(ref fallbackText, plugin.FallbackSeconds.Value, 0.1f, 600f);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("Length: the Timeline animation (" + TimelineLink.RealDuration.ToString("0.00") + " s)");
            }

            plugin.ExportCharacterMeshes.Value = GUILayout.Toggle(plugin.ExportCharacterMeshes.Value, " Prepare character meshes (KKBP)");

            GUI.enabled = true;
            GUILayout.Space(6);
            if (!busy)
            {
                if (GUILayout.Button("Export All", GUILayout.Height(32)))
                {
                    plugin.StartExport();
                }
            }
            else if (GUILayout.Button("Cancel", GUILayout.Height(32)))
            {
                plugin.CancelExport();
            }

            if (job != null)
            {
                GUILayout.Label(job.Status);
                if (job.Finished && job.Folder != null)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Open folder"))
                    {
                        Process.Start(job.Folder);
                    }
                    if (job.KkbpPending && GUILayout.Button("Export meshes now (starts the game)"))
                    {
                        string exe = Path.Combine(BepInEx.Paths.GameRootPath, "Koikatu.exe");
                        if (File.Exists(exe))
                        {
                            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = BepInEx.Paths.GameRootPath, UseShellExecute = true });
                        }
                    }
                    GUILayout.EndHorizontal();
                    if (!busy && GUILayout.Button("Close"))
                    {
                        plugin.CancelExport();
                        Visible = false;
                    }
                }
            }
            GUI.DragWindow();
        }

        private static int IntField(ref string text, int value, int min, int max)
        {
            if (text == null)
            {
                text = value.ToString();
            }
            text = GUILayout.TextField(text);
            int parsed;
            if (int.TryParse(text, out parsed) && parsed >= min && parsed <= max)
            {
                return parsed;
            }
            return value;
        }

        private static float FloatField(ref string text, float value, float min, float max)
        {
            if (text == null)
            {
                text = value.ToString("0.###");
            }
            text = GUILayout.TextField(text);
            float parsed;
            if (float.TryParse(text, out parsed) && parsed >= min && parsed <= max)
            {
                return parsed;
            }
            return value;
        }
    }

    /// <summary>The toolbar icon, drawn in code so the plugin is one DLL: a "K→B" arrow on a dark tile.</summary>
    internal static class Icon
    {
        public static Texture2D Make()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var bg = new Color32(40, 44, 52, 255);
            var fg = new Color32(240, 140, 40, 255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool border = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                    // A right-pointing arrow: shaft plus head.
                    bool shaft = y >= 14 && y <= 17 && x >= 6 && x <= 20;
                    bool head = x >= 18 && x <= 27 && System.Math.Abs(y - 15.5f) <= (27 - x) * 0.9f;
                    tex.SetPixel(x, y, border ? (Color)fg : (shaft || head ? (Color)fg : (Color)bg));
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
