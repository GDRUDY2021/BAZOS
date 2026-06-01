using BAZOS.Drivers;
using BAZOS.FS;
using Cosmos.Kernel.Core.X64.Power;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BAZOS.Api.Commands;
using BAZOS.Api.Editor;
using System.Text;

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
                if (command == null) return;
                RegisterName(command.Name, command);
                var aliases = command.Aliases ?? Array.Empty<string>();
                for (int i = 0; i < aliases.Length; i++) RegisterName(aliases[i], command);
            }

            private void RegisterName(string name, ICommand command)
            {
                if (!string.IsNullOrWhiteSpace(name)) _byName[name.Trim()] = command;
            }

            public bool TryGet(string name, out ICommand command) => _byName.TryGetValue(name, out command);

            public bool Disable(string nameOrAlias)
            {
                if (string.IsNullOrWhiteSpace(nameOrAlias) || !_byName.TryGetValue(nameOrAlias.Trim(), out var cmd)) return false;
                var keys = _byName.Where(kv => ReferenceEquals(kv.Value, cmd)).Select(kv => kv.Key).ToList();
                foreach (var k in keys) _byName.Remove(k);
                return true;
            }

            public IEnumerable<ICommand> AllUnique() => _byName.Values.Distinct();
        }

        private sealed class DelegateCommand : ICommand
        {
            private readonly Action<CommandContext> _action;
            public string Name { get; }
            public string[] Aliases { get; }
            public string? Help { get; }

            public DelegateCommand(string name, Action<CommandContext> action, string? help = null, params string[] aliases)
            {
                Name = name; _action = action; Help = help; Aliases = aliases ?? Array.Empty<string>();
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
                Command = command; Args = args; Options = options;
            }

            public bool HasOption(string name) => Options.ContainsKey(name);
            public string? GetOption(string name) => Options.TryGetValue(name, out var v) ? v : null;
        }

        private static readonly CommandRegistry Commands = new();
        private static readonly X64PowerOps p = new X64PowerOps();
        private static readonly DriverManager DriverManager = new DriverManager();
        private static readonly HashSet<string> RecoveryCommands = new(StringComparer.OrdinalIgnoreCase) { "device", "driver", "power", "disk" };

        public static void Init()
        {
            RegisterCommands();
        }

        public static void RunCommand(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;
            var tokens = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return;

            var cmd = tokens[0];
            if (!BazFs.IsMounted && !RecoveryCommands.Contains(cmd))
            {
                Console.WriteLine("Recovery mode: only device, driver, power, disk are available.");
                return;
            }

            var rawArgs = tokens.Skip(1).ToArray();
            var args = new List<string>();
            var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in rawArgs)
            {
                if (t.StartsWith("/"))
                {
                    var body = t.Substring(1);
                    int eq = body.IndexOf('=');
                    int colon = body.IndexOf(':');
                    int sep = eq >= 0 && colon >= 0 ? Math.Min(eq, colon) : (eq >= 0 ? eq : colon);

                    if (sep < 0) { if (!string.IsNullOrEmpty(body)) options[body] = null; }
                    else
                    {
                        var key = body.Substring(0, sep);
                        var val = body.Substring(sep + 1);
                        if (!string.IsNullOrEmpty(key)) options[key] = val;
                    }
                }
                else { args.Add(t); }
            }

            if (Commands.TryGet(cmd, out var command))
            {
                try { command.Execute(new CommandContext(cmd, args.ToArray(), options)); }
                catch (Exception ex)
                {
                    if (ex is KernelPanicException) throw;
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
            else
            {
                // СТРОГИЙ ПОИСК ИСПОЛНЯЕМОГО ФАЙЛА НА ДИСКЕ
                if (!TryRunExecutable(cmd))
                {
                    Console.WriteLine($"\"{cmd}\" is not an internal command or valid executable.");
                }
            }
        }

        private static bool TryRunExecutable(string cmd)
        {
            if (!BazFs.IsMounted) return false;

            if (!cmd.EndsWith(".bvx", StringComparison.OrdinalIgnoreCase))
                return false;

            string targetFile = cmd;

            if (!BazFs.TryReadFileBytes(targetFile, out byte[] fileData))
            {
                string sysPath = "/system/drivers/" + targetFile;
                if (!BazFs.TryReadFileBytes(sysPath, out fileData))
                    return false;

                targetFile = sysPath;
            }

            if (!Runtime.VmModule.TryLoad(fileData, out var module, out string error))
            {
                Console.WriteLine($"[bvx] Failed to load '{targetFile}': {error}");
                return true;
            }

            string procName = targetFile.Contains('/') ? targetFile.Substring(targetFile.LastIndexOf('/') + 1) : targetFile;

            // --- Очищаем экран, создавая эффект "отдельного окна" ---
            Console.Clear();
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($" BAZOS VM: Running {procName} ".PadRight(Console.WindowWidth));
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;

            int pid = Core.Scheduler.StartProcess(procName, module);
            var proc = Core.Scheduler.GetProcess(pid); // Получаем ссылку на процесс

            // Модально ждем завершения программы
            while (proc != null && !proc.IsFinished)
            {
                Core.Scheduler.Tick();
            }

            // Если программа упала - выводим красивую красную полосу
            if (proc != null && !string.IsNullOrEmpty(proc.ErrorMessage))
            {
                Console.WriteLine();
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($" [VM CRASH] {proc.ErrorMessage} ".PadRight(Console.WindowWidth));
            }
            else
            {
                //Console.WriteLine();
                //Console.BackgroundColor = ConsoleColor.DarkGreen;
                //Console.ForegroundColor = ConsoleColor.White;
                //Console.WriteLine($" [VM EXITED] Program finished normally. ".PadRight(Console.WindowWidth));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("\nPress any key to return to Shell...");

            // Ждем нажатия любой кнопки
            Drivers.InputBus.ReadKey();

            // --- "Восстанавливаем" Shell ---
            Console.Clear();
            Console.WriteLine("BAZOS Shell restored.");
            return true;
        }

        private static void RegisterCommands()
        {
            Commands.Clear();
            Commands.Register(new DelegateCommand("help", Help, "Show help"));
            Commands.Register(new DelegateCommand("cls", _ => Console.Clear(), "Clear screen", aliases: new[] { "clear" }));
            Commands.Register(new DelegateCommand("halt", Halt, "Halt the system"));
            Commands.Register(new DelegateCommand("dir", ListDirectory, "List current directory"));
            Commands.Register(new DelegateCommand("cd", ChangeDirectory, "Change directory"));
            Commands.Register(new DelegateCommand("copy", CopyFile, "Copy file"));
            Commands.Register(new DelegateCommand("type", TypeFile, "Print file contents"));
            Commands.Register(new DelegateCommand("panic", _ => Panic(), "Kernel panic (crash)"));
            Commands.Register(new DelegateCommand("dumpfs", DumpFs, "Dump raw FS sector"));
            Commands.Register(new DelegateCommand("fsck", _ => BazFs.FsckLite(), "Check BAZFS (fsck-lite)"));
            Commands.Register(new DelegateCommand("dirfs", DirFs, "List BAZFS root"));
            Commands.Register(new DelegateCommand("catfs", CatFs, "Read file (legacy)"));
            Commands.Register(new DelegateCommand("check", CheckEntry, "Check file/dir exists"));
            Commands.Register(new DelegateCommand("change", ChangeFile, "Open text editor"));
            Commands.Register(new DelegateCommand("power", Power, "Power control"));
            Commands.Register(new DelegateCommand("say", Say, "Print text to console. Use /n for no newline."));
            Commands.Register(new DelegateCommand("pack", InstallFs, "-"));

            Commands.Register(new DelegateCommand("ide", ctx => Ide.Run(ctx.Args.Length > 0 ? ctx.Args[0] : ""), "Open Console IDE"));

            Commands.Register(new DeviceCommand());
            Commands.Register(new DriverCommand(DriverManager));
            Commands.Register(new FsCommand());
            Commands.Register(new GroupCommand());
        }

        private static void Say(CommandContext ctx)
        {
            string message = string.Join(" ", ctx.Args);

            if (ctx.HasOption("n"))
            {
                Console.Write(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        public static bool DiskFormat(int slot)
        {
            if (!BazFs.SetActiveDiskSlot(slot)) { Console.WriteLine("disk: invalid slot."); return false; }
            Console.WriteLine($"Formatting BAZFS on disk slot {slot}...");
            bool ok = BazFs.Format();
            Console.WriteLine(ok ? "mkfs: done." : "mkfs: failed.");
            InputBus.SetRescueConsoleFallback(!BazFs.IsMounted);
            return ok;
        }

        public static bool DiskMount(int slot)
        {
            if (!BazFs.SetActiveDiskSlot(slot)) { Console.WriteLine("disk: invalid slot."); return false; }
            Console.WriteLine($"Mounting BAZFS on disk slot {slot}...");
            if (!BazFs.Mount())
            {
                InputBus.SetRescueConsoleFallback(true);
                Console.WriteLine("mount: failed.");
                return false;
            }

            InputBus.SetRescueConsoleFallback(true);
            var sb = BazFs.Superblock;
            Console.WriteLine("mount: OK");
            Console.WriteLine($"  Magic   = 0x{sb.Magic:X8}");
            Console.WriteLine($"  RootLBA = {sb.RootDirLba}");

            DeviceManager.LoadConfig();
            DriverManager.Reload();
            DriverManager.ApplyEnabled();

            for (int i = 0; i < 50; i++)
            {
                Core.Scheduler.Tick();
            }

            if (InputBus.IsKeyboardEnabled)
            {
                Console.WriteLine("[recovery] User-space keyboard driver active.");
                InputBus.SetRescueConsoleFallback(false);
            }
            else
            {
                Console.WriteLine("[recovery] kbd driver not active, using cosmos rescue keyboard");
                InputBus.SetRescueConsoleFallback(true);
            }

            return true;
        }

        private static void DumpFs(CommandContext ctx)
        {
            int slot = BazFs.ActiveDiskSlot;
            var drive = AtaManager.GetDrive(slot);
            if (drive == null) { Console.WriteLine($"dumpfs: Disk slot {slot} not available."); return; }
            Console.WriteLine($"Reading sector 0 via ATA (Slot {slot})...");
            Span<byte> buffer = stackalloc byte[512];
            if (drive.ReadSector(0, buffer))
            {
                Console.WriteLine("Sector 0 read OK. First bytes:");
                for (int i = 0; i < 512; i++) Console.Write($"{buffer[i]:X2} ");
                Console.WriteLine();
            }
            else { Console.WriteLine("Failed to read sector 0."); }
        }

        private static void DirFs(CommandContext ctx) => BazFs.ListRoot();
        private static void CatFs(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.ReadFileFromCurrentDir(ctx.Args[0]); }
        private static void Panic() => throw new KernelPanicException("Kernel panic requested");
        public static event Action? HaltRequested;

        private static void Help(CommandContext ctx)
        {
            Console.WriteLine("Available commands:");
            foreach (var c in Commands.AllUnique().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {c.Name}");
        }

        private static void Halt(CommandContext ctx) { Console.WriteLine("Halting system..."); HaltRequested?.Invoke(); }
        private static void ListDirectory(CommandContext ctx) => BazFs.ListDirectory();
        private static void ChangeDirectory(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.ChangeDirectory(ctx.Args[0]); }
        private static void CreateDirectory(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.CreateDirectory(ctx.Args[0]); }
        private static void RemoveDirectory(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.RemoveDirectory(ctx.Args[0], ctx.HasOption("f")); }
        private static void DeleteFile(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.DeleteFile(ctx.Args[0]); }
        private static void CopyFile(CommandContext ctx) { if (ctx.Args.Length > 1) BazFs.CopyFile(ctx.Args[0], ctx.Args[1], ctx.HasOption("overwrite")); }
        private static void TypeFile(CommandContext ctx) { if (ctx.Args.Length > 0) BazFs.ReadFileFromCurrentDir(ctx.Args[0]); }
        private static void ChangeFile(CommandContext ctx) { if (ctx.Args.Length > 0) TextEditor.Run(ctx.Args[0]); }

        private static void CheckEntry(CommandContext ctx)
        {
            if (ctx.Args.Length == 0) return;
            string name = ctx.Args[0];
            bool isFile = ctx.HasOption("f"), isDir = ctx.HasOption("d");
            if (isFile == isDir) return;
            Console.WriteLine((isFile ? BazFs.FileExistsInCurrentDir(name) : BazFs.DirectoryExistsInCurrentDir(name)) ? "true" : "false");
        }

        private static void Power(CommandContext ctx)
        {
            if (ctx.HasOption("rb")) { Console.WriteLine("Rebooting..."); p.Reboot(); }
            else if (ctx.HasOption("off")) { Console.WriteLine("Powering off..."); p.Shutdown(); }
        }

        private static void InstallFs(CommandContext ctx)
        {
            if (!BazFs.IsMounted)
            {
                Console.WriteLine("Error: Mount the BAZFS disk first! (device /disk=0 /m)");
                return;
            }

            Console.WriteLine("Reading built-in Initrd payload from kernel memory...");

            byte[] archive = Core.InitrdPayload.Data;

            if (archive == null || archive.Length < 8)
            {
                Console.WriteLine("Error: InitrdPayload.Data is missing or empty.");
                return;
            }

            // Проверяем подпись "PACK"
            string magic = Encoding.ASCII.GetString(archive, 0, 4);
            if (magic != "PACK")
            {
                Console.WriteLine($"Error: Invalid archive magic: {magic}");
                return;
            }

            int fileCount = BitConverter.ToInt32(archive, 4);
            Console.WriteLine($"Found {fileCount} files in initrd. Extracting to BAZFS...");

            int pos = 8;
            for (int i = 0; i < fileCount; i++)
            {
                if (pos >= archive.Length) break;

                // Читаем длину пути и путь
                int pathLen = BitConverter.ToInt32(archive, pos);
                pos += 4;
                string path = Encoding.UTF8.GetString(archive, pos, pathLen);
                pos += pathLen;

                // Читаем размер файла
                int dataLen = BitConverter.ToInt32(archive, pos);
                pos += 4;

                // Извлекаем директорию и создаем её в BazFs
                int lastSlash = path.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    string dir = path.Substring(0, lastSlash);
                    BazFs.CreateDirectory(dir);
                }

                // Вырезаем байты файла
                byte[] fileData = new byte[dataLen];
                Array.Copy(archive, pos, fileData, 0, dataLen);

                // Пишем на жесткий диск BAZFS
                BazFs.CreateFileWithPath(path, fileData, overwrite: true);
                Console.WriteLine($" Extracted: {path}");

                pos += dataLen;
            }

            Console.WriteLine("Installation complete! The BAZOS file system is ready.");
        }
    }
}