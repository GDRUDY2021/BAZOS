using System.Text;

namespace BAZOS.Drivers
{
    public static class DriverPackageFormat
    {
        public const string DriversRoot = "/system/drivers";
        public const string ManifestName = "manifest.txt";
        public const string PayloadName = "payload.bin";

        // --- УПАКОВЩИК .DRV ФОРМАТА ---
        public static byte[] Pack(byte[] manifest, byte[] payload)
        {
            manifest ??= Array.Empty<byte>();
            payload ??= Array.Empty<byte>();

            List<byte> res = new List<byte>();

            // Магическая сигнатура "BDRV" (BAZOS Driver)
            res.AddRange(Encoding.ASCII.GetBytes("BDRV"));
            res.Add(1); // Версия формата

            // Размеры (по 4 байта)
            res.AddRange(BitConverter.GetBytes(manifest.Length));
            res.AddRange(BitConverter.GetBytes(payload.Length));

            // Данные
            res.AddRange(manifest);
            res.AddRange(payload);

            return res.ToArray();
        }

        public static bool Unpack(byte[] drv, out byte[] manifest, out byte[] payload)
        {
            manifest = Array.Empty<byte>();
            payload = Array.Empty<byte>();

            if (drv == null || drv.Length < 13) return false;

            string magic = Encoding.ASCII.GetString(drv, 0, 4);
            if (magic != "BDRV") return false;

            byte version = drv[4];
            if (version != 1) return false;

            int manLen = BitConverter.ToInt32(drv, 5);
            int payLen = BitConverter.ToInt32(drv, 9);

            if (drv.Length < 13 + manLen + payLen) return false;

            manifest = new byte[manLen];
            Array.Copy(drv, 13, manifest, 0, manLen);

            payload = new byte[payLen];
            Array.Copy(drv, 13 + manLen, payload, 0, payLen);

            return true;
        }

        public static Dictionary<string, string> ParseManifest(byte[] bytes)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (bytes == null || bytes.Length == 0) return dict;

            var text = Encoding.ASCII.GetString(bytes);
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1);
                if (k.Length > 0) dict[k] = v;
            }
            return dict;
        }
    }
}