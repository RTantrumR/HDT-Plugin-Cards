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

        private readonly LowLevelKeyboardProc _proc;   // kept alive for the hook's lifetime
        private IntPtr _hookId = IntPtr.Zero;
        private readonly Dictionary<int, Key> _targets = new Dictionary<int, Key>();

        /// <summary>Raised when a registered key is pressed. Args: the key, and foreground process name.</summary>
        public event Action<Key, string> HotkeyPressed;

        /// <summary>Raised for every key while in capture mode — the settings dialog reads it to
        /// rebind. Fires on the hook thread.</summary>
        public event Action<Key> KeyCaptured;

        private bool _capturing;

        // Capture mode (settings window active): swallow every key-down and report it via KeyCaptured,
        // so rebinding doesn't trigger the hotkeys being rebound.
        public void BeginCapture() => _capturing = true;
        public void EndCapture() => _capturing = false;

        public HotkeyManager()
        {
            _proc = HookCallback;
        }

        public void AddKey(Key key)
        {
            _targets[KeyInterop.VirtualKeyFromKey(key)] = key;
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
                    if (_capturing)
                    {
                        try { KeyCaptured?.Invoke(KeyInterop.KeyFromVirtualKey(vk)); } catch { }
                        return (IntPtr)1;   // swallow during rebind
                    }
                    if (_targets.TryGetValue(vk, out Key key))
                    {
                        try { HotkeyPressed?.Invoke(key, GetForegroundProcessName()); } catch { }
                        // fall through — normal mode never swallows
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

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
