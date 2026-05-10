namespace BAZOS.Runtime
{
    public enum VmOpcode : byte
    {
        Nop = 0x00,
        PushStr = 0x01,
        PushI32 = 0x02,
        CallHost = 0x03,
        Ret = 0x04,
        Halt = 0x05
    }

    public static class VmHostCall
    {
        public const byte Log = 1;
        public const byte RegisterDevice = 2;
        public const byte SetProp = 3;
    }
}

