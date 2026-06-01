using BAZOS.Api;
using BAZOS.Drivers;
using Cosmos.Kernel.HAL;
using System.Text;

namespace BAZOS.Runtime
{
    public static class VmRuntime
    {
        public static bool CallHost(byte id, List<object> stack, out string error)
        {
            error = "";
            switch (id)
            {
                case VmHostCall.Log:
                {
                    if (!TryPopString(stack, out var msg)) { error = "host.log: missing arg"; return false; }
                    Console.WriteLine(msg);
                    return true;
                }

                case VmHostCall.Print:
                {
                    if (!TryPopString(stack, out var text)) { error = "host.print: missing arg"; return false; }
                    Console.Write(text);
                    return true;
                }

                case VmHostCall.Clear:
                {
                    Console.Clear();
                    return true;
                }

                case VmHostCall.System:
                {
                    if (stack.Count == 0) { error = "host.system: missing arg"; return false; }

                    string cmd = stack[0]?.ToString() ?? "";
                    int argIndex = 1;
                    var sb = new StringBuilder();

                    bool IsValidStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
                    bool IsValidPart(char c) => IsValidStart(c) || (c >= '0' && c <= '9');

                    for (int i = 0; i < cmd.Length; i++)
                    {
                        if (cmd[i] == '$' && i + 1 < cmd.Length && IsValidStart(cmd[i + 1]))
                        {
                            i++;
                            int startName = i;
                            while (i < cmd.Length && IsValidPart(cmd[i])) i++;

                            string varName = cmd.Substring(startName, i - startName);
                            i--; // Возвращаемся на последний символ имени

                            if (argIndex < stack.Count)
                            {
                                var val = stack[argIndex];
                                sb.Append(val != null ? val.ToString() : "");
                                argIndex++;
                            }
                            else
                            {
                                error = $"Syscall Error: Missing argument for variable '${varName}' in format string.";
                                return false;
                            }
                        }
                        else
                        {
                            sb.Append(cmd[i]);
                        }
                    }

                    Shell.RunCommand(sb.ToString());
                    return true;
                }

                case VmHostCall.ReadLine:
                {
                    string input = InputBus.ReadLine();
                    stack.Add(input);
                    return true;
                }

                case VmHostCall.RegisterDevice:
                {
                    if (!TryPopInt(stack, out var enabledI) || !TryPopString(stack, out var name) ||
                        !TryPopString(stack, out var typeName) || !TryPopString(stack, out var idStr))
                    { error = "host.register_device: bad args"; return false; }

                    DeviceType type = DeviceType.Other;
                    try { Enum.TryParse(typeName, ignoreCase: true, out type); } catch { }

                    DeviceManager.RegisterDevice(new DeviceDescriptor { Id = idStr, Type = type, Name = name, Enabled = enabledI != 0 });
                    return true;
                }

                case VmHostCall.SetProp:
                {
                    if (!TryPopString(stack, out var value) || !TryPopString(stack, out var key) || !TryPopString(stack, out var devId))
                    { error = "host.set_prop: bad args"; return false; }
                    if (!DeviceManager.SetProp(devId, key, value))
                    { error = $"host.set_prop: device \"{devId}\" not found"; return false; }
                    return true;
                }

                case VmHostCall.KeyboardEnable:
                case VmHostCall.InputSetEnabled:
                {
                    if (!TryPopInt(stack, out var enabledI)) { error = "host.input_set_enabled: bad args"; return false; }
                    InputBus.SetKeyboardEnabled(enabledI != 0);
                    return true;
                }

                case VmHostCall.KeyboardPush:
                case VmHostCall.InputEnqueue:
                {
                    if (!TryPopInt(stack, out var shiftI) || !TryPopInt(stack, out var altI) ||
                        !TryPopInt(stack, out var ctrlI) || !TryPopInt(stack, out var keyCharI) || !TryPopInt(stack, out var keyCodeI))
                    { error = "host.input: bad args"; return false; }

                    var evt = new KeyboardEvent((ConsoleKey)keyCodeI, (char)(keyCharI & 0xFF), ctrlI != 0, altI != 0, shiftI != 0);
                    InputBus.PushKey(evt);
                    return true;
                }

                case VmHostCall.KeyboardReadRaw:
                {
                    stack.Add(0); stack.Add(0); stack.Add(0); stack.Add(0); stack.Add(0);
                    return true;
                }

                case VmHostCall.PortRead8:
                {
                    if (!TryPopInt(stack, out var port)) { error = "host.port_read8: bad args"; return false; }
                    byte val = PlatformHAL.PortIO.ReadByte((ushort)port);
                    stack.Add((int)val);
                    return true;
                }

                case VmHostCall.PortWrite8:
                {
                    if (!TryPopInt(stack, out var val) || !TryPopInt(stack, out var port))
                    { error = "host.port_write8: bad args"; return false; }
                    PlatformHAL.PortIO.WriteByte((ushort)port, (byte)val);
                    return true;
                    }

                default:
                {
                    error = $"unknown host call {id}";
                    return false;
                }
            }
        }

        private static bool TryPopString(List<object> stack, out string value)
        {
            value = "";
            if (stack.Count == 0) return false;
            int idx = stack.Count - 1;
            var obj = stack[idx];
            stack.RemoveAt(idx);
            if (obj is string s) { value = s; return true; }
            return false;
        }

        private static bool TryPopInt(List<object> stack, out int value)
        {
            value = 0;
            if (stack.Count == 0) return false;
            int idx = stack.Count - 1;
            var obj = stack[idx];
            stack.RemoveAt(idx);
            if (obj is int i) { value = i; return true; }
            return false;
        }
    }
}