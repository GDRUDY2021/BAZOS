using System;
using System.Text;

namespace BAZOS.Runtime
{
    public class BvxExecutable
    {
        public string Magic { get; private set; }
        public ushort EntryPoint { get; private set; }
        public byte[] Bytecode { get; private set; }

        public static bool TryLoad(byte[] fileData, out BvxExecutable bvx, out string error)
        {
            bvx = null;
            error = "";

            try
            {
                if (fileData == null || fileData.Length < 13)
                {
                    error = "File is too small to be a valid BVX.";
                    return false;
                }

                string magic = Encoding.ASCII.GetString(fileData, 0, 6);
                if (magic != "BVX-Vm" && magic != "DRV-Vm")
                {
                    // Ваш кастомный текст для неверного бинарника
                    error = "This BVX is not supported in the current version of BAZOS.";
                    return false;
                }

                byte version = fileData[6];
                if (version != 1)
                {
                    error = $"Unsupported version: {version}. Please recompile the program.";
                    return false;
                }

                ushort entryPoint = (ushort)(fileData[7] | (fileData[8] << 8));
                int payloadSize = fileData[9] | (fileData[10] << 8) | (fileData[11] << 16) | (fileData[12] << 24);

                // Защита от битых payload size
                if (payloadSize < 0 || fileData.Length < 13 + payloadSize)
                {
                    error = "BVX payload is corrupted or truncated.";
                    return false;
                }

                byte[] bytecode = new byte[payloadSize];
                Array.Copy(fileData, 13, bytecode, 0, payloadSize);

                bvx = new BvxExecutable
                {
                    Magic = magic,
                    EntryPoint = entryPoint,
                    Bytecode = bytecode
                };

                return true;
            }
            catch (Exception ex)
            {
                error = $"Fatal error reading BVX: {ex.Message}";
                return false;
            }
        }
    }
}