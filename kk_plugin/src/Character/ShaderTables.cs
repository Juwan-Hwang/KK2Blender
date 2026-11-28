using System;
using System.IO;
using System.Reflection;
using KK2Blender.Format.Bundle;

namespace KK2Blender.Character
{
    /// <summary>
    /// The shader property table the capture reads materials with: the exporter's own (embedded,
    /// tools/build_shader_table.py), plus KKBP's when its exporter is loaded in the game (its
    /// embedded "KKBP.ShaderProperties.json", read from the running assembly, not copied).
    /// </summary>
    internal static class ShaderTables
    {
        private static ShaderTable table;

        public static ShaderTable Get()
        {
            if (table != null)
            {
                return table;
            }
            var t = new ShaderTable();
            int own = LoadFrom(t, typeof(ShaderTables).Assembly, "KK2Blender.ShaderProperties.json");
            int kkbp = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(ShaderTables).Assembly)
                {
                    continue;
                }
                kkbp += LoadFrom(t, asm, "KKBP.ShaderProperties.json");
            }
            Plugin.Log.LogInfo("Shader property tables: " + t.ShaderCount + " shaders (" + (own > 0 ? "own" : "own table missing") +
                (kkbp > 0 ? ", KKBP's" : ", KKBP's not loaded") + ")");
            table = t;
            return t;
        }

        private static int LoadFrom(ShaderTable t, Assembly asm, string resource)
        {
            try
            {
                using (var stream = asm.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        return 0;
                    }
                    using (var reader = new StreamReader(stream))
                    {
                        t.Load(reader.ReadToEnd());
                        return 1;
                    }
                }
            }
            catch (NotSupportedException)
            {
                return 0; // dynamic assemblies have no resources
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't read " + resource + " from " + asm.GetName().Name + ": " + e.Message);
                return 0;
            }
        }
    }
}
