using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace HsbgCardLookup.Hotkey
{
    /// <summary>
    /// How a binding is written down: <c>"F3"</c>, <c>"Ctrl+H"</c>, <c>"Ctrl+Shift+H"</c>, or
    /// <c>"None"</c> for unbound.
    ///
    /// One place for the format because three of them read it — the config, the hook and the
    /// settings row that displays it — and a binding that parses differently in any one of them is a
    /// key that appears bound and does nothing.
    ///
    /// Alt is deliberately not in the vocabulary. Windows gives Alt+key to the window's menu, and a
    /// global hook that fires on it while the game has a menu open is a fight we would lose.
    /// </summary>
    internal static class HotkeyText
    {
        public const string Unbound = "None";

        public static Key KeyOf(string text)
        {
            Key k;
            var last = LastToken(text);
            return Enum.TryParse(last, true, out k) ? k : Key.None;
        }

        public static ModifierKeys ModsOf(string text)
        {
            var mods = ModifierKeys.None;
            if (string.IsNullOrWhiteSpace(text)) return mods;
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var p = parts[i].Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Control;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Shift;
            }
            return mods;
        }

        /// <summary>Write a binding out. Order is fixed so two equal bindings always compare equal
        /// as strings, which is what the steal check in the settings window relies on.</summary>
        public static string Format(Key key, ModifierKeys mods)
        {
            if (key == Key.None) return Unbound;
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        private static string LastToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Unbound;
            var parts = text.Split('+');
            return parts[parts.Length - 1].Trim();
        }
    }
}
