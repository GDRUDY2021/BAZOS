using System;
using System.Collections.Generic;
using BAZOS.Drivers;

namespace BAZOS.Runtime
{
    public static class VmRuntime
    {
        public static bool RunInit(VmModule module, out string error)
        {
            error = "";
            if (module == null || module.Code == null)
            {
                error = "null module";
                return false;
            }

            int pc = 0;
            var stack = new List<object>();
            var code = module.Code;

            while (pc < code.Length)
            {
                VmOpcode op = (VmOpcode)code[pc++];

                switch (op)
                {
                    case VmOpcode.Nop:
                        break;

                    case VmOpcode.PushStr:
                        if (pc >= code.Length)
                        {
                            error = "PUSH_STR missing length";
                            return false;
                        }
                        int len = code[pc++];
                        if (pc + len > code.Length)
                        {
                            error = "PUSH_STR out of bounds";
                            return false;
                        }
                        var chars = new char[len];
                        for (int i = 0; i < len; i++)
                            chars[i] = (char)code[pc + i];
                        pc += len;
                        stack.Add(new string(chars));
                        break;

                    case VmOpcode.PushI32:
                        if (pc + 4 > code.Length)
                        {
                            error = "PUSH_I32 out of bounds";
                            return false;
                        }
                        int value = code[pc]
                                  | (code[pc + 1] << 8)
                                  | (code[pc + 2] << 16)
                                  | (code[pc + 3] << 24);
                        pc += 4;
                        stack.Add(value);
                        break;

                    case VmOpcode.CallHost:
                        if (pc >= code.Length)
                        {
                            error = "CALL_HOST missing id";
                            return false;
                        }
                        byte hostId = code[pc++];
                        if (!CallHost(hostId, stack, out error))
                            return false;
                        break;

                    case VmOpcode.Ret:
                    case VmOpcode.Halt:
                        return true;

                    default:
                        error = $"unknown opcode 0x{(byte)op:X2}";
                        return false;
                }
            }

            return true;
        }

        private static bool CallHost(byte id, List<object> stack, out string error)
        {
            error = "";

            switch (id)
            {
                case VmHostCall.Log:
                {
                    if (!TryPopString(stack, out var msg))
                    {
                        error = "host.log: missing arg";
                        return false;
                    }
                    Console.WriteLine($"[vm] {msg}");
                    return true;
                }

                case VmHostCall.RegisterDevice:
                {
                    if (!TryPopInt(stack, out var enabledI)
                        || !TryPopString(stack, out var name)
                        || !TryPopString(stack, out var typeName)
                        || !TryPopString(stack, out var idStr))
                    {
                        error = "host.register_device: bad args";
                        return false;
                    }

                    DeviceType type = DeviceType.Other;
                    Enum.TryParse(typeName, ignoreCase: true, out type);

                    DeviceManager.RegisterDevice(new DeviceDescriptor
                    {
                        Id = idStr,
                        Type = type,
                        Name = name,
                        Enabled = enabledI != 0
                    });

                    return true;
                }

                case VmHostCall.SetProp:
                {
                    if (!TryPopString(stack, out var value)
                        || !TryPopString(stack, out var key)
                        || !TryPopString(stack, out var devId))
                    {
                        error = "host.set_prop: bad args";
                        return false;
                    }

                    if (!DeviceManager.SetProp(devId, key, value))
                    {
                        error = $"host.set_prop: device \"{devId}\" not found";
                        return false;
                    }

                    return true;
                }

                default:
                    error = $"unknown host call {id}";
                    return false;
            }
        }

        private static bool TryPopString(List<object> stack, out string value)
        {
            value = "";
            if (stack.Count == 0)
                return false;
            int idx = stack.Count - 1;
            var obj = stack[idx];
            stack.RemoveAt(idx);
            if (obj is string s)
            {
                value = s;
                return true;
            }
            return false;
        }

        private static bool TryPopInt(List<object> stack, out int value)
        {
            value = 0;
            if (stack.Count == 0)
                return false;
            int idx = stack.Count - 1;
            var obj = stack[idx];
            stack.RemoveAt(idx);
            if (obj is int i)
            {
                value = i;
                return true;
            }
            return false;
        }
    }
}

