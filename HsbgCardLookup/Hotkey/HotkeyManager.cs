using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace HsbgCardLookup.Hotkey
{
    /// <summary>
    /// System-wide single-key hotkey via a low-level keyboard hook (WH_KEYBOARD_LL) — supports any
    /// key and lets us decide per-press. Normal mode never swallows (the key still reaches the game);
    /// capture mode (settings rebind) swallows every key. Installed on HDT's message-pumping UI thread.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_ESCAPE = 0x1B;

        private readonly LowLevelKeyboardProc _proc;   // kept alive for the hook's lifetime
        private IntPtr _hookId = IntPtr.Zero;

        /// <summary>
        /// Registered bindings. A list rather than a dictionary because the KEY alone no longer
        /// identifies a binding: H and Ctrl+H are two different things and both may be registered.
        /// </summary>
        private readonly List<Binding> _targets = new List<Binding>();

        private struct Binding
        {
            public int Vk;
            public Key Key;
            public ModifierKeys Mods;
        }

        /// <summary>Raised when a registered binding is pressed. Args: key, modifiers, and the
        /// foreground process name.</summary>
        public event Action<Key, ModifierKeys, string> HotkeyPressed;

        /// <summary>Raised for every key while in capture mode — the settings dialog reads it to
        /// rebind, together with whatever modifiers were being held. Fires on the hook thread.</summary>
        public event Action<Key, ModifierKeys> KeyCaptured;

        private bool _capturing;
        private volatile bool _suppressed;

        /// <summary>
        /// The one exception to "normal mode never swallows". A canvas-hosted panel cannot hold
        /// keyboard focus, so Esc-to-dismiss has to come through here — and if the game saw the
        /// same Esc it would open its own menu on top of the panel closing. Installed only while
        /// such a panel is up, called with the foreground process name; return true to swallow.
        /// Plain Esc only: chords fall through untouched.
        /// </summary>
        public Func<string, bool> EscapeConsumer;

        // Capture mode (settings window active): swallow every key-down and report it via KeyCaptured,
        // so rebinding doesn't trigger the hotkeys being rebound.
        public void BeginCapture() => _capturing = true;
        public void EndCapture() => _capturing = false;

        /// <summary>Stop OUR hotkeys firing without swallowing anything — used while the settings
        /// window is focused, so F3 doesn't summon the overlay from under the dialog while ordinary
        /// typing, Alt+Tab and system shortcuts all still reach Windows normally. Distinct from
        /// capture mode, which really does eat every key and only runs during a rebind.</summary>
        public void Suppress(bool on) => _suppressed = on;

        public HotkeyManager()
        {
            _proc = HookCallback;
        }

        public void AddKey(Key key) => AddKey(key, ModifierKeys.None);

        public void AddKey(Key key, ModifierKeys mods)
        {
            if (key == Key.None) return;
            _targets.Add(new Binding { Vk = KeyInterop.VirtualKeyFromKey(key), Key = key, Mods = mods });
        }

        public void ClearKeys() => _targets.Clear();

        public bool IsInstalled => _hookId != IntPtr.Zero;

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            using (var proc = Process.GetCurrentProcess())
            using (var module = proc.MainModule)
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        }

        public void Uninstall()
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        public void Dispose() => Uninstall();

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    var mods = CurrentModifiers();
                    if (_capturing)
                    {
                        try { KeyCaptured?.Invoke(KeyInterop.KeyFromVirtualKey(vk), mods); } catch { }
                        return (IntPtr)1;   // swallow during rebind
                    }
                    if (!_suppressed)
                    {
                        if (vk == VK_ESCAPE && mods == ModifierKeys.None)
                        {
                            var consumer = EscapeConsumer;
                            bool eaten = false;
                            if (consumer != null)
                            {
                                try { eaten = consumer(GetForegroundProcessName()); } catch { }
                            }
                            if (eaten) return (IntPtr)1;
                        }
                        // EXACT match on the modifiers, in both directions: Ctrl+H must not trip a
                        // binding on plain H, and a plain H binding must not swallow the meaning of
                        // every chord that happens to end in H.
                        foreach (var t in _targets)
                        {
                            if (t.Vk != vk || t.Mods != mods) continue;
                            try { HotkeyPressed?.Invoke(t.Key, t.Mods, GetForegroundProcessName()); } catch { }
                            break;
                        }
                        // fall through — normal mode never swallows
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>What is held right now. Read inside the hook, so it must be the ASYNC state:
        /// GetKeyState reports the queue of the calling thread, which for a global hook is not the
        /// thread the keystroke belongs to.</summary>
        private static ModifierKeys CurrentModifiers()
        {
            var mods = ModifierKeys.None;
            if (Down(VK_CONTROL)) mods |= ModifierKeys.Control;
            if (Down(VK_SHIFT)) mods |= ModifierKeys.Shift;
            if (Down(VK_MENU)) mods |= ModifierKeys.Alt;
            return mods;
        }

        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static string GetForegroundProcessName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "(none)";
                GetWindowThreadProcessId(hwnd, out uint pid);
                using (var proc = Process.GetProcessById((int)pid))
                    return proc.ProcessName;
            }
            catch { return "?"; }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
