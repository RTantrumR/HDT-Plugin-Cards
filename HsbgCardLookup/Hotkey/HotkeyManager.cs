using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace HsbgCardLookup.Hotkey
{
    /// <summary>
    /// System-wide single-key hotkey via a low-level keyboard hook (WH_KEYBOARD_LL).
    /// Chosen over RegisterHotKey because it (a) supports binding to virtually any key and
    /// (b) lets us decide per-press whether to act/swallow based on the foreground window,
    /// instead of hijacking the key across the whole OS.
    ///
    /// Install/Uninstall must run on a thread that pumps Win32 messages. HDT calls
    /// OnLoad/OnUnload on its WPF UI thread, which does, so that's where we live.
    ///
    /// Spike note: this build OBSERVES only — it never swallows the key. It raises
    /// <see cref="HotkeyPressed"/> with the current foreground process name so we can tell
    /// "fired while Hearthstone focused" from "blocked by HS" from "never fired".
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

        /// <summary>
        /// Raised for EVERY key while in capture mode (see <see cref="BeginCapture"/>). Used by the
        /// settings dialog to read a key to rebind. Fires on the hook thread (HDT's UI thread).
        /// </summary>
        public event Action<Key> KeyCaptured;

        private bool _capturing;

        /// <summary>
        /// Enter capture mode: every key-down is swallowed (so it never summons an overlay or
        /// reaches the game) and reported via <see cref="KeyCaptured"/>. Used while the settings
        /// window is active so rebinding doesn't trigger the very hotkeys being rebound.
        /// </summary>
        public void BeginCapture() => _capturing = true;
        public void EndCapture() => _capturing = false;

        /// <summary>
        /// Decides whether to swallow the key (block it from the foreground app) for the given
        /// foreground process name. Null = never swallow. Kept fast — runs inside the hook.
        /// </summary>
        public Func<string, bool> ShouldSwallow;

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
                        // Rebind in progress: report the key and swallow it (return non-zero), so
                        // it neither summons an overlay, reaches the game, nor bubbles anywhere.
                        try { KeyCaptured?.Invoke(KeyInterop.KeyFromVirtualKey(vk)); }
                        catch { /* never let the hook callback throw */ }
                        return (IntPtr)1;
                    }
                    if (_targets.TryGetValue(vk, out Key key))
                    {
                        try { HotkeyPressed?.Invoke(key, GetForegroundProcessName()); }
                        catch { /* never let the hook callback throw */ }
                        // Normal mode: fall through to CallNextHookEx (do NOT swallow).
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
