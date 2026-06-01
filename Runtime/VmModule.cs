namespace BAZOS.Runtime
{
    public sealed class VmModule
    {
        public byte[] Code { get; private set; } = Array.Empty<byte>();

        public static bool TryLoad(byte[] fileData, out VmModule module, out string error)
        {
            module = new VmModule();
            error = "";

            if (fileData == null || fileData.Length == 0)
            {
                error = "File is empty.";
                return false;
            }

            // Пытаемся распарсить как новый формат BVX-Vm
            if (BvxExecutable.TryLoad(fileData, out var bvx, out string bvxErr))
            {
                module.Code = bvx.Bytecode;
                return true;
            }

            // Если не BVX, проверяем старый формат (на всякий случай)
            if (fileData.Length > 6 && fileData[0] == 'V' && fileData[1] == 'M' && fileData[2] == '1')
            {
                int codeSize = fileData[5];
                if (fileData.Length >= 6 + codeSize)
                {
                    var code = new byte[codeSize];
                    Array.Copy(fileData, 6, code, 0, codeSize);
                    module.Code = code;
                    return true;
                }
            }

            error = $"Unknown executable format. {bvxErr}";
            return false;
        }
    }
}