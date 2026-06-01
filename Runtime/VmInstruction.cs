namespace BAZOS.Runtime
{
    public enum VmOpcode : byte
    {
        LoadNull = 0x00, LoadTrue = 0x01, LoadFalse = 0x02,
        LoadI32 = 0x03, LoadF64 = 0x04, LoadStr = 0x05, LoadByte = 0x06, LoadI16 = 0x07, Mov = 0x08,

        Add = 0x10, Sub = 0x11, Mul = 0x12, Div = 0x13, Mod = 0x14, Pow = 0x15, Neg = 0x16, AddI = 0x17, SubI = 0x18,

        Eq = 0x20, Neq = 0x21, Lt = 0x22, Le = 0x23, Gt = 0x24, Ge = 0x25, IsNull = 0x26, CmpI = 0x27,

        And = 0x30, Or = 0x31, Not = 0x32, BitAnd = 0x38, BitOr = 0x39, BitXor = 0x3A, BitNot = 0x3B, Shl = 0x3C, Shr = 0x3D,

        CastI32 = 0x40, CastF64 = 0x41, CastStr = 0x42, CastBool = 0x43, CastByte = 0x44,

        GLoad = 0x48, GStore = 0x49, GInit = 0x4A,

        Jmp = 0x50, JmpFalse = 0x51, JmpTrue = 0x52, JmpNull = 0x53, JmpEq = 0x54, JmpLt = 0x55,

        Call = 0x60, CallVoid = 0x61, CallInd = 0x62, CallIndVoid = 0x63, Ret = 0x64, RetVoid = 0x65,
        CallExtern = 0x66, CallExternVoid = 0x67, CallDestructor = 0x68, CallNative = 0x69, CallNativeVoid = 0x6A,

        FuncBegin = 0x70, FuncEnd = 0x71, PushFunc = 0x72, Closure = 0x73, LoadCapture = 0x74, StoreCapture = 0x75,

        NewArray = 0x80, NewArrayEmpty = 0x81, ArrLoad = 0x82, ArrStore = 0x83, ArrLength = 0x84, ArrInit = 0x85, ArrInitGlobal = 0x86, ArrPush = 0x87, NewByteArray = 0x88,

        NewStruct = 0x90, GetField = 0x91, SetField = 0x92, GetTypeId = 0x93, SetTypeId = 0x94, IndexStruct = 0x95, StructArrSetField = 0x96,

        StrLength = 0xA0, StrConcat = 0xA1, PtrReadStr = 0xA2, PtrReadI32 = 0xA8, PtrWriteI32 = 0xA9,

        Try = 0xB0, TryEnd = 0xB1, Throw = 0xB2, Catch = 0xB3,
        Nop = 0xF0, PrintArr = 0xF1, End = 0xFF
    }

    public static class VmHostCall
    {
        public const byte Log = 1; public const byte RegisterDevice = 2; public const byte SetProp = 3;
        public const byte KeyboardEnable = 4; public const byte KeyboardPush = 5; public const byte KeyboardReadRaw = 6;
        public const byte InputEnqueue = 7; public const byte InputSetEnabled = 8; public const byte PortRead8 = 9;
        public const byte PortWrite8 = 10; public const byte Sleep = 11; public const byte Print = 12;
        public const byte Clear = 13; public const byte ReadLine = 14; public const byte System = 15;
    }
}