using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Shared overlay chrome: a borderless, transparent-edged, topmost window that toggles open/closed
    /// and dismisses on Esc / focus loss. Variants supply content via <see cref="SetRoot"/>.
    /// </summary>
    public abstract class OverlayBase : Window
    {
        protected OverlayBase(double width, double height)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Transparent;

            // Focus mode: a normal activating window that takes the OS foreground on summon so the
            // search box gets real keyboard focus (native typing). HDT hides its own overlay while
            // ours is open — accepted trade-off.
            ShowActivated = true;
            SourceInitialized += (s, e) => ApplyToolWindow();
            // Dismiss on lost activation, unless a popup (dropdown / notification panel) stole it.
            Deactivated += (s, e) => { if (_popupGuard == 0) Hide(); };
        }

        // Take foreground + keyboard focus. AttachThreadInput to the current foreground thread lifts
        // Windows' foreground lock so SetForegroundWindow succeeds from our background process.
        private void ForceForegroundAndFocus()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                IntPtr fore = GetForegroundWindow();
                uint foreThread = fore == IntPtr.Zero ? 0u : GetWindowThreadProcessId(fore, out _);
                uint ourThread = GetCurrentThreadId();
                bool attached = foreThread != 0 && foreThread != ourThread
                                && AttachThreadInput(foreThread, ourThread, true);
                try
                {
                    SetForegroundWindow(hwnd);
                    SetFocus(hwnd);
                }
                finally { if (attached) AttachThreadInput(foreThread, ourThread, false); }
                Activate();
            }
            catch { }
        }

        // WS_EX_TOOLWINDOW keeps the overlay out of the taskbar / Alt-Tab list (no WS_EX_NOACTIVATE —
        // focus mode wants activation).
        private void ApplyToolWindow()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            }
            catch { }
        }

        // Guards the focus-loss auto-hide while a WPF Popup (which briefly deactivates us) is open.
        private int _popupGuard;
        public void BeginPopup() => _popupGuard++;
        public void EndPopup() { if (_popupGuard > 0) _popupGuard--; }

        protected void SetRoot(UIElement content)
        {
            var grid = new Grid();
            grid.Children.Add(content);

            // Transparent top strip to drag the borderless window.
            var dragBar = new System.Windows.Controls.Border
            {
                Height = 18,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll
            };
            dragBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };
            grid.Children.Add(dragBar);

            Content = grid;
        }

        public void Toggle()
        {
            if (IsVisible) Hide();
            else { Show(); ForceForegroundAndFocus(); }
        }

        public void HideIfOpen()
        {
            if (IsVisible) Hide();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);
    }
}
