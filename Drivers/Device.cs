using System;
using System.Collections.Generic;
using System.Text;
using BAZOS.FS;

namespace BAZOS.Drivers
{
    public enum DeviceType
    {
        Mouse,
        Keyboard,
        Audio,
        Mic,
        Display,
        Disk,
        Other
    }

    public class DeviceDescriptor
    {
        public string Id { get; set; }          // "mouse0", "kbd0"
        public DeviceType Type { get; set; }    // Mouse, Keyboard...
        public string Name { get; set; }        // Человеческое имя
        public bool Enabled { get; set; }
        public Dictionary<string, string> Props { get; } = new();
    }

    public static class DeviceManager
    {
        private static readonly List<DeviceDescriptor> _devices = new();

        public const string ConfigPath = "/system/config/devices.cfg";

        public static void RegisterDevice(DeviceDescriptor dev)
        {
            if (dev == null || string.IsNullOrWhiteSpace(dev.Id))
                return;

            // простая защита от дублей
            if (Find(dev.Id) != null)
                return;

            _devices.Add(dev);
        }

        public static IEnumerable<DeviceDescriptor> All => _devices;

        public static void LoadConfig()
        {
            if (!BazFs.IsMounted)
                return;

            if (!BazFs.TryReadFileBytes(ConfigPath, out var bytes))
                return;

            var text = Encoding.ASCII.GetString(bytes);
            ApplyConfigText(text);
        }

        public static void SaveConfig()
        {
            if (!BazFs.IsMounted)
                return;

            BazFs.CreateDirectory("/system/config");

            var text = BuildConfigText();
            var bytes = Encoding.ASCII.GetBytes(text);
            BazFs.CreateFileWithPath(ConfigPath, bytes, overwrite: true);
        }

        private static string BuildConfigText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BAZOS devices.cfg");

            foreach (var d in _devices)
            {
                sb.Append("device ").AppendLine(d.Id ?? "");
                sb.Append("type=").AppendLine(d.Type.ToString());
                sb.Append("name=").AppendLine(d.Name ?? "");
                sb.Append("enabled=").AppendLine(d.Enabled ? "1" : "0");

                foreach (var kv in d.Props)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;

                    sb.Append("prop.").Append(kv.Key.Trim()).Append('=').AppendLine(kv.Value ?? "");
                }

                sb.AppendLine("end");
            }

            return sb.ToString();
        }

        private static void ApplyConfigText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            DeviceDescriptor current = null;

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

                if (line.StartsWith("device ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = line.Substring("device ".Length).Trim();
                    current = Find(id);
                    continue;
                }

                if (string.Equals(line, "end", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }

                if (current == null)
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1);

                if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    current.Enabled = value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    current.Name = value;
                    continue;
                }

                if (key.StartsWith("prop.", StringComparison.OrdinalIgnoreCase))
                {
                    var propKey = key.Substring("prop.".Length).Trim();
                    if (propKey.Length == 0)
                        continue;
                    current.Props[propKey] = value;
                    continue;
                }

                // type=... is currently informational; ignore to avoid inconsistent enum parsing
            }
        }

        public static bool SetEnabled(string idOrName, bool enabled)
        {
            var d = Find(idOrName);
            if (d == null)
                return false;

            d.Enabled = enabled;
            SaveConfig();
            return true;
        }

        public static bool SetProp(string idOrName, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var d = Find(idOrName);
            if (d == null)
                return false;

            d.Props[key.Trim()] = value ?? string.Empty;
            SaveConfig();
            return true;
        }

        public static DeviceDescriptor Find(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName))
                return null;

            idOrName = idOrName.Trim();

            foreach (var d in _devices)
            {
                if (string.Equals(d.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }
    }
}