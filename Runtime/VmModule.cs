using System;
using System.Collections.Generic;
using System.Text;

namespace BAZOS.Runtime
{
    public sealed class VmModule
    {
        public const byte Version = 1;

        public byte[] Code { get; private set; } = Array.Empty<byte>();

        public static bool TryLoad(byte[] payload, out VmModule module, out string error)
        {
            module = new VmModule();
            error = "";

            if (payload == null || payload.Length < 6)
            {
                error = "payload too small";
                return false;
            }

            // Magic: 'V','M','1',0
            if (payload[0] != (byte)'V' || payload[1] != (byte)'M' || payload[2] != (byte)'1' || payload[3] != 0)
            {
                error = "bad vm magic";
                return false;
            }

            if (payload[4] != Version)
            {
                error = $"unsupported vm version {payload[4]}";
                return false;
            }

            int codeSize = payload[5];
            int expected = 6 + codeSize;
            if (payload.Length < expected)
            {
                error = "bad vm code size";
                return false;
            }

            var code = new byte[codeSize];
            Array.Copy(payload, 6, code, 0, codeSize);

            module.Code = code;
            return true;
        }
    }

    public static class VmModuleBuilder
    {
        public static byte[] BuildSimple(byte[] code)
        {
            if (code == null)
                code = Array.Empty<byte>();
            if (code.Length > 255)
                throw new ArgumentException("VM code too large (max 255 bytes).", nameof(code));

            var bytes = new byte[6 + code.Length];
            bytes[0] = (byte)'V';
            bytes[1] = (byte)'M';
            bytes[2] = (byte)'1';
            bytes[3] = 0;
            bytes[4] = VmModule.Version;
            bytes[5] = (byte)code.Length;
            Array.Copy(code, 0, bytes, 6, code.Length);
            return bytes;
        }

        public static byte[] BuildSysDrvSampleCode()
        {
            var code = new List<byte>();

            // host.log("sys.drv init")
            EmitPushStr(code, "sys.drv init");
            EmitCallHost(code, VmHostCall.Log);

            // host.register_device("system0", "Other", "System Driver", 1)
            EmitPushStr(code, "system0");
            EmitPushStr(code, "Other");
            EmitPushStr(code, "System Driver");
            EmitPushI32(code, 1);
            EmitCallHost(code, VmHostCall.RegisterDevice);

            // host.set_prop("system0", "runtime", "vm1")
            EmitPushStr(code, "system0");
            EmitPushStr(code, "runtime");
            EmitPushStr(code, "vm1");
            EmitCallHost(code, VmHostCall.SetProp);

            code.Add((byte)VmOpcode.Halt);
            return code.ToArray();
        }

        private static void EmitCallHost(List<byte> code, byte hostId)
        {
            code.Add((byte)VmOpcode.CallHost);
            code.Add(hostId);
        }

        private static void EmitPushI32(List<byte> code, int value)
        {
            code.Add((byte)VmOpcode.PushI32);
            code.Add((byte)(value & 0xFF));
            code.Add((byte)((value >> 8) & 0xFF));
            code.Add((byte)((value >> 16) & 0xFF));
            code.Add((byte)((value >> 24) & 0xFF));
        }

        private static void EmitPushStr(List<byte> code, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text ?? string.Empty);
            if (bytes.Length > 255)
                throw new ArgumentException("VM string literal too large (max 255 bytes).", nameof(text));

            code.Add((byte)VmOpcode.PushStr);
            code.Add((byte)bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
                code.Add(bytes[i]);
        }
    }
}

