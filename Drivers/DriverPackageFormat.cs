using System;
using System.Collections.Generic;
using System.Text;

namespace BAZOS.Drivers
{
    public static class DriverPackageFormat
    {
        // BAZFS constraints currently: each file <= 512 bytes.
        public const string DriversRoot = "/system/drivers";
        public const string ManifestName = "manifest.txt";
        public const string PayloadName = "payload.bin";
        public const string SignatureName = "signature.sig"; // hex-encoded Ed25519 signature (64 bytes => 128 hex chars)
        public const string PubKeyIdName = "pubkey.id";      // ASCII key id

        public static Dictionary<string, string> ParseManifest(byte[] bytes)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (bytes == null || bytes.Length == 0)
                return dict;

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

                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1);
                if (k.Length == 0)
                    continue;

                dict[k] = v;
            }

            return dict;
        }
    }
}

