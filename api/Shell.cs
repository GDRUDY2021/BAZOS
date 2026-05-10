using BAZOS.Drivers;
using BAZOS.FS;
using Cosmos.Kernel.Core.X64.Power;
using Cosmos.Kernel.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static System.Net.WebRequestMethods;
using BAZOS.Api.Commands;
using System.Text;
using BAZOS.Drivers;

namespace BAZOS.Api
{
    public static class Shell
    {
        private sealed class KernelPanicException : Exception
        {
            public KernelPanicException(string message) : base(message) { }
        }

        public interface ICommand
        {
            string Name { get; }
            string[] Aliases { get; }
            string? Help { get; }
            void Execute(CommandContext ctx);
        }

        public sealed class CommandRegistry
        {
            private readonly Dictionary<string, ICommand> _byName = new(StringComparer.OrdinalIgnoreCase);

            public void Clear() => _byName.Clear();

            public void Register(ICommand command)
            {
                if (command == null)
                    return;

                RegisterName(command.Name, command);

                var aliases = command.Aliases ?? Array.Empty<string>();
                for (int i = 0; i < aliases.Length; i++)
                    RegisterName(aliases[i], command);
            }

            private void RegisterName(string name, ICommand command)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _byName[name.Trim()] = command;
            }

            public bool TryGet(string name, out ICommand command)
                => _byName.TryGetValue(name, out command);

            public bool Disable(string nameOrAlias)
            {
                if (string.IsNullOrWhiteSpace(nameOrAlias))
                    return false;

                if (!_byName.TryGetValue(nameOrAlias.Trim(), out var cmd))
                    return false;

                var keys = new List<string>();
                foreach (var kv in _byName)
                {
                    if (ReferenceEquals(kv.Value, cmd))
                        keys.Add(kv.Key);
                }

                for (int i = 0; i < keys.Count; i++)
                    _byName.Remove(keys[i]);

                return true;
            }

            public IEnumerable<ICommand> AllUnique()
            {
                var seen = new HashSet<ICommand>();
                foreach (var kv in _byName)
                {
                    if (seen.Add(kv.Value))
                        yield return kv.Value;
                }
            }
        }

        private sealed class DelegateCommand : ICommand
        {
            private readonly Action<CommandContext> _action;

            public string Name { get; }
            public string[] Aliases { get; }
            public string? Help { get; }

            public DelegateCommand(string name, Action<CommandContext> action, string? help = null, params string[] aliases)
            {
                Name = name;
                _action = action;
                Help = help;
                Aliases = aliases ?? Array.Empty<string>();
            }

            public void Execute(CommandContext ctx) => _action(ctx);
        }

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

        private static readonly CommandRegistry Commands = new();
        private static readonly X64PowerOps p = new X64PowerOps();
        private static readonly DriverManager DriverManager = new DriverManager();

        public static void Init()
        {
            DeviceManager.RegisterDevice(new DeviceDescriptor
            {
                Id = "mouse0",
                Type = DeviceType.Mouse,
                Name = "Virtual Mouse",
                Enabled = true
            });
            DeviceManager.RegisterDevice(new DeviceDescriptor
            {
                Id = "kbd0",
                Type = DeviceType.Keyboard,
                Name = "Keyboard",
                Enabled = true
            });
            DeviceManager.RegisterDevice(new DeviceDescriptor
            {
                Id = "audio0",
                Type = DeviceType.Audio,
                Name = "Audio",
                Enabled = true
            });

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

            if (Commands.TryGet(cmd, out var command))
            {
                try
                {
                    var ctx = new CommandContext(cmd, args.ToArray(), options);
                    command.Execute(ctx);
                }
                catch (Exception ex)
                {
                    if (ex is KernelPanicException)
                        throw;
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

            Commands.Register(new DelegateCommand("help", Help, "Show help"));
            Commands.Register(new DelegateCommand("cls", _ => Console.Clear(), "Clear screen", aliases: new[] { "clear" }));
            Commands.Register(new DelegateCommand("halt", Halt, "Halt the system"));

            Commands.Register(new DelegateCommand("dir", ListDirectory, "List current directory"));
            Commands.Register(new DelegateCommand("cd", ChangeDirectory, "Change directory"));
            Commands.Register(new DelegateCommand("mkdir", CreateDirectory, "Create directory"));
            Commands.Register(new DelegateCommand("rmdir", RemoveDirectory, "Remove directory"));
            Commands.Register(new DelegateCommand("del", DeleteFile, "Delete file"));
            Commands.Register(new DelegateCommand("copy", CopyFile, "Copy file"));
            Commands.Register(new DelegateCommand("type", TypeFile, "Print file contents"));

            Commands.Register(new DelegateCommand("panic", _ => Panic(), "Kernel panic (crash)"));

            Commands.Register(new DelegateCommand("mkfs", Mkfs, "Format BAZFS"));
            Commands.Register(new DelegateCommand("mount", MountFs, "Mount BAZFS"));
            Commands.Register(new DelegateCommand("dumpfs", DumpFs, "Dump raw FS sector"));
            Commands.Register(new DelegateCommand("fsck", _ => BazFs.FsckLite(), "Check BAZFS (fsck-lite)"));
            Commands.Register(new DelegateCommand("dirfs", DirFs, "List BAZFS root"));
            Commands.Register(new DelegateCommand("mkfile", MkFile, "Create file with data"));
            Commands.Register(new DelegateCommand("catfs", CatFs, "Read file (legacy)"));
            Commands.Register(new DelegateCommand("check", CheckEntry, "Check file/dir exists"));

            Commands.Register(new DelegateCommand("power", Power, "Power control"));

            Commands.Register(new DeviceCommand());
            Commands.Register(new DriverCommand(DriverManager));
        }

        private static void DumpFs(CommandContext ctx)
        {
            Console.WriteLine("Reading sector 0 via ATA...");

            Span<byte> buffer = stackalloc byte[512];
            if (AtaDisk.ReadSector(0, buffer))
            {
                Console.WriteLine("Sector 0 read OK. First bytes:");
                for (int i = 0; i < 512; i++)
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
            throw new KernelPanicException("Test exception (panic-ex)");
        }

        private static void Panic()
        {
            throw new KernelPanicException("Kernel panic requested");
        }

        public static event Action? HaltRequested;

        private static void Help(CommandContext ctx)
        {
            Console.WriteLine("Available commands:");
            foreach (var c in Commands.AllUnique().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                string aliases = (c.Aliases != null && c.Aliases.Length > 0)
                    ? $" (aliases: {string.Join(", ", c.Aliases)})"
                    : string.Empty;
                string help = string.IsNullOrWhiteSpace(c.Help) ? string.Empty : $" - {c.Help}";
                Console.WriteLine($"  {c.Name}{aliases}{help}");
            }
            Console.WriteLine("options syntax: /name or /name=value");
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

                DeviceManager.LoadConfig();
                ModuleLoader.Apply(Commands);

                DriverManager.Reload();
                DriverManager.ApplyEnabled();
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
                Console.WriteLine("Usage: rmdir <name> [/f]");
                return;
            }

            bool force = ctx.HasOption("f");
            BazFs.RemoveDirectory(args[0], force);
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