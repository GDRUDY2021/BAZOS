using BAZOS.FS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static System.Net.WebRequestMethods;
using Cosmos.Kernel.System;
using Cosmos.Kernel.Core.X64.Power;

namespace BAZOS.Api
{
    public static class Shell
    {

        public readonly struct CommandContext
        {
            public string Command { get; }
            public string[] Args { get; }
            public Dictionary<string, string?> Options { get; }

            public CommandContext(string command, string[] args, Dictionary<string, string?> options)
            {
                Command = command;
                Args = args;
                Options = options;
            }

            public bool HasOption(string name)
                => Options.ContainsKey(name);

            public string? GetOption(string name)
                => Options.TryGetValue(name, out var v) ? v : null;
        }

        private static readonly Dictionary<string, Action<CommandContext>> Commands = new(StringComparer.OrdinalIgnoreCase);
        private static readonly X64PowerOps p = new X64PowerOps();

        public static void Init()
        {
            RegisterCommands();
        }

        public static void RunCommand(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            var tokens = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;

            var cmd = tokens[0];
            var rawArgs = tokens.Skip(1).ToArray();

            var args = new List<string>();
            var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in rawArgs)
            {
                if (t.StartsWith("/"))
                {
                    // убираем '/'
                    var body = t.Substring(1);

                    // ищем разделитель = или :
                    int eq = body.IndexOf('=');
                    int colon = body.IndexOf(':');

                    int sep = eq >= 0 && colon >= 0
                        ? Math.Min(eq, colon)
                        : (eq >= 0 ? eq : colon);

                    if (sep < 0)
                    {
                        // просто флаг: /x
                        var key = body;
                        if (!string.IsNullOrEmpty(key))
                            options[key] = null;
                    }
                    else
                    {
                        var key = body.Substring(0, sep);
                        var val = body.Substring(sep + 1);
                        if (!string.IsNullOrEmpty(key))
                            options[key] = val;
                    }
                }
                else
                {
                    args.Add(t);
                }
            }

            if (Commands.TryGetValue(cmd, out var action))
            {
                try
                {
                    var ctx = new CommandContext(cmd, args.ToArray(), options);
                    action(ctx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"\"{cmd}\" is not a command.");
            }
        }

        private static void RegisterCommands()
        {
            Commands.Clear();

            Commands["help"] = Help;
            Commands["cls"] = _ => Console.Clear();
            Commands["clear"] = _ => Console.Clear();
            Commands["halt"] = Halt;

            Commands["dir"] = ListDirectory;
            Commands["cd"] = ChangeDirectory;
            Commands["mkdir"] = CreateDirectory;
            Commands["rmdir"] = RemoveDirectory;
            Commands["del"] = DeleteFile;
            Commands["copy"] = CopyFile;
            Commands["type"] = TypeFile;
            Commands["panic-ex"] = _ => ThrowTestException(); //test
            Commands["mkfs"] = Mkfs;
            Commands["mount"] = MountFs;
            Commands["dumpfs"] = DumpFs;
            Commands["dirfs"] = DirFs;
            Commands["mkfile"] = MkFile;
            Commands["catfs"] = CatFs; //удалить потом
            Commands["check"] = CheckEntry;
            Commands["power"] = Power;
        }

        private static void DumpFs(CommandContext ctx)
        {
            Console.WriteLine("Reading sector 0 via ATA...");

            Span<byte> buffer = stackalloc byte[512];
            if (AtaDisk.ReadSector(0, buffer))
            {
                Console.WriteLine("Sector 0 read OK. First bytes:");
                for (int i = 0; i < 2048; i++)
                    Console.Write($"{buffer[i]:X2} ");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("Failed to read sector 0.");
            }
        }

        private static void DirFs(CommandContext ctx)
        {
            BazFs.ListRoot();
        }

        private static void MkFile(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: mkfile <path> [data...] [/b] [/o]");
                return;
            }

            string path = args[0];
            bool binary = ctx.HasOption("b");
            bool overwrite = ctx.HasOption("o");

            var dataArgs = args.Skip(1).ToArray();

            byte[] bytes;

            if (binary)
            {
                // mkfile f 41 42 43 /b -> [0x41, 0x42, 0x43]
                if (dataArgs.Length == 0)
                {
                    bytes = Array.Empty<byte>();
                }
                else
                {
                    var list = new List<byte>();

                    foreach (var token in dataArgs)
                    {
                        // допускаем "41", "0x41", "ff" и т.п.
                        string t = token.Trim();

                        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            t = t.Substring(2);

                        if (t.Length == 0)
                            continue;

                        if (t.Length > 2)
                        {
                            Console.WriteLine($"Invalid byte '{token}', use 00..FF.");
                            return;
                        }

                        if (!byte.TryParse(t, System.Globalization.NumberStyles.HexNumber,  System.Globalization.CultureInfo.InvariantCulture, out byte value))
                        {
                            Console.WriteLine($"Invalid byte '{token}', use hex 00..FF.");
                            return;
                        }

                        list.Add(value);
                    }

                    bytes = list.ToArray();
                }
            }
            else
            {
                string content = dataArgs.Length > 0
                    ? string.Join(' ', dataArgs)
                    : string.Empty;

                bytes = System.Text.Encoding.ASCII.GetBytes(content);
            }

            BazFs.CreateFileWithPath(path, bytes, overwrite);
        }

        private static void CatFs(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: catfs <name>");
                return;
            }

            BazFs.ReadFileFromCurrentDir(args[0]);
        }

        private static void ThrowTestException()
        {
        }

        public static event Action? HaltRequested;

        private static void Help(CommandContext ctx)
        {
            Console.WriteLine("Available commands:");
            Console.WriteLine("  help             - Show this help");
            Console.WriteLine("  cls/clear        - Clear screen");
            Console.WriteLine("  halt             - Halt the system");
            Console.WriteLine("  dir              - List current directory");
            Console.WriteLine("  cd <path>        - Change directory");
            Console.WriteLine("  mkdir <path>     - Create directory");
            Console.WriteLine("  rmdir <path>     - Remove empty directory");
            Console.WriteLine("  del <file>       - Delete file");
            Console.WriteLine("  copy <src> <dst> - Copy file");
            Console.WriteLine("  type <file>      - Show file contents");
            Console.WriteLine("  options syntax:  /name or /name=value");
        }

        private static void Halt(CommandContext ctx)
        {
            Console.WriteLine("Halting system...");
            HaltRequested?.Invoke();
        }

        private static void Mkfs(CommandContext ctx)
        {
            Console.WriteLine("Formatting BAZFS on disk...");
            if (BazFs.Format())
                Console.WriteLine("mkfs: done.");
            else
                Console.WriteLine("mkfs: failed.");
        }

        private static void MountFs(CommandContext ctx)
        {
            Console.WriteLine("Mounting BAZFS...");
            if (BazFs.Mount())
            {
                var sb = BazFs.Superblock;
                Console.WriteLine("mount: OK");
                Console.WriteLine($"  Magic   = 0x{sb.Magic:X8}");
                Console.WriteLine($"  Version = {sb.Version}");
                Console.WriteLine($"  RootLBA = {sb.RootDirLba}");
                Console.WriteLine($"  Blocks  = {sb.TotalBlocks}");
            }
            else
            {
                Console.WriteLine("mount: failed.");
            }
        }

        private static void ListDirectory(CommandContext ctx)
        {
            BazFs.ListDirectory();
        }

        private static void ChangeDirectory(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: cd <name> or cd /");
                return;
            }

            BazFs.ChangeDirectory(args[0]);
        }

        private static void CreateDirectory(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: mkdir <name>");
                return;
            }

            BazFs.CreateDirectory(args[0]);
        }

        private static void RemoveDirectory(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: rmdir <name>");
                return;
            }

            BazFs.RemoveDirectory(args[0]);
        }

        private static void DeleteFile(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: del <name>");
                return;
            }

            BazFs.DeleteFile(args[0]);
        }

        private static void CopyFile(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: copy <src> <dst> [/overwrite]");
                return;
            }

            bool overwrite = ctx.HasOption("overwrite");
            BazFs.CopyFile(args[0], args[1], overwrite);
        }

        private static void TypeFile(CommandContext ctx)
        {
            var args = ctx.Args;
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: type <name>");
                return;
            }

            BazFs.ReadFileFromCurrentDir(args[0]);
        }

        private static string ResolvePath(string path)
        {
            return path;
        }

        private static string NormalizeDir(string dir)
        {
            return dir;
        }

        private static void CheckEntry(CommandContext ctx)
        {
            var args = ctx.Args;

            if (args.Length == 0)
            {
                Console.WriteLine("Usage: check <name> /f | /d");
                return;
            }

            string name = args[0];
            bool isFile = ctx.HasOption("f");
            bool isDir = ctx.HasOption("d");

            if (isFile == isDir)
            {
                Console.WriteLine("Usage: check <name> /f | /d");
                return;
            }

            bool result;
            if (isFile)
                result = BazFs.FileExistsInCurrentDir(name);
            else
                result = BazFs.DirectoryExistsInCurrentDir(name);

            Console.WriteLine(result ? "true" : "false");
        }

        private static void Power(CommandContext ctx)
        {
            bool rb = ctx.HasOption("rb");
            bool off = ctx.HasOption("off");
            int count = (rb ? 1 : 0) + (off ? 1 : 0);

            if (count != 1)
            {
                Console.WriteLine("Usage: power /rb | /off");
                return;
            }

            if (rb)
            {
                Console.WriteLine("Rebooting...");
                p.Reboot();
            }
            else
            {
                Console.WriteLine("Powering off...");
                p.Shutdown();
            }
        }
    }
}