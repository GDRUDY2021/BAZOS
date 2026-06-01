using System;
using System.Linq;
using BAZOS.Drivers;
using BAZOS.FS;

namespace BAZOS.Api.Commands
{
    public sealed class FsCommand : Shell.ICommand
    {
        public string Name => "fs";
        public string[] Aliases => Array.Empty<string>();
        public string? Help => "Filesystem (mkdir/rmdir/mkfile/rmfile/perm)";

        public void Execute(Shell.CommandContext ctx)
        {
            if (!BazFs.IsMounted)
            {
                Console.WriteLine("fs: FS is not mounted.");
                return;
            }

            if (ctx.HasOption("mkdir"))
            {
                if (ctx.Args.Length < 1) { Console.WriteLine("Usage: fs /mkdir <path>"); return; }
                BazFs.CreateDirectory(ctx.Args[0]);
                return;
            }

            if (ctx.HasOption("rmdir"))
            {
                if (ctx.Args.Length < 1) { Console.WriteLine("Usage: fs /rmdir <path> [/f]"); return; }
                BazFs.RemoveDirectory(ctx.Args[0], force: ctx.HasOption("f"));
                return;
            }

            if (ctx.HasOption("mkfile"))
            {
                if (ctx.Args.Length < 1) { Console.WriteLine("Usage: fs /mkfile <path> [data...] [/o]"); return; }
                string path = ctx.Args[0];
                bool overwrite = ctx.HasOption("o");
                string content = ctx.Args.Length > 1 ? string.Join(' ', ctx.Args.Skip(1)) : string.Empty;
                var bytes = System.Text.Encoding.ASCII.GetBytes(content);
                BazFs.CreateFileWithPath(path, bytes, overwrite);
                return;
            }

            if (ctx.HasOption("rmfile"))
            {
                if (ctx.Args.Length < 1) { Console.WriteLine("Usage: fs /rmfile <path>"); return; }
                BazFs.DeleteFile(ctx.Args[0]);
                return;
            }

            var permOpt = ctx.GetOption("perm");
            if (!string.IsNullOrWhiteSpace(permOpt))
            {
                HandlePerm(permOpt!, ctx);
                return;
            }

            PrintUsage();
        }

        private static void HandlePerm(string mode, Shell.CommandContext ctx)
        {
            string path = ctx.Args.Length > 0 ? ctx.Args[0] : ".";

            if (string.Equals(mode, "get", StringComparison.OrdinalIgnoreCase))
            {
                if (!BazFs.TryGetPerm(path, out var p))
                {
                    Console.WriteLine("fs: perm not found");
                    return;
                }
                Console.WriteLine($"owner_gid={p.OwnerGroupId}");
                Console.WriteLine($"owner={ToPermText(p.PermOwner)} other={ToPermText(p.PermOther)} inherit={(p.Inherit != 0 ? 1 : 0)}");
                return;
            }

            if (string.Equals(mode, "set", StringComparison.OrdinalIgnoreCase))
            {
                byte owner = pbyte(ctx.GetOption("r"), ctx.GetOption("w"), ctx.GetOption("x"), ctx.GetOption("d"));
                byte other = pbyte(ctx.GetOption("or"), ctx.GetOption("ow"), ctx.GetOption("ox"), ctx.GetOption("od"));
                byte inherit = (byte)(ctx.HasOption("inherit") ? 1 : 0);

                if (!BazFs.TrySetPerm(path, owner, other, inherit))
                {
                    Console.WriteLine("fs: failed");
                    return;
                }
                Console.WriteLine("OK");
                return;
            }

            Console.WriteLine("Usage: fs /perm=get [path] | fs /perm=set [path] /r=0|1 /w=0|1 /x=0|1 /d=0|1");

            static byte pbyte(string? r, string? w, string? x, string? d)
            {
                byte p = 0;
                if (r == "1") p |= BazFs.PermR;
                if (w == "1") p |= BazFs.PermW;
                if (x == "1") p |= BazFs.PermX;
                if (d == "1") p |= BazFs.PermD;
                return p;
            }
        }

        private static string ToPermText(byte p)
        {
            char r = (p & BazFs.PermR) != 0 ? 'r' : '-';
            char w = (p & BazFs.PermW) != 0 ? 'w' : '-';
            char x = (p & BazFs.PermX) != 0 ? 'x' : '-';
            char d = (p & BazFs.PermD) != 0 ? 'd' : '-';
            return new string(new[] { r, w, x, d });
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  fs /mkdir <path>");
            Console.WriteLine("  fs /rmdir <path> [/f]");
            Console.WriteLine("  fs /mkfile <path> [data...] [/o]");
            Console.WriteLine("  fs /rmfile <path>");
            Console.WriteLine("  fs /perm=get [path]");
            Console.WriteLine("  fs /perm=set [path] /r=0|1 /w=0|1 /x=0|1 /d=0|1");
        }
    }
}

