using System;
using System.Collections.Generic;

namespace BAZOS.Drivers
{
    public static class InputBus
    {
        private static readonly Queue<KeyboardEvent> _queue = new();
        private static bool _kbdDriverEnabled;
        private static bool _rescueConsoleFallbackEnabled = true;

        public static bool IsKeyboardEnabled => _kbdDriverEnabled;
        public static bool IsRescueConsoleFallbackEnabled => _rescueConsoleFallbackEnabled;

        public static void SetKeyboardEnabled(bool enabled) => _kbdDriverEnabled = enabled;
        public static void SetRescueConsoleFallback(bool enabled) => _rescueConsoleFallbackEnabled = enabled;

        public static void PushKey(KeyboardEvent evt)
        {
            _queue.Enqueue(evt);
        }

        public static bool TryReadKey(out KeyboardEvent evt)
        {
            if (_queue.Count > 0)
            {
                evt = _queue.Dequeue();
                return true;
            }

            if (_rescueConsoleFallbackEnabled)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        evt = KeyboardEvent.FromConsole(Console.ReadKey(true));
                        return true;
                    }
                }
                catch
                {
                }
            }

            evt = default;
            return false;
        }

        public static KeyboardEvent ReadKey()
        {
            while (true)
            {
                Core.Scheduler.Tick();

                if (TryReadKey(out var evt))
                    return evt;
            }
        }

        public static string ReadLine()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var k = ReadKey();

                if (k.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return sb.ToString();
                }

                if (k.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
                {
                    sb.Append(k.KeyChar);
                    Console.Write(k.KeyChar);
                }
            }
        }
    }
}