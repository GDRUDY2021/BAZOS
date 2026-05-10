using System;
using System.Security.Cryptography;
using System.Reflection;

namespace BAZOS.Drivers
{
    public static class DriverCrypto
    {
        public static byte[] Sha256(ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2)
        {
            using var sha = SHA256.Create();

            // Cosmos may not support TransformBlock well; keep it simple with one buffer.
            byte[] combined = new byte[data1.Length + data2.Length];
            data1.CopyTo(combined.AsSpan(0, data1.Length));
            data2.CopyTo(combined.AsSpan(data1.Length, data2.Length));

            return sha.ComputeHash(combined);
        }

        public static bool VerifyEd25519(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> signature64, ReadOnlySpan<byte> messageHash32)
        {
            // Cosmos toolchain currently may not expose System.Security.Cryptography.Ed25519.
            // We avoid a hard compile-time dependency and try reflection first.
            try
            {
                var ed25519Type =
                    Type.GetType("System.Security.Cryptography.Ed25519, System.Security.Cryptography.Algorithms")
                    ?? Type.GetType("System.Security.Cryptography.Ed25519");

                if (ed25519Type == null)
                    return false;

                // Preferred runtime signature (if available):
                // bool Verify(byte[] signature, byte[] data, byte[] publicKey)
                MethodInfo? verify = ed25519Type.GetMethod(
                    "Verify",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(byte[]), typeof(byte[]), typeof(byte[]) },
                    modifiers: null
                );

                if (verify == null)
                    return false;

                var result = verify.Invoke(
                    obj: null,
                    parameters: new object[]
                    {
                        signature64.ToArray(),
                        messageHash32.ToArray(),
                        publicKey32.ToArray()
                    }
                );

                return result is bool ok && ok;
            }
            catch
            {
                return false;
            }
        }
    }
}

