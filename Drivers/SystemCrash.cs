using System;
using System.Threading;

namespace BAZOS.Drivers
{
    public static class SystemCrash
    {
        public static void ShowCritical(DriverStatus status, DriverPackage? pkg = null, Exception? ex = null)
        {
            try
            {
                Console.Clear();
                Console.WriteLine("==================================================");
                Console.WriteLine("              CRITICAL SYSTEM FAILURE             ");
                Console.WriteLine("==================================================");
                Console.WriteLine();
                Console.WriteLine("System failed to boot because a required driver failed.");
                Console.WriteLine();

                Console.WriteLine("Summary:");
                Console.WriteLine($"  Driver : {status.DriverId}");
                Console.WriteLine($"  State  : {status.State}");
                Console.WriteLine($"  Reason : {ToReasonCode(status.Reason)}");
                Console.WriteLine($"  Phase  : {status.Phase}");
                Console.WriteLine($"  Time   : {status.UpdatedAtUtc:O}");
                Console.WriteLine();

                Console.WriteLine("Details:");
                Console.WriteLine($"  Message   : {status.Message}");
                if (pkg != null)
                {
                    Console.WriteLine($"  Package   : {pkg.PackagePath}");
                    Console.WriteLine($"  Runtime   : {pkg.Runtime}");
                    Console.WriteLine($"  EntryInit : {pkg.EntryInit}");
                    Console.WriteLine($"  Required  : {pkg.IsRequired}");
                }
                Console.WriteLine();

                if (ex != null)
                {
                    Console.WriteLine("Technical:");
                    Console.WriteLine($"  Exception: {ex.GetType().FullName}");
                    Console.WriteLine($"  Error    : {ex.Message}");
                    if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    {
                        Console.WriteLine("  Stack:");
                        Console.WriteLine(ex.StackTrace);
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("Power cycle required. Press Reset.");
            }
            catch (Exception crashEx)
            {
                // Last-resort fallback if console rendering fails.
                Console.WriteLine("CRITICAL SYSTEM FAILURE");
                Console.WriteLine($"Crash screen failed: {crashEx.Message}");
            }

            while (true)
                Thread.Sleep(1000);
        }

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
