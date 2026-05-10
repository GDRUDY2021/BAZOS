using BAZOS.Drivers;
using System;
using System.Linq;

namespace BAZOS.Api.Commands
{
    public sealed class DeviceCommand : Shell.ICommand
    {
        public string Name => "device";
        public string[] Aliases => Array.Empty<string>();
        public string? Help => "Device management (list/info/enable/disable/status/config)";

        public void Execute(Shell.CommandContext ctx)
        {
            var args = ctx.Args ?? Array.Empty<string>();
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            string sub = args[0];

            if (Equals(sub, "list"))
            {
                foreach (var d in DeviceManager.All)
                    Console.WriteLine($"{d.Id}  {d.Type}  {(d.Enabled ? "on" : "off")}  {d.Name}");
                return;
            }

            if (Equals(sub, "info"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: device info <id|name>");
                    return;
                }

                var d = DeviceManager.Find(args[1]);
                if (d == null)
                {
                    Console.WriteLine("device: not found");
                    return;
                }

                Console.WriteLine($"Id: {d.Id}");
                Console.WriteLine($"Type: {d.Type}");
                Console.WriteLine($"Name: {d.Name}");
                Console.WriteLine($"Enabled: {d.Enabled}");
                if (d.Props.Count == 0)
                {
                    Console.WriteLine("Props: (empty)");
                }
                else
                {
                    Console.WriteLine("Props:");
                    foreach (var kv in d.Props.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {kv.Key}={kv.Value}");
                }
                return;
            }

            if (Equals(sub, "enable") || Equals(sub, "disable"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine($"Usage: device {sub} <id|name>");
                    return;
                }

                bool enabled = Equals(sub, "enable");
                if (!DeviceManager.SetEnabled(args[1], enabled))
                {
                    Console.WriteLine("device: not found");
                    return;
                }

                Console.WriteLine("OK");
                return;
            }

            if (Equals(sub, "status"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: device status <id|name>");
                    return;
                }

                var d = DeviceManager.Find(args[1]);
                if (d == null)
                {
                    Console.WriteLine("device: not found");
                    return;
                }

                Console.WriteLine($"{d.Id}: {(d.Enabled ? "on" : "off")}");
                return;
            }

            if (Equals(sub, "config"))
            {
                if (args.Length < 4)
                {
                    Console.WriteLine("Usage: device config <id|name> <key> <value>");
                    return;
                }

                string id = args[1];
                string key = args[2];
                string value = string.Join(' ', args.Skip(3));

                if (!DeviceManager.SetProp(id, key, value))
                {
                    Console.WriteLine("device: not found or invalid key");
                    return;
                }

                Console.WriteLine("OK");
                return;
            }

            // sugar: device mouse speed 8
            if (Equals(sub, "mouse") || Equals(sub, "keyboard") || Equals(sub, "audio"))
            {
                HandleDeviceSugar(sub, args);
                return;
            }

            PrintUsage();
        }

        private static void HandleDeviceSugar(string type, string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine($"Usage: device {type} <key> <value>");
                return;
            }

            string id = type switch
            {
                "mouse" => "mouse0",
                "keyboard" => "kbd0",
                "audio" => "audio0",
                _ => ""
            };

            if (string.IsNullOrEmpty(id))
            {
                Console.WriteLine("device: unknown type");
                return;
            }

            string key = args[1];
            string value = string.Join(' ', args.Skip(2));

            if (!DeviceManager.SetProp(id, key, value))
            {
                Console.WriteLine("device: not found");
                return;
            }

            Console.WriteLine("OK");
        }

        private static bool Equals(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  device list");
            Console.WriteLine("  device info <id|name>");
            Console.WriteLine("  device enable <id|name>");
            Console.WriteLine("  device disable <id|name>");
            Console.WriteLine("  device status <id|name>");
            Console.WriteLine("  device config <id|name> <key> <value>");
            Console.WriteLine("  device mouse <key> <value>");
            Console.WriteLine("  device keyboard <key> <value>");
            Console.WriteLine("  device audio <key> <value>");
        }
    }
}

