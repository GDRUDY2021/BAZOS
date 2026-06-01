using BAZOS.FS;
using BAZOS.Drivers;
using System;

namespace BAZOS.Api.Editor
{
    public static class TextEditor
    {
        private const int FooterRows = 2;

        public static void Run(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("change: path is required.");
                return;
            }

            string text;
            if (!BazFs.TryReadTextFile(path, out text))
                text = string.Empty;

            var buffer = new TextBuffer(text);
            int top = 0;
            string? lastError = null;
            bool firstRender = true;
            // Hardware cursor is unreliable in Cosmos text mode; render a software caret instead.
            Console.CursorVisible = false;

            while (true)
            {
                Render(path, buffer, top, lastError, firstRender);
                firstRender = false;
                lastError = null;

                var key = InputBus.ReadKey();
                bool ctrl = key.Ctrl;

                if (ctrl && key.Key == ConsoleKey.S)
                {
                    BazFs.WriteTextFile(path, buffer.GetText(), overwrite: true);
                    buffer.MarkClean();
                    continue;
                }

                if (ctrl && key.Key == ConsoleKey.Q)
                {
                    if (!buffer.IsDirty)
                        break;

                    var act = PromptConfirm();
                    if (act == ConfirmAction.Save)
                    {
                        BazFs.WriteTextFile(path, buffer.GetText(), overwrite: true);
                        buffer.MarkClean();
                        break;
                    }
                    if (act == ConfirmAction.Discard)
                        break;
                    continue;
                }

                if (ctrl && key.Key == ConsoleKey.G)
                {
                    var lineText = PromptInput("Go to line:");
                    if (!int.TryParse(lineText, out var ln) || !buffer.GoToLine(ln))
                        lastError = "Invalid line number.";
                    EnsureVisible(buffer, ref top);
                    continue;
                }

                if (ctrl && key.Key == ConsoleKey.F)
                {
                    var q = PromptInput("Find:");
                    if (!buffer.FindNext(q))
                        lastError = "Not found.";
                    EnsureVisible(buffer, ref top);
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow: buffer.MoveLeft(); break;
                    case ConsoleKey.RightArrow: buffer.MoveRight(); break;
                    case ConsoleKey.UpArrow: buffer.MoveUp(); break;
                    case ConsoleKey.DownArrow: buffer.MoveDown(); break;
                    case ConsoleKey.Home: buffer.MoveHome(); break;
                    case ConsoleKey.End: buffer.MoveEnd(); break;
                    case ConsoleKey.Backspace: buffer.Backspace(); break;
                    case ConsoleKey.Delete: buffer.Delete(); break;
                    case ConsoleKey.Enter: buffer.InsertNewLine(); break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                            buffer.InsertChar(key.KeyChar);
                        break;
                }

                EnsureVisible(buffer, ref top);
            }

            Console.Clear();
        }

        private static void EnsureVisible(TextBuffer b, ref int top)
        {
            int h = Math.Max(1, Console.WindowHeight - FooterRows);
            if (b.Row < top)
                top = b.Row;
            if (b.Row >= top + h)
                top = b.Row - h + 1;
            if (top < 0)
                top = 0;
        }

        private static void Render(string path, TextBuffer b, int top, string? error, bool firstRender)
        {
            if (firstRender)
                Console.Clear();

            int consoleHeight = Console.WindowHeight <= 0 ? 25 : Console.WindowHeight;
            int consoleWidth = Console.WindowWidth <= 0 ? 80 : Console.WindowWidth;
            int height = Math.Max(1, consoleHeight - FooterRows);
            int width = Math.Max(10, consoleWidth);
            // Avoid writing the very last console column: many text consoles auto-wrap/scroll there.
            int drawWidth = Math.Max(1, width - 1);

            for (int screenRow = 0; screenRow < height; screenRow++)
            {
                int lineIdx = top + screenRow;
                Console.SetCursorPosition(0, screenRow);
                if (lineIdx >= b.LineCount)
                {
                    WritePadded("~", drawWidth);
                    continue;
                }

                string line = b.Lines[lineIdx];
                if (lineIdx == b.Row)
                    line = ApplyCaret(line, b.Col, drawWidth);
                if (line.Length > drawWidth)
                    line = line.Substring(0, drawWidth);
                WritePadded(line, drawWidth);
            }

            string dirty = b.IsDirty ? "*" : "-";
            Console.SetCursorPosition(0, height);
            WritePadded($"[{dirty}] {path}  Ln {b.Row + 1}, Col {b.Col + 1}   Ctrl+S Save  Ctrl+Q Quit  Ctrl+G Goto  Ctrl+F Find", drawWidth);
            Console.SetCursorPosition(0, height + 1);
            WritePadded(error ?? string.Empty, drawWidth);

        }

        private static void WritePadded(string text, int width)
        {
            if (text == null)
                text = string.Empty;

            if (text.Length >= width)
            {
                Console.Write(text.Substring(0, width));
                return;
            }

            Console.Write(text);
            Console.Write(new string(' ', width - text.Length));
        }

        private static string ApplyCaret(string line, int col, int drawWidth)
        {
            if (drawWidth <= 0)
                return string.Empty;

            if (line == null)
                line = string.Empty;

            int clamped = col;
            if (clamped < 0)
                clamped = 0;
            if (clamped >= drawWidth)
                clamped = drawWidth - 1;

            if (line.Length < drawWidth)
                line = line + new string(' ', drawWidth - line.Length);
            else if (line.Length > drawWidth)
                line = line.Substring(0, drawWidth);

            var chars = line.ToCharArray();
            chars[clamped] = '|';
            return new string(chars);
        }

        private static string PromptInput(string title)
        {
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
            Console.Write(new string(' ', Math.Max(1, Console.WindowWidth)));
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
            Console.Write(title + " ");
            return InputBus.ReadLine();
        }

        private static ConfirmAction PromptConfirm()
        {
            while (true)
            {
                Console.SetCursorPosition(0, Console.WindowHeight - 1);
                Console.Write(new string(' ', Math.Max(1, Console.WindowWidth)));
                Console.SetCursorPosition(0, Console.WindowHeight - 1);
                Console.Write("Unsaved changes: [S]ave / [D]iscard / [C]ancel");

                var k = InputBus.ReadKey();
                switch (char.ToUpperInvariant(k.KeyChar))
                {
                    case 'S': return ConfirmAction.Save;
                    case 'D': return ConfirmAction.Discard;
                    case 'C': return ConfirmAction.Cancel;
                }
            }
        }

        private enum ConfirmAction
        {
            Save,
            Discard,
            Cancel
        }
    }
}

