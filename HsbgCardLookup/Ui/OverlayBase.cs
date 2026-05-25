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
    /// Shared overlay chrome: a Topmost, borderless, transparent-edged window that hides on
    /// Esc or focus loss and toggles open/closed. Variants supply their own content via
    /// <see cref="SetRoot"/>. This is shared infrastructure — deleting a variant file does
    /// not touch it.
    /// </summary>
    public abstract class OverlayBase : Window
    {
        protected OverlayBase(double width, double height)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;        // requires WindowStyle.None; gives rounded edges
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Transparent;  // the inner panel paints the (mostly) opaque bg

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Hide(); };
            // Clicking back into the game dismisses it — but not while a filter dropdown
            // (a Popup, which can briefly deactivate the window) is open.
            Deactivated += (s, e) => { if (_popupGuard == 0) Hide(); };
        }

        private int _popupGuard;
        public void BeginPopup() => _popupGuard++;
        public void EndPopup() { if (_popupGuard > 0) _popupGuard--; }

        protected void SetRoot(UIElement content)
        {
            // Overlay the panel with a thin transparent strip along the very top that drags the
            // window (the window is borderless, so there's no title bar otherwise).
            var grid = new Grid();
            grid.Children.Add(content);

            var dragBar = new System.Windows.Controls.Border
            {
                Height = 18,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,   // transparent but hit-testable
                Cursor = Cursors.SizeAll
            };
            dragBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { /* DragMove throws if button released mid-call */ }
                }
            };
            grid.Children.Add(dragBar);

            Content = grid;
        }

        public void Toggle()
        {
            if (IsVisible) Hide();
            else
            {
                Show();
                ForceForeground();   // lift the cross-process focus lock, then activate
                Activate();
            }
        }

        public void HideIfOpen()
        {
            if (IsVisible) Hide();
        }

        /// <summary>
        /// When the hotkey fires while another *process* owns the foreground, Windows refuses to
        /// let us steal it (the SetForegroundWindow lock), so Show()+Activate() leaves the overlay
        /// visible but unfocused — typing goes nowhere and our Activated handler never fires.
        /// Briefly attaching our input queue to the foreground thread lifts that lock for the
        /// duration of the call, so SetForegroundWindow succeeds and the search box can focus.
        /// (If another window later appears *over* us it just deactivates us — same as alt-tab —
        /// which is the intended dismiss.)
        /// </summary>
        private void ForceForeground()
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
                    BringWindowToTop(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(foreThread, ourThread, false);
                }
            }
            catch { /* focus is best-effort; never throw out of a UI callback */ }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    }
}
