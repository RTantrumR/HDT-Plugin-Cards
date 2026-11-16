using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Toolkit.Standalone
{
    /// <summary>
    /// Finds the Hearthstone window and its client area in screen pixels. Every caller (the 60 Hz
    /// overlay glue, the reader thread, the idle watcher) goes through here, so it never lists
    /// processes: <c>Process.GetProcessesByName</c> snapshots every process AND thread on the machine
    /// and measured ~6.5 MB of extra resident memory in a tray app polling it every 3 s. Instead it
    /// walks the few top-level Unity windows and asks which exe owns each.
    /// </summary>
    internal static class HsWindow
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("kernel32")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static readonly object Gate = new object();
        private static IntPtr _cached;
        private static int _cachedPid;

        /// <summary>Hearthstone's main window, or zero. Any thread.</summary>
        public static IntPtr Find()
        {
            int pid;
            return Find(out pid);
        }

        /// <summary>Hearthstone's main window and process id, or zero for both. Any thread.</summary>
        public static IntPtr Find(out int pid)
        {
            lock (Gate)
            {
                if (_cached != IntPtr.Zero && IsWindow(_cached)) { pid = _cachedPid; return _cached; }
                _cached = IntPtr.Zero; _cachedPid = 0;
                // The title is "Hearthstone" for most clients but not all (the Chinese one is localized),
                // so match on the class and the owning exe, not the title.
                for (IntPtr w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "UnityWndClass", null); w != IntPtr.Zero;
                     w = FindWindowEx(IntPtr.Zero, w, "UnityWndClass", null))
                {
                    int p;
                    GetWindowThreadProcessId(w, out p);
                    if (p != 0 && IsHearthstoneExe(p)) { _cached = w; _cachedPid = p; break; }
                }
                pid = _cachedPid;
                return _cached;
            }
        }

        private static bool IsHearthstoneExe(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size)
                    && Path.GetFileName(sb.ToString()).Equals("Hearthstone.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Client area in screen pixels; false when there is no visible, foreground HS window.</summary>
        public static bool ClientArea(out int x, out int y, out int w, out int h, out bool foreground)
        {
            x = y = w = h = 0; foreground = false;
            var hwnd = Find();
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
            RECT r;
            if (!GetClientRect(hwnd, out r)) return false;
            var pt = new POINT();
            if (!ClientToScreen(hwnd, ref pt)) return false;
            x = pt.X; y = pt.Y; w = r.Right - r.Left; h = r.Bottom - r.Top;
            foreground = GetForegroundWindow() == hwnd;
            return w > 0 && h > 0;
        }
    }
}
