using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HsbgCardLookup.Ui
{
    /// <summary>Shared Hearthstone-window geometry.</summary>
    internal static class HsGeometry
    {
        /// <summary>The HS window's client size in px (≈ the player's HS resolution — used to scale
        /// pixel offsets that were tuned at 1920×1080). False if HS isn't running / has no window.</summary>
        public static bool TryClientSize(out int w, out int h)
        {
            w = h = 0;
            try
            {
                var hs = Process.GetProcessesByName("Hearthstone");
                if (hs.Length == 0) return false;
                var hwnd = hs[0].MainWindowHandle;
                foreach (var p in hs) p.Dispose();
                if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out RECT cr)) return false;
                w = cr.right - cr.left; h = cr.bottom - cr.top;
                return w > 0 && h > 0;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    }
}
