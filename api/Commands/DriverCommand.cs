using BAZOS.Drivers;
using System;
using System.Linq;

namespace BAZOS.Api.Commands
{
    public sealed class DriverCommand : Shell.ICommand
    {
        private readonly DriverManager _manager;

        public DriverCommand(DriverManager manager)
        {
            _manager = manager;
        }

        public string Name => "driver";
        public string[] Aliases => new[] { "drv" };
        public string? Help => "Driver management (list/info/enable/disable/verify)";

        public void Execute(Shell.CommandContext ctx)
        {
            var args = ctx.Args ?? Array.Empty<string>();
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            string sub = args[0];

            if (Eq(sub, "reload"))
            {
                _manager.Reload();
                Console.WriteLine("OK");
                return;
            }

            if (Eq(sub, "list"))
            {
                foreach (var p in _manager.Packages.OrderBy(x => x.DriverId, StringComparer.OrdinalIgnoreCase))
                {
                    bool enabled = _manager.IsEnabled(p.DriverId);
                    bool ok = _manager.VerifyPackage(p, out var msg);
                    Console.WriteLine($"{p.DriverId}  v{p.DriverVersion}  {(enabled ? "enabled" : "disabled")}  {(ok ? "verified" : "blocked")}  {msg}");
                }
                return;
            }

            if (Eq(sub, "info"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: driver info <id>");
                    return;
                }

                if (!_manager.TryGetPackage(args[1], out var p))
                {
                    Console.WriteLine("driver: not found");
                    return;
                }

                Console.WriteLine($"Id: {p.DriverId}");
                Console.WriteLine($"Version: {p.DriverVersion}");
                Console.WriteLine($"Type: {p.DriverType}");
                Console.WriteLine($"Path: {p.PackagePath}");
                Console.WriteLine($"Enabled: {_manager.IsEnabled(p.DriverId)}");
                Console.WriteLine($"PubKeyId: {p.PubKeyId}");
                Console.WriteLine($"HasSignature: {p.SignatureBytes.Length > 0}");

                bool ok = _manager.VerifyPackage(p, out var msg);
                Console.WriteLine($"Verify: {(ok ? "OK" : "FAIL")} ({msg})");
                return;
            }

            if (Eq(sub, "status"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: driver status <id>");
                    return;
                }

                string id = args[1];
                if (!_manager.TryGetStatus(id, out var st))
                {
                    Console.WriteLine("driver: status not found");
                    return;
                }

                Console.WriteLine($"Id: {st.DriverId}");
                Console.WriteLine($"State: {st.State}");
                Console.WriteLine($"Reason: {ToReasonCode(st.Reason)}");
                Console.WriteLine($"Phase: {st.Phase}");
                Console.WriteLine($"Message: {st.Message}");
                Console.WriteLine($"Enabled: {st.Enabled}");
                Console.WriteLine($"UpdatedAtUtc: {st.UpdatedAtUtc:O}");

                if (_manager.TryGetPackage(st.DriverId, out var p))
                {
                    Console.WriteLine($"Required: {p.IsRequired}");
                    Console.WriteLine($"Runtime: {p.Runtime}");
                }

                return;
            }

            if (Eq(sub, "verify"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: driver verify <id>");
                    return;
                }

                bool ok = _manager.VerifyPackage(args[1], out var msg);
                Console.WriteLine(ok ? $"OK ({msg})" : $"FAIL ({msg})");
                return;
            }

            if (Eq(sub, "enable") || Eq(sub, "disable"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine($"Usage: driver {sub} <id>");
                    return;
                }

                if (Eq(sub, "enable"))
                    _manager.Enable(args[1]);
                else
                    _manager.Disable(args[1]);

                Console.WriteLine("OK");
                return;
            }

            PrintUsage();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  driver reload");
            Console.WriteLine("  driver list");
            Console.WriteLine("  driver info <id>");
            Console.WriteLine("  driver status <id>");
            Console.WriteLine("  driver verify <id>");
            Console.WriteLine("  driver enable <id>");
            Console.WriteLine("  driver disable <id>");
        }

        private static bool Eq(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string ToReasonCode(DriverErrorReason reason)
        {
            return reason switch
            {
                DriverErrorReason.BadSignature => "bad_signature",
                DriverErrorReason.MissingDep => "missing_dep",
                DriverErrorReason.InitFailed => "init_failed",
                DriverErrorReason.PolicyBlocked => "policy_blocked",
                _ => "none"
            };
        }
    }
}

