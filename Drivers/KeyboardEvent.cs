using System;

namespace BAZOS.Drivers
{
    public readonly struct KeyboardEvent
    {
        public ConsoleKey Key { get; }
        public char KeyChar { get; }
        public bool Ctrl { get; }
        public bool Alt { get; }
        public bool Shift { get; }

        public KeyboardEvent(ConsoleKey key, char keyChar, bool ctrl, bool alt, bool shift)
        {
            Key = key;
            KeyChar = keyChar;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        public static KeyboardEvent FromConsole(ConsoleKeyInfo k)
        {
            bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
            bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
            return new KeyboardEvent(k.Key, k.KeyChar, ctrl, alt, shift);
        }
    }
}

