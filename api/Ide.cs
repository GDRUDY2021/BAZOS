// --- START OF FILE Ide.cs ---
using BAZOS.FS;
using BAZOS.Drivers;
using BAZOS.Runtime;
using BAZOS.Api.Editor;
using System.Text;

namespace BAZOS.Api
{
    public static class Ide
    {
        private static bool _editorFocused = true;
        private static string _currentDir = "/system/drivers";
        private static List<string> _files = new();
        private static int _fileSelectedIndex = 0;
        private static string _statusMessage = "Ready. ESC: Switch | F5: Run | F7: Compile | F10: Exit";
        private static bool _needRedraw = true;
        private static int _leftOffset = 0;

        public static void Run(string startPath)
        {
            Console.Clear();
            Console.CursorVisible = false;

            if (string.IsNullOrWhiteSpace(startPath))
                startPath = "/system/drivers/main.bs";

            if (!BazFs.TryReadFileBytes(startPath, out var _))
                BazFs.WriteTextFile(startPath, "// Write your BAZScript here\n");

            BazFs.TryReadTextFile(startPath, out string text);
            var buffer = new TextBuffer(text);
            int top = 0;

            RefreshFileList();
            _needRedraw = true;

            while (true)
            {
                if (_needRedraw)
                {
                    DrawLayout(startPath, buffer, top);
                    _needRedraw = false;
                }

                var key = InputBus.ReadKey();

                if (key.Key == ConsoleKey.F10)
                {
                    if (buffer.IsDirty) BazFs.WriteTextFile(startPath, buffer.GetText());
                    break;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    _editorFocused = !_editorFocused;
                    if (_editorFocused) _statusMessage = "Editor mode. F5/F7: Compile | F10: Exit";
                    else _statusMessage = "Explorer. N: File | D: Folder | Del: Delete | Enter: Open";
                    _needRedraw = true;
                    continue;
                }

                if (key.Key == ConsoleKey.N && !_editorFocused)
                {
                    CreateNewFilePrompt();
                    RefreshFileList();
                    _needRedraw = true;
                    continue;
                }

                if (key.Key == ConsoleKey.D && !_editorFocused)
                {
                    CreateNewFolderPrompt();
                    RefreshFileList();
                    _needRedraw = true;
                    continue;
                }

                if (key.Key == ConsoleKey.Delete && !_editorFocused && _files.Count > 0)
                {
                    string selected = _files[_fileSelectedIndex];
                    if (selected != "-")
                    {
                        if (selected.StartsWith("[") && selected.EndsWith("]"))
                        {
                            string dirName = selected.Substring(1, selected.Length - 2);
                            BazFs.RemoveDirectory(_currentDir + "/" + dirName, force: true);
                            _statusMessage = $"Deleted folder {dirName}";
                        }
                        else
                        {
                            BazFs.DeleteFile(_currentDir + "/" + selected);
                            _statusMessage = $"Deleted file {selected}";
                        }
                        RefreshFileList();
                        _needRedraw = true;
                    }
                    continue;
                }

                if (key.Key == ConsoleKey.F7 || key.Key == ConsoleKey.F5)
                {
                    int mode = AskCompileMode();
                    if (mode == 0)
                    {
                        _needRedraw = true;
                        continue;
                    }

                    if (buffer.IsDirty) { BazFs.WriteTextFile(startPath, buffer.GetText()); buffer.MarkClean(); }

                    CompileToFile(startPath, buffer.GetText(), run: key.Key == ConsoleKey.F5, mode);
                    RefreshFileList();
                    _needRedraw = true;
                    continue;
                }

                if (_editorFocused) HandleEditorInput(key, buffer, ref top);
                else HandleTreeInput(key, ref buffer, ref startPath, ref top);
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Clear();
        }

        private static int AskCompileMode()
        {
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.SetCursorPosition(10, 10);
            Console.Write(" Select Compilation Target: ".PadRight(30));
            Console.SetCursorPosition(10, 11);
            Console.Write(" 1. App Executable (.bvx)   ".PadRight(30));
            Console.SetCursorPosition(10, 12);
            Console.Write(" 2. OS Driver Pack (.drv)   ".PadRight(30));
            Console.SetCursorPosition(10, 13);
            Console.Write(" ESC. Cancel                ".PadRight(30));

            while (true)
            {
                var k = InputBus.ReadKey();
                if (k.Key == ConsoleKey.D1 || k.Key == ConsoleKey.NumPad1) return 1;
                if (k.Key == ConsoleKey.D2 || k.Key == ConsoleKey.NumPad2) return 2;
                if (k.Key == ConsoleKey.Escape) return 0;
            }
        }

        private static void CreateNewFilePrompt()
        {
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("New file name (empty for default): ".PadRight(Console.WindowWidth - 1));
            Console.SetCursorPosition(35, Console.WindowHeight - 1);
            Console.CursorVisible = true;
            string inputName = InputBus.ReadLine();
            Console.CursorVisible = false;

            if (string.IsNullOrWhiteSpace(inputName))
            {
                string baseName = "file";
                string ext = ".bs";
                inputName = baseName + ext;
                int counter = 1;
                while (BazFs.TryReadFileBytes(_currentDir + "/" + inputName, out _))
                {
                    inputName = $"{baseName}({counter}){ext}";
                    counter++;
                }
            }
            else if (!inputName.Contains("."))
            {
                inputName += ".bs";
            }

            string fullPath = _currentDir + "/" + inputName;
            if (!BazFs.TryReadFileBytes(fullPath, out _))
            {
                BazFs.WriteTextFile(fullPath, "// New BAZScript\n");
            }
            _statusMessage = $"Created {inputName}";
        }

        private static void CreateNewFolderPrompt()
        {
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("New folder name (empty for default): ".PadRight(Console.WindowWidth - 1));
            Console.SetCursorPosition(37, Console.WindowHeight - 1);
            Console.CursorVisible = true;
            string inputName = InputBus.ReadLine();
            Console.CursorVisible = false;

            if (string.IsNullOrWhiteSpace(inputName))
            {
                string baseName = "folder";
                inputName = baseName;
                int counter = 1;
                while (BazFs.DirectoryExistsInCurrentDir(inputName))
                {
                    inputName = $"{baseName}({counter})";
                    counter++;
                }
            }

            string fullPath = _currentDir + "/" + inputName;
            BazFs.CreateDirectory(fullPath);
            _statusMessage = $"Created folder {inputName}";
        }

        private static void CompileToFile(string sourcePath, string sourceCode, bool run, int mode)
        {
            _statusMessage = "Compiling...";
            DrawLayout("", null, 0);

            if (!Compiler.TryCompile(sourceCode, out byte[] bytecode, out string error))
            {
                _statusMessage = $"Compile Error: {error}";
                return;
            }

            int lastSlash = sourcePath.LastIndexOf('/');
            string currentFolder = lastSlash >= 0 ? sourcePath.Substring(0, lastSlash) : "/system/drivers";
            string folderName = currentFolder.Substring(currentFolder.LastIndexOf('/') + 1);
            string nameOnly = sourcePath.Substring(lastSlash + 1).Replace(".bs", "");

            // Режим DRV (Архивируем манифест и байткод в .drv)
            if (mode == 2)
            {
                string manifestPath = currentFolder + "/manifest.txt";
                // Если манифеста нет - выдаем ошибку!
                if (!BazFs.TryReadFileBytes(manifestPath, out byte[] manifestBytes))
                {
                    _statusMessage = "Error: manifest.txt not found! Create it first.";
                    return;
                }

                byte[] drvData = DriverPackageFormat.Pack(manifestBytes, bytecode);

                string parentDir = currentFolder.LastIndexOf('/') > 0 ? currentFolder.Substring(0, currentFolder.LastIndexOf('/')) : "/";
                string drvPath = parentDir + "/" + folderName + ".drv";

                BazFs.CreateFileWithPath(drvPath, drvData, overwrite: true);
                _statusMessage = $"Compiled driver to {drvPath}";

                if (run)
                {
                    _statusMessage = "Use 'driver enable' to start drivers!";
                }
                return;
            }

            // Стандартный режим BVX
            string outPath = currentFolder + "/" + nameOnly + ".bvx";
            BazFs.CreateFileWithPath(outPath, bytecode, overwrite: true);

            if (!run)
            {
                _statusMessage = $"Compiled successfully to {nameOnly}.bvx";
                return;
            }

            if (!VmModule.TryLoad(bytecode, out var module, out string loadErr))
            {
                _statusMessage = $"VM Load Error: {loadErr}";
                return;
            }

            string procName = nameOnly + ".bvx";

            Console.Clear();
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($" BAZOS IDE: Running {procName} ".PadRight(Console.WindowWidth));
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;

            int pid = Core.Scheduler.StartProcess(procName, module);
            var proc = Core.Scheduler.GetProcess(pid);

            while (proc != null && !proc.IsFinished)
            {
                Core.Scheduler.Tick();
            }

            if (proc != null && !string.IsNullOrEmpty(proc.ErrorMessage))
            {
                Console.WriteLine();
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($" [VM CRASH] {proc.ErrorMessage} ".PadRight(Console.WindowWidth));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("\nPress any key to return to IDE...");
            InputBus.ReadKey();

            Console.Clear();
            _statusMessage = $"Finished running {procName}";
        }

        private static void HandleTreeInput(KeyboardEvent key, ref TextBuffer buffer, ref string startPath, ref int top)
        {
            if (key.Key == ConsoleKey.UpArrow && _fileSelectedIndex > 0) { _fileSelectedIndex--; _needRedraw = true; }
            if (key.Key == ConsoleKey.DownArrow && _fileSelectedIndex < _files.Count - 1) { _fileSelectedIndex++; _needRedraw = true; }

            if (key.Key == ConsoleKey.Enter && _files.Count > 0)
            {
                string selected = _files[_fileSelectedIndex];

                if (selected == "-")
                {
                    int lastSlash = _currentDir.LastIndexOf('/');
                    if (lastSlash > 0) _currentDir = _currentDir.Substring(0, lastSlash);
                    else _currentDir = "/";
                    RefreshFileList();
                    _needRedraw = true;
                    return;
                }

                if (selected.StartsWith("[") && selected.EndsWith("]"))
                {
                    string dirName = selected.Substring(1, selected.Length - 2);
                    if (_currentDir == "/") _currentDir = "/" + dirName;
                    else _currentDir = _currentDir + "/" + dirName;
                    RefreshFileList();
                    _needRedraw = true;
                    return;
                }

                if (buffer.IsDirty) BazFs.WriteTextFile(startPath, buffer.GetText());

                startPath = _currentDir + "/" + selected;
                BazFs.TryReadTextFile(startPath, out string text);
                buffer = new TextBuffer(text);
                top = 0;
                _editorFocused = true;
                _statusMessage = $"Opened {startPath}";
                _needRedraw = true;
            }
        }

        private static void HandleEditorInput(KeyboardEvent key, TextBuffer buffer, ref int top)
        {
            bool changed = false;

            switch (key.Key)
            {
                case ConsoleKey.LeftArrow: buffer.MoveLeft(); changed = true; break;
                case ConsoleKey.RightArrow: buffer.MoveRight(); changed = true; break;
                case ConsoleKey.UpArrow: buffer.MoveUp(); changed = true; break;
                case ConsoleKey.DownArrow: buffer.MoveDown(); changed = true; break;
                case ConsoleKey.Backspace: buffer.Backspace(); changed = true; break;
                case ConsoleKey.Enter: buffer.InsertNewLine(); changed = true; break;
                case ConsoleKey.Tab:
                    buffer.InsertChar(' '); buffer.InsertChar(' '); buffer.InsertChar(' '); buffer.InsertChar(' ');
                    changed = true; break;
                default:
                    if (!char.IsControl(key.KeyChar)) { buffer.InsertChar(key.KeyChar); changed = true; }
                    break;
            }

            if (changed)
            {
                int h = Math.Max(1, Console.WindowHeight - 2);
                if (buffer.Row < top) top = buffer.Row;
                if (buffer.Row >= top + h) top = buffer.Row - h + 1;
                _needRedraw = true;
            }
        }

        private static void RefreshFileList()
        {
            _files.Clear();
            if (_currentDir != "/" && _currentDir != "") _files.Add("-");

            if (BazFs.TryListDirectory(_currentDir, out var entries))
            {
                foreach (var e in entries) if (e.Flags == 1) _files.Add($"[{e.Name}]");
                foreach (var e in entries) if (e.Flags == 0) _files.Add(e.Name);
            }
            if (_fileSelectedIndex >= _files.Count) _fileSelectedIndex = 0;
        }

        private static void DrawLayout(string path, TextBuffer? b, int top)
        {
            int width = Console.WindowWidth <= 0 ? 80 : Console.WindowWidth;
            int height = Console.WindowHeight <= 0 ? 25 : Console.WindowHeight;

            int treeWidth = 20;
            int lineNumWidth = 5;
            int editorWidth = width - treeWidth - lineNumWidth;

            if (_editorFocused && b != null)
            {
                if (b.Col < _leftOffset) _leftOffset = b.Col;
                if (b.Col >= _leftOffset + editorWidth - 2) _leftOffset = b.Col - editorWidth + 2;
            }

            for (int y = 0; y < height - 1; y++)
            {
                Console.SetCursorPosition(0, y);

                Console.BackgroundColor = _editorFocused ? ConsoleColor.DarkBlue : ConsoleColor.DarkCyan;
                Console.ForegroundColor = ConsoleColor.White;
                string leftLine = y == 0 ? " Explorer" : (y - 1 < _files.Count ? (y - 1 == _fileSelectedIndex && !_editorFocused ? ">" : " ") + _files[y - 1] : "");
                if (leftLine.Length > treeWidth) leftLine = leftLine.Substring(0, treeWidth);
                Console.Write(leftLine.PadRight(treeWidth));

                Console.BackgroundColor = ConsoleColor.DarkGray;
                Console.ForegroundColor = ConsoleColor.Cyan;
                int lineIdx = top + y;
                string lnStr = (b != null && lineIdx < b.LineCount) ? (lineIdx + 1).ToString().PadLeft(lineNumWidth - 1) + "|" : new string(' ', lineNumWidth - 1) + "|";
                Console.Write(lnStr);

                Console.BackgroundColor = _editorFocused ? ConsoleColor.Black : ConsoleColor.DarkGray;
                Console.ForegroundColor = ConsoleColor.Gray;

                string rightLine = "";
                if (b != null)
                {
                    string rawLine = lineIdx < b.LineCount ? b.Lines[lineIdx] : "~";
                    if (rawLine.Length > _leftOffset) rightLine = rawLine.Substring(_leftOffset);
                    rightLine = " " + rightLine;

                    if (lineIdx == b.Row && _editorFocused)
                    {
                        int screenCol = b.Col - _leftOffset + 1;
                        if (screenCol >= 1 && screenCol <= editorWidth)
                        {
                            if (screenCol >= rightLine.Length) rightLine = rightLine.PadRight(screenCol) + "_";
                            else rightLine = rightLine.Remove(screenCol, 1).Insert(screenCol, "_");
                        }
                    }
                }

                if (rightLine.Length > editorWidth) rightLine = rightLine.Substring(0, editorWidth);
                Console.Write(rightLine.PadRight(editorWidth));
            }

            Console.SetCursorPosition(0, height - 1);
            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.Black;
            string status = _statusMessage;
            if (status.Length > width - 1) status = status.Substring(0, width - 1);
            Console.Write(status.PadRight(width - 1));
        }
    }
}