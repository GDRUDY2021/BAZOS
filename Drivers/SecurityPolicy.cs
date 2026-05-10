using System;
using System.Text;
using BAZOS.FS;

namespace BAZOS.Drivers
{
    public sealed class SecurityPolicy
    {
        public const string PolicyPath = "/system/security/policy.cfg";

        public bool DevMode { get; set; } = true;
        public bool AllowUnsigned { get; set; } = true;

        public static SecurityPolicy Load()
        {
            var p = new SecurityPolicy();
            if (!BazFs.IsMounted)
                return p;

            if (!BazFs.TryReadFileBytes(PolicyPath, out var bytes))
                return p;

            var text = Encoding.ASCII.GetString(bytes);
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();

                bool b = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("yes", StringComparison.OrdinalIgnoreCase);

                if (key.Equals("dev_mode", StringComparison.OrdinalIgnoreCase))
                    p.DevMode = b;
                else if (key.Equals("allow_unsigned", StringComparison.OrdinalIgnoreCase))
                    p.AllowUnsigned = b;
            }

            return p;
        }
    }
}

