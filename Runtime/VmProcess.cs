namespace BAZOS.Runtime
{
    public struct CallFrame
    {
        public int ReturnIp;
        public int FuncIndex;
        public byte DstReg;
        public int RegBase;
        public int NumRegs;
    }
    
    public class VmProcess
    {
        public int ProcessId { get; }
        public string Name { get; }
        public VmModule Module { get; }

        public int IP { get; set; }
        public bool IsFinished { get; private set; }
        public string ErrorMessage { get; private set; } = "";

        private readonly object[] _regs = new object[4096];
        private int _regTop = 0;
        private readonly List<CallFrame> _frames = new();

        public List<object> Globals { get; } = new();
        public List<string> StringPool { get; } = new();

        private int[] _funcIps;
        private byte[] _funcNumRegs;

        public VmProcess(int pid, string name, VmModule module)
        {
            ProcessId = pid;
            Name = name;
            Module = module;
            IP = 0;
            IsFinished = false;

            LoadStringPool();
            PreScanFunctions();

            _frames.Add(new CallFrame { ReturnIp = 0, RegBase = 0, NumRegs = 256, DstReg = 255 });
            _regTop = 256;
        }

        private void LoadStringPool()
        {
            var code = Module.Code;
            if (code == null || code.Length < 10)
            {
                IP = 0;
                return;
            }

            int strCount = code[0] | (code[1] << 8);
            int pos = 10;

            try
            {
                for (int i = 0; i < strCount && pos + 2 <= code.Length; i++)
                {
                    int len = code[pos] | (code[pos + 1] << 8);
                    pos += 2;

                    if (pos + len > code.Length) break;

                    var chars = new char[len];
                    for (int c = 0; c < len; c++)
                    {
                        chars[c] = (char)code[pos++];
                    }
                    StringPool.Add(new string(chars));
                }
                IP = pos;
            }
            catch
            {
                IP = 0;
            }
        }

        private void PreScanFunctions()
        {
            var code = Module.Code;
            if (code == null || code.Length < 10) return;

            int funcCount = code[4] | (code[5] << 8);
            _funcIps = new int[funcCount];
            _funcNumRegs = new byte[funcCount];

            int scan = IP;
            while (scan < code.Length)
            {
                VmOpcode op = (VmOpcode)code[scan++];

                if (op == VmOpcode.FuncBegin)
                {
                    int fidx = code[scan] | (code[scan + 1] << 8);
                    scan += 2;
                    byte numRegs = code[scan++];

                    if (fidx >= 0 && fidx < funcCount)
                    {
                        _funcIps[fidx] = scan;
                        _funcNumRegs[fidx] = numRegs;
                    }
                    continue;
                }

                if (op == VmOpcode.End) break;
                scan += GetInstructionSize(op) - 1;
            }
        }

        private int GetInstructionSize(VmOpcode op)
        {
            switch (op)
            {
                case VmOpcode.Nop:
                case VmOpcode.RetVoid:
                case VmOpcode.FuncEnd:
                case VmOpcode.End:
                    return 1;

                case VmOpcode.LoadNull:
                case VmOpcode.LoadTrue:
                case VmOpcode.LoadFalse:
                case VmOpcode.Ret:
                case VmOpcode.NewArrayEmpty:
                case VmOpcode.NewStruct:
                    return 2;

                case VmOpcode.Mov:
                case VmOpcode.Jmp:
                case VmOpcode.NewArray:
                case VmOpcode.ArrPush:
                case VmOpcode.PushFunc:
                    return 3;

                case VmOpcode.LoadStr:
                case VmOpcode.GInit:
                case VmOpcode.GStore:
                case VmOpcode.GLoad:
                case VmOpcode.JmpFalse:
                case VmOpcode.Add:
                case VmOpcode.Sub:
                case VmOpcode.Mul:
                case VmOpcode.Div:
                case VmOpcode.Eq:
                case VmOpcode.Neq:
                case VmOpcode.Lt:
                case VmOpcode.Le:
                case VmOpcode.Gt:
                case VmOpcode.Ge:
                case VmOpcode.ArrLoad:
                case VmOpcode.ArrStore:
                case VmOpcode.CallIndVoid:
                    return 4;

                case VmOpcode.CallNativeVoid:
                case VmOpcode.CallVoid:
                case VmOpcode.GetField:
                case VmOpcode.SetField:
                case VmOpcode.CallInd:
                    return 5;

                case VmOpcode.LoadI32:
                case VmOpcode.LoadF64:
                case VmOpcode.Call:
                    return 6;

                default:
                    return 1;
            }
        }

        private float SafeToFloat(object obj)
        {
            if (obj is float f) return f;
            if (obj is int i) return i;
            if (obj is bool b) return b ? 1f : 0f;
            return 0f;
        }

        private int SafeToInt(object obj)
        {
            if (obj is int i) return i;
            if (obj is float f) return (int)f;
            if (obj is bool b) return b ? 1 : 0;
            return 0;
        }

        private void HaltWithError(string err)
        {
            IsFinished = true;
            ErrorMessage = err;
        }

        private bool CheckBounds(int requiredBytes)
        {
            if (IP < 0 || IP + requiredBytes > Module.Code.Length)
            {
                HaltWithError($"Segmentation fault: Invalid memory access at IP {IP}.");
                return false;
            }
            return true;
        }

        private void SetReg(int rBase, byte reg, object value)
        {
            int target = rBase + reg;
            if (target < 0 || target >= _regs.Length)
            {
                HaltWithError($"Register OOB: {target}");
                return;
            }
            _regs[target] = value;
        }

        private object GetReg(int rBase, byte reg)
        {
            int target = rBase + reg;
            if (target < 0 || target >= _regs.Length)
            {
                HaltWithError($"Register OOB: {target}");
                return null;
            }
            return _regs[target];
        }

        public void Step(int instructionsCount)
        {
            if (IsFinished) return;
            var code = Module.Code;

            if (code == null)
            {
                HaltWithError("Null bytecode array");
                return;
            }

            int executed = 0;

            try
            {
                while (executed < instructionsCount && !IsFinished)
                {
                    if (IP < 0 || IP >= code.Length)
                    {
                        HaltWithError($"IP out of bounds ({IP})");
                        return;
                    }

                    if (_frames.Count == 0)
                    {
                        IsFinished = true;
                        return;
                    }

                    var frame = _frames[_frames.Count - 1];
                    int rBase = frame.RegBase;

                    VmOpcode op = (VmOpcode)code[IP++];

                    switch (op)
                    {
                        case VmOpcode.Nop:
                            break;

                        case VmOpcode.LoadNull:
                            if (!CheckBounds(1)) return;
                            SetReg(rBase, code[IP++], null);
                            break;

                        case VmOpcode.LoadTrue:
                            if (!CheckBounds(1)) return;
                            SetReg(rBase, code[IP++], true);
                            break;

                        case VmOpcode.LoadFalse:
                            if (!CheckBounds(1)) return;
                            SetReg(rBase, code[IP++], false);
                            break;

                        case VmOpcode.LoadI32:
                            if (!CheckBounds(5)) return;
                            byte aI32 = code[IP++];
                            int valI32 = code[IP] | (code[IP + 1] << 8) | (code[IP + 2] << 16) | (code[IP + 3] << 24);
                            IP += 4;
                            SetReg(rBase, aI32, valI32);
                            break;

                        case VmOpcode.LoadF64:
                            if (!CheckBounds(5)) return;
                            byte aF64 = code[IP++];
                            float valF64 = System.BitConverter.ToSingle(code, IP);
                            IP += 4;
                            SetReg(rBase, aF64, valF64);
                            break;

                        case VmOpcode.LoadStr:
                            if (!CheckBounds(3)) return;
                            byte aStr = code[IP++];
                            int strIdx = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            if (strIdx >= 0 && strIdx < StringPool.Count)
                            {
                                SetReg(rBase, aStr, StringPool[strIdx]);
                            }
                            else
                            {
                                SetReg(rBase, aStr, "");
                            }
                            break;

                        case VmOpcode.Mov:
                            if (!CheckBounds(2)) return;
                            byte aMov = code[IP++];
                            byte bMov = code[IP++];
                            SetReg(rBase, aMov, GetReg(rBase, bMov));
                            break;

                        case VmOpcode.Add:
                        case VmOpcode.Sub:
                        case VmOpcode.Mul:
                        case VmOpcode.Div:
                            if (!CheckBounds(3)) return;
                            byte aAr = code[IP++];
                            byte bAr = code[IP++];
                            byte cAr = code[IP++];

                            object left = GetReg(rBase, bAr) ?? 0;
                            object right = GetReg(rBase, cAr) ?? 0;

                            if (op == VmOpcode.Add && (left is string || right is string))
                            {
                                SetReg(rBase, aAr, left.ToString() + right.ToString());
                            }
                            else if (left is float || right is float)
                            {
                                float fl = SafeToFloat(left);
                                float fr = SafeToFloat(right);

                                if (op == VmOpcode.Add) SetReg(rBase, aAr, fl + fr);
                                else if (op == VmOpcode.Sub) SetReg(rBase, aAr, fl - fr);
                                else if (op == VmOpcode.Mul) SetReg(rBase, aAr, fl * fr);
                                else if (op == VmOpcode.Div) SetReg(rBase, aAr, (fr == 0) ? 0 : fl / fr);
                            }
                            else
                            {
                                int il = SafeToInt(left);
                                int ir = SafeToInt(right);

                                if (op == VmOpcode.Add) SetReg(rBase, aAr, il + ir);
                                else if (op == VmOpcode.Sub) SetReg(rBase, aAr, il - ir);
                                else if (op == VmOpcode.Mul) SetReg(rBase, aAr, il * ir);
                                else if (op == VmOpcode.Div) SetReg(rBase, aAr, (ir == 0) ? 0 : il / ir);
                            }
                            break;

                        case VmOpcode.BitAnd:
                        case VmOpcode.BitOr:
                        case VmOpcode.Shl:
                        case VmOpcode.Shr:
                            if (!CheckBounds(3)) return;
                            byte aBit = code[IP++]; byte bBit = code[IP++]; byte cBit = code[IP++];
                            int lBit = SafeToInt(GetReg(rBase, bBit));
                            int rBit = SafeToInt(GetReg(rBase, cBit));

                            if (op == VmOpcode.BitAnd) SetReg(rBase, aBit, lBit & rBit);
                            else if (op == VmOpcode.BitOr) SetReg(rBase, aBit, lBit | rBit);
                            else if (op == VmOpcode.Shl) SetReg(rBase, aBit, lBit << rBit);
                            else if (op == VmOpcode.Shr) SetReg(rBase, aBit, lBit >> rBit);
                            break;

                        case VmOpcode.Eq:
                        case VmOpcode.Neq:
                        case VmOpcode.Lt:
                        case VmOpcode.Le:
                        case VmOpcode.Gt:
                        case VmOpcode.Ge:
                            if (!CheckBounds(3)) return;
                            byte aCp = code[IP++];
                            byte bCp = code[IP++];
                            byte cCp = code[IP++];

                            object lCmp = GetReg(rBase, bCp);
                            object rCmp = GetReg(rBase, cCp);
                            bool res = false;

                            if (lCmp is float || rCmp is float)
                            {
                                float fl = SafeToFloat(lCmp);
                                float fr = SafeToFloat(rCmp);

                                if (op == VmOpcode.Eq) res = fl == fr;
                                else if (op == VmOpcode.Neq) res = fl != fr;
                                else if (op == VmOpcode.Lt) res = fl < fr;
                                else if (op == VmOpcode.Le) res = fl <= fr;
                                else if (op == VmOpcode.Gt) res = fl > fr;
                                else if (op == VmOpcode.Ge) res = fl >= fr;
                            }
                            else if (lCmp is string sl && rCmp is string sr)
                            {
                                if (op == VmOpcode.Eq) res = sl == sr;
                                else if (op == VmOpcode.Neq) res = sl != sr;
                            }
                            else
                            {
                                int il = SafeToInt(lCmp);
                                int ir = SafeToInt(rCmp);

                                if (op == VmOpcode.Eq) res = il == ir;
                                else if (op == VmOpcode.Neq) res = il != ir;
                                else if (op == VmOpcode.Lt) res = il < ir;
                                else if (op == VmOpcode.Le) res = il <= ir;
                                else if (op == VmOpcode.Gt) res = il > ir;
                                else if (op == VmOpcode.Ge) res = il >= ir;
                            }
                            SetReg(rBase, aCp, res);
                            break;

                        // === OOPS / ARRAYS / STRUCTS === //
                        case VmOpcode.NewArrayEmpty:
                            if (!CheckBounds(1)) return;
                            SetReg(rBase, code[IP++], new List<object>());
                            break;

                        case VmOpcode.NewArray:
                            if (!CheckBounds(2)) return;
                            byte aNa = code[IP++];
                            int sizeNa = SafeToInt(GetReg(rBase, code[IP++]));

                            var lstNa = new List<object>();
                            for (int i = 0; i < sizeNa; i++)
                            {
                                lstNa.Add(null);
                            }
                            SetReg(rBase, aNa, lstNa);
                            break;

                        case VmOpcode.ArrPush:
                            if (!CheckBounds(2)) return;
                            byte aAp = code[IP++];
                            byte bAp = code[IP++];

                            if (GetReg(rBase, aAp) is List<object> lstAp)
                            {
                                lstAp.Add(GetReg(rBase, bAp));
                            }
                            else
                            {
                                HaltWithError("ArrPush error: Target is not an array.");
                            }
                            break;

                        case VmOpcode.NewByteArray:
                            if (!CheckBounds(2)) return;
                            byte aNba = code[IP++];
                            int sizeNba = SafeToInt(GetReg(rBase, code[IP++]));
                            SetReg(rBase, aNba, new byte[sizeNba]);
                            break;

                        case VmOpcode.ArrLoad:
                            if (!CheckBounds(3)) return;
                            byte aAl = code[IP++];
                            byte bAl = code[IP++];
                            byte cAl = code[IP++];

                            object targetAl = GetReg(rBase, bAl);
                            int idxAl = SafeToInt(GetReg(rBase, cAl));

                            if (targetAl is List<object> lstAl)
                            {
                                SetReg(rBase, aAl, (idxAl >= 0 && idxAl < lstAl.Count) ? lstAl[idxAl] : null);
                            }
                            else if (targetAl is byte[] bArrAl) // Читаем из bytearray
                            {
                                SetReg(rBase, aAl, (idxAl >= 0 && idxAl < bArrAl.Length) ? (int)bArrAl[idxAl] : 0);
                            }
                            else HaltWithError("ArrLoad error: Target is not an array.");
                            break;

                        case VmOpcode.ArrStore:
                            if (!CheckBounds(3)) return;
                            byte aAs = code[IP++];
                            byte bAs = code[IP++];
                            byte cAs = code[IP++];

                            object targetAs = GetReg(rBase, aAs);
                            int idxAs = SafeToInt(GetReg(rBase, bAs));

                            if (targetAs is List<object> lstAs)
                            {
                                if (idxAs >= 0 && idxAs < lstAs.Count) lstAs[idxAs] = GetReg(rBase, cAs);
                                else HaltWithError($"Index out of bounds ({idxAs}).");
                            }
                            else if (targetAs is byte[] bArrAs) // Пишем в bytearray
                            {
                                if (idxAs >= 0 && idxAs < bArrAs.Length) bArrAs[idxAs] = (byte)(SafeToInt(GetReg(rBase, cAs)) & 0xFF);
                                else HaltWithError($"Index out of bounds ({idxAs}).");
                            }
                            else HaltWithError("ArrStore error: Target is not an array.");
                            break;

                        case VmOpcode.NewStruct:
                            if (!CheckBounds(1)) return;
                            SetReg(rBase, code[IP++], new Dictionary<string, object>());
                            break;

                        case VmOpcode.GetField:
                            if (!CheckBounds(4)) return;
                            byte aGf = code[IP++];
                            byte bGf = code[IP++];
                            int sGf = code[IP] | (code[IP + 1] << 8);
                            IP += 2;

                            if (GetReg(rBase, bGf) is Dictionary<string, object> dictGf)
                            {
                                SetReg(rBase, aGf, dictGf.TryGetValue(StringPool[sGf], out var v) ? v : null);
                            }
                            else
                            {
                                HaltWithError($"GetField error: Target '{StringPool[sGf]}' is not an object.");
                            }
                            break;

                        case VmOpcode.SetField:
                            if (!CheckBounds(4)) return;
                            byte aSf = code[IP++];
                            int sSf = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            byte cSf = code[IP++];

                            if (GetReg(rBase, aSf) is Dictionary<string, object> dictSf)
                            {
                                dictSf[StringPool[sSf]] = GetReg(rBase, cSf);
                            }
                            else
                            {
                                HaltWithError($"SetField error: Target is not an object.");
                            }
                            break;

                        case VmOpcode.PushFunc:
                            if (!CheckBounds(3)) return;
                            byte aPf = code[IP++];
                            int fidxPf = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            SetReg(rBase, aPf, fidxPf); // Сохраняем ID функции как int
                            break;

                        case VmOpcode.CallInd:
                        case VmOpcode.CallIndVoid:
                            bool hasDst = op == VmOpcode.CallInd;
                            if (!CheckBounds(hasDst ? 4 : 3)) return;

                            byte dstCi = hasDst ? code[IP++] : (byte)255;
                            byte rFunc = code[IP++];
                            byte rBaseCi = code[IP++];
                            byte argcCi = code[IP++];

                            object rawFunc = GetReg(rBase, rFunc);
                            if (!(rawFunc is int fidxCi))
                            {
                                HaltWithError("CallInd error: Object is not a function.");
                                return;
                            }
                            if (fidxCi < 0 || fidxCi >= _funcIps.Length)
                            {
                                HaltWithError("CallInd error: Invalid function pointer.");
                                return;
                            }

                            var frameCi = new CallFrame
                            {
                                ReturnIp = IP,
                                FuncIndex = fidxCi,
                                DstReg = dstCi,
                                RegBase = _regTop,
                                NumRegs = _funcNumRegs[fidxCi]
                            };

                            for (int i = 0; i < argcCi; i++)
                            {
                                _regs[_regTop + i] = GetReg(rBase, (byte)(rBaseCi + i));
                            }

                            _regTop += frameCi.NumRegs;
                            _frames.Add(frameCi);
                            IP = _funcIps[fidxCi];
                            break;

                        // ============================ //

                        case VmOpcode.GInit:
                        case VmOpcode.GStore:
                            if (!CheckBounds(3)) return;
                            int idxStore = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            byte aGs = code[IP++];

                            while (Globals.Count <= idxStore)
                            {
                                Globals.Add(null);
                            }
                            Globals[idxStore] = GetReg(rBase, aGs);
                            break;

                        case VmOpcode.GLoad:
                            if (!CheckBounds(3)) return;
                            byte aGl = code[IP++];
                            int idxLoad = code[IP] | (code[IP + 1] << 8);
                            IP += 2;

                            if (idxLoad >= 0 && idxLoad < Globals.Count)
                            {
                                SetReg(rBase, aGl, Globals[idxLoad]);
                            }
                            else
                            {
                                SetReg(rBase, aGl, null);
                            }
                            break;

                        case VmOpcode.Jmp:
                            if (!CheckBounds(2)) return;
                            short jmpOff = (short)(code[IP] | (code[IP + 1] << 8));
                            IP += 2;
                            IP += jmpOff;
                            break;

                        case VmOpcode.JmpFalse:
                            if (!CheckBounds(3)) return;
                            byte aJf = code[IP++];
                            short jmpfOff = (short)(code[IP] | (code[IP + 1] << 8));
                            IP += 2;
                            object valJf = GetReg(rBase, aJf);

                            if (valJf == null || (valJf is bool bv && !bv) || (valJf is int iv && iv == 0))
                            {
                                IP += jmpfOff;
                            }
                            break;

                        case VmOpcode.CallVoid:
                            if (!CheckBounds(4)) return;
                            int fidxVoid = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            byte bRegVoid = code[IP++];
                            byte argcVoid = code[IP++];

                            if (fidxVoid < 0 || fidxVoid >= _funcIps.Length)
                            {
                                HaltWithError($"Invalid function ID {fidxVoid}");
                                return;
                            }

                            var frameVoid = new CallFrame
                            {
                                ReturnIp = IP,
                                FuncIndex = fidxVoid,
                                DstReg = 255,
                                RegBase = _regTop,
                                NumRegs = _funcNumRegs[fidxVoid]
                            };

                            for (int i = 0; i < argcVoid; i++)
                            {
                                _regs[_regTop + i] = GetReg(rBase, (byte)(bRegVoid + i));
                            }

                            _regTop += frameVoid.NumRegs;
                            _frames.Add(frameVoid);
                            IP = _funcIps[fidxVoid];
                            break;

                        case VmOpcode.Call:
                            if (!CheckBounds(5)) return;
                            byte dstCall = code[IP++];
                            int fidxCall = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            byte bRegCall = code[IP++];
                            byte argcCall = code[IP++];

                            if (fidxCall < 0 || fidxCall >= _funcIps.Length)
                            {
                                HaltWithError($"Invalid function ID {fidxCall}");
                                return;
                            }

                            var frameCall = new CallFrame
                            {
                                ReturnIp = IP,
                                FuncIndex = fidxCall,
                                DstReg = dstCall,
                                RegBase = _regTop,
                                NumRegs = _funcNumRegs[fidxCall]
                            };

                            for (int i = 0; i < argcCall; i++)
                            {
                                _regs[_regTop + i] = GetReg(rBase, (byte)(bRegCall + i));
                            }

                            _regTop += frameCall.NumRegs;
                            _frames.Add(frameCall);
                            IP = _funcIps[fidxCall];
                            break;

                        case VmOpcode.Ret:
                            if (!CheckBounds(1)) return;
                            byte rRet = code[IP++];
                            object retVal = GetReg(rBase, rRet);
                            byte retDst = frame.DstReg;

                            IP = frame.ReturnIp;
                            _regTop = frame.RegBase;
                            _frames.RemoveAt(_frames.Count - 1);

                            if (_frames.Count > 0 && retDst != 255)
                            {
                                SetReg(_frames[_frames.Count - 1].RegBase, retDst, retVal);
                            }
                            break;

                        case VmOpcode.RetVoid:
                            IP = frame.ReturnIp;
                            _regTop = frame.RegBase;
                            _frames.RemoveAt(_frames.Count - 1);
                            break;

                        case VmOpcode.FuncBegin:
                            if (!CheckBounds(3)) return;
                            IP += 3;
                            break;

                        case VmOpcode.FuncEnd:
                            IP = frame.ReturnIp;
                            _regTop = frame.RegBase;
                            _frames.RemoveAt(_frames.Count - 1);
                            break;

                        case VmOpcode.CallNativeVoid:
                            if (!CheckBounds(4)) return;
                            int nid = code[IP] | (code[IP + 1] << 8);
                            IP += 2;
                            byte bRegN = code[IP++];
                            byte argcN = code[IP++];

                            var argsList = new List<object>();
                            for (int i = 0; i < argcN; i++)
                            {
                                argsList.Add(GetReg(rBase, (byte)(bRegN + i)));
                            }

                            if (!VmRuntime.CallHost((byte)nid, argsList, out string err))
                            {
                                HaltWithError(err);
                                return;
                            }
                            break;

                        case VmOpcode.End:
                            IsFinished = true;
                            return;

                        default:
                            HaltWithError($"Unknown opcode {(byte)op:X2} at IP {IP - 1}");
                            return;
                    }
                    executed++;
                }

                if (IP >= code.Length) IsFinished = true;
            }
            catch (System.Exception ex)
            {
                HaltWithError($"Segmentation fault (Corrupted BVX memory at IP {IP}). Sys details: {ex.Message}");
            }
        }
    }
}