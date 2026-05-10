using BAZOS.FS;
using System;
using System.Text;

namespace BAZOS.Api
{
    public static class ModuleLoader
    {
        public const string ConfigPath = "/system/config/modules.cfg";

        public static void Apply(Shell.CommandRegistry registry)
        {
            if (registry == null)
                return;

            if (!BazFs.IsMounted)
                return;

            if (!BazFs.TryReadFileBytes(ConfigPath, out var bytes))
                return;

            var text = Encoding.ASCII.GetString(bytes);
            ApplyConfigText(registry, text);
        }

        private static void ApplyConfigText(Shell.CommandRegistry registry, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                if (raw == null)
                    continue;

                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#"))
                    continue;

                if (line.StartsWith("disable ", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line.Substring("disable ".Length).Trim();
                    if (name.Length == 0)
                        continue;
                    registry.Disable(name);
                    continue;
                }

                // MVP: enable is a no-op because commands are registered before load.
                // To "enable back" you can reboot or remove the disable line.
            }
        }
    }
}

