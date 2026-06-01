using System;
using System.Linq;
using BAZOS.Drivers;

namespace BAZOS.Api.Commands
{
    public sealed class GroupCommand : Shell.ICommand
    {
        public string Name => "group";
        public string[] Aliases => Array.Empty<string>();
        public string? Help => "Groups (list/add/remove/set/whoami)";

        public void Execute(Shell.CommandContext ctx)
        {
            var args = ctx.Args ?? Array.Empty<string>();
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            string sub = args[0];

            if (Eq(sub, "list"))
            {
                foreach (var g in SecurityContext.AllGroups.OrderBy(x => x.Id))
                    Console.WriteLine($"{g.Id}: {g.Name}");
                return;
            }

            if (Eq(sub, "add"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: group add <name>");
                    return;
                }
                if (!SecurityContext.AddGroup(args[1], out var id))
                {
                    Console.WriteLine("group: failed");
                    return;
                }
                Console.WriteLine($"OK (id={id})");
                return;
            }

            if (Eq(sub, "remove"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: group remove <name|id>");
                    return;
                }
                Console.WriteLine(SecurityContext.RemoveGroup(args[1]) ? "OK" : "FAIL");
                return;
            }

            if (Eq(sub, "set"))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: group set <name|id>");
                    return;
                }
                Console.WriteLine(SecurityContext.TrySetCurrentGroup(args[1]) ? "OK" : "FAIL");
                return;
            }

            if (Eq(sub, "whoami"))
            {
                Console.WriteLine($"User: {SecurityContext.CurrentUserName}");
                Console.WriteLine($"Group: {SecurityContext.CurrentGroupName} ({SecurityContext.CurrentGroupId})");
                return;
            }

            PrintUsage();
        }

        private static bool Eq(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  group list");
            Console.WriteLine("  group add <name>");
            Console.WriteLine("  group remove <name|id>");
            Console.WriteLine("  group set <name|id>");
            Console.WriteLine("  group whoami");
        }
    }
}

