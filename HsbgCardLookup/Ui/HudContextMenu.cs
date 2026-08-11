using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Tiny right-click menu for HUD cards (trinkets/anomaly). A real WPF ContextMenu would activate
    /// its popup and steal foreground from the game, so this is another no-activate/topmost window
    /// (the FloatingCard/DarkGiftPanel pattern). Spawns under the cursor; closes on a click, on
    /// mouse-leave, or after a timeout.
    /// </summary>
    public sealed class HudContextMenu : Window
    {
        private static HudContextMenu _open;   // at most one menu at a time
        private readonly DispatcherTimer _timeout;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xF2, 0x10, 0x14, 0x1C));

        public static void ShowMenu(IReadOnlyList<KeyValuePair<string, Action>> items)
        {
            try { _open?.Close(); } catch { }
            _open = new HudContextMenu(items);
            _open.Show();
        }

        private HudContextMenu(IReadOnlyList<KeyValuePair<string, Action>> items)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var stack = new StackPanel { MinWidth = 170 };
            foreach (var it in items)
            {
                var row = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(13, 7, 13, 7),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = it.Key, Foreground = UiKit.TextPrimary, FontSize = 13 }
                };
                var act = it.Value;
                row.MouseEnter += (s, e) => row.Background = UiKit.Br(UiKit.PanelActive);
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; CloseMenu(); try { act?.Invoke(); } catch { } };
                stack.Children.Add(row);
            }

            Content = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 4, 0, 4),
                Child = stack
            };

            MouseLeave += (s, e) => CloseMenu();
            _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _timeout.Tick += (s, e) => CloseMenu();
            _timeout.Start();
            Closed += (s, e) => { _timeout.Stop(); if (ReferenceEquals(_open, this)) _open = null; };
            SourceInitialized += OnSourceInitialized;

            new WindowInteropHelper(this).EnsureHandle();
            PlaceAtCursor();
            Dispatcher.BeginInvoke(new Action(ClampToWorkArea), DispatcherPriority.Loaded);
        }

        private void CloseMenu() { try { Close(); } catch { } }

        // Slight overlap so the cursor starts INSIDE the menu — MouseLeave then closes it naturally.
        private void PlaceAtCursor()
        {
            GetCursorPos(out POINT p);
            var dip = new Point(p.X, p.Y);
            try
            {
                var ct = PresentationSource.FromVisual(this)?.CompositionTarget;
                if (ct != null) dip = ct.TransformFromDevice.Transform(dip);
            }
            catch { }
            Left = dip.X - 8;
            Top = dip.Y - 8;
        }

        private void ClampToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double w = ActualWidth > 0 ? ActualWidth : 180, h = ActualHeight > 0 ? ActualHeight : 70;
                if (Left + w > wa.Right) Left = wa.Right - w;
                if (Top + h > wa.Bottom) Top = wa.Bottom - h;
                if (Left < wa.Left) Left = wa.Left;
                if (Top < wa.Top) Top = wa.Top;
            }
            catch { }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var src = (HwndSource)PresentationSource.FromVisual(this);
                if (src != null)
                {
                    src.AddHook(WndProc);
                    int ex = GetWindowLong(src.Handle, GWL_EXSTYLE);
                    SetWindowLong(src.Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
