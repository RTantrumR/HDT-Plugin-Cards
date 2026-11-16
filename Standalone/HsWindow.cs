using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Toolkit.Standalone
{
    /// <summary>Finds the Hearthstone window and its client area in screen pixels.</summary>
    internal static class HsWindow
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("user32")] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32")] private static extern IntPtr GetForegroundWindow();

        private static IntPtr _cached;

        public static IntPtr Find()
        {
            if (_cached != IntPtr.Zero && IsWindow(_cached)) return _cached;
            _cached = FindWindow("UnityWndClass", "Hearthstone");
            if (_cached == IntPtr.Zero)
                foreach (var p in Process.GetProcessesByName("Hearthstone"))
                    if (p.MainWindowHandle != IntPtr.Zero) { _cached = p.MainWindowHandle; break; }
            return _cached;
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
