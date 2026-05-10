using System;
using System.Collections.Generic;
using System.Text;
using BAZOS.FS;

namespace BAZOS.Drivers
{
    public sealed class Keyring
    {
        public const string KeyringDir = "/system/security/keyring";

        // keyId -> publicKeyBytes (Ed25519 public key is 32 bytes)
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> KeyIds => _keys.Keys;

        public bool TryGet(string keyId, out byte[] pubKey)
            => _keys.TryGetValue(keyId, out pubKey);

        public static Keyring Load()
        {
            var kr = new Keyring();
            if (!BazFs.IsMounted)
                return kr;

            if (!BazFs.TryListDirectory(KeyringDir, out var entries))
                return kr;

            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.Flags != 0)
                    continue;

                string name = e.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string path = $"{KeyringDir}/{name}";
                if (!BazFs.TryReadFileBytes(path, out var bytes))
                    continue;

                // expected: ASCII hex of 32 bytes (64 hex chars) with optional whitespace/newlines
                var text = Encoding.ASCII.GetString(bytes).Trim();
                if (!TryParseHex(text, out var keyBytes))
                    continue;

                if (keyBytes.Length != 32)
                    continue;

                string keyId = name;
                kr._keys[keyId] = keyBytes;
            }

            return kr;
        }

        public static bool TryParseHex(string hex, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var sb = new StringBuilder(hex.Length);
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    continue;
                sb.Append(c);
            }

            string s = sb.ToString();
            if (s.Length % 2 != 0)
                return false;

            int n = s.Length / 2;
            var arr = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int hi = HexNibble(s[i * 2]);
                int lo = HexNibble(s[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                    return false;
                arr[i] = (byte)((hi << 4) | lo);
            }

            bytes = arr;
            return true;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}

