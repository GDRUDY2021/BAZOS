using System;
using System.Collections.Generic;
using System.Linq;

namespace BAZOS.Drivers
{
    public sealed class DriverPackage
    {
        public string PackageName { get; set; } = "";
        public string PackagePath { get; set; } = "";

        public Dictionary<string, string> Manifest { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[] ManifestBytes { get; set; } = Array.Empty<byte>();
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();

        public byte[] SignatureBytes { get; set; } = Array.Empty<byte>(); // 64 bytes for Ed25519
        public string PubKeyId { get; set; } = ""; // key id selector

        public string? GetManifest(string key)
            => Manifest.TryGetValue(key, out var v) ? v : null;

        public string DriverId => GetManifest("id") ?? PackageName;
        public string DriverVersion => GetManifest("version") ?? "0";
        public string DriverType => GetManifest("type") ?? "unknown";
        public string Runtime => GetManifest("runtime") ?? "none";
        public string EntryInit => GetManifest("entry_init") ?? "Init";

        public bool IsRequired
        {
            get
            {
                var v = GetManifest("required");
                if (string.IsNullOrWhiteSpace(v))
                    return false;
                v = v.Trim();
                return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string[] Depends
        {
            get
            {
                var v = GetManifest("depends");
                if (string.IsNullOrWhiteSpace(v))
                    return Array.Empty<string>();

                return v.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }
}

