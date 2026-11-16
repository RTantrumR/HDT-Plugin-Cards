using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Toolkit.Standalone
{
    /// <summary>
    /// A transparent, never-activated window kept exactly over Hearthstone's client area, shown only
    /// while Hearthstone is the foreground window. Its <see cref="Surface"/> canvas is what the
    /// plugin's own features draw on — the standalone counterpart of HDT's overlay canvas.
    ///
    /// Clicks: the window is click-through (WS_EX_TRANSPARENT) except while the cursor is over an
    /// element registered with <see cref="RegisterClickable"/>; then it takes real mouse input. It is
    /// WS_EX_NOACTIVATE and answers WM_MOUSEACTIVATE with MA_NOACTIVATE, so a click never moves the
    /// foreground off Hearthstone. The same idea HDT's overlay uses for plugin elements.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        public readonly Canvas Surface = new Canvas();

        /// <summary>Test switch (--ignore-focus): draw even when Hearthstone isn't the foreground window.</summary>
        public bool IgnoreFocus;
        public Action<string> Log;

        private int _lastX = int.MinValue, _lastY, _lastW, _lastH;
        private readonly List<WeakReference<FrameworkElement>> _clickable = new List<WeakReference<FrameworkElement>>();
        private bool _clickThrough = true;
        private readonly DispatcherTimer _glue;
        private static readonly TimeSpan ShownRate = TimeSpan.FromMilliseconds(16), HiddenRate = TimeSpan.FromMilliseconds(250);
        private WinEventProc _onForeground;     // held: the hook calls it from native code
        private IntPtr _foregroundHook;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000; Top = -10000; Width = 10; Height = 10;
            Content = Surface;

            SourceInitialized += (s, e) =>
            {
                MakeClickThrough();
                HwndSource.FromHwnd(Hwnd)?.AddHook(WndProc);
                // Alt-tab back into Hearthstone shows the overlay at once, without polling fast for it.
                _onForeground = (hook, ev, hwnd, obj, child, thread, time) => Glue();
                _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
            };
            Closed += (s, e) => { if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook); };

            // Glue: follow the HS window, hide when it isn't in front. 60 Hz while shown (window moves,
            // hover over clickable elements); 4 Hz while hidden, when the foreground hook does the waking.
            _glue = new DispatcherTimer(DispatcherPriority.Render) { Interval = HiddenRate };
            _glue.Tick += (s, e) => Glue();
            _glue.Start();
        }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
        private const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        [DllImport("user32")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32")] private static extern bool GetCursorPos(out POINT p);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, VK_LBUTTON = 1, VK_RBUTTON = 2;
        [DllImport("user32")] private static extern short GetAsyncKeyState(int vKey);

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
            return IntPtr.Zero;
        }

        /// <summary>Lets <paramref name="element"/> take real mouse input while the rest of the window stays click-through.</summary>
        public void RegisterClickable(FrameworkElement element)
        {
            if (element == null) return;
            _clickable.RemoveAll(w => { FrameworkElement e; return !w.TryGetTarget(out e) || e == element; });
            _clickable.Add(new WeakReference<FrameworkElement>(element));
            Log?.Invoke("clickable registered: " + element.GetType().Name + (string.IsNullOrEmpty(element.Name) ? "" : " " + element.Name) + " (" + _clickable.Count + " live)");
        }

        // Is the cursor over any registered element that is actually on screen in this window?
        private FrameworkElement ClickableUnderCursor()
        {
            POINT c;
            if (_clickable.Count == 0 || !GetCursorPos(out c)) return null;
            Point p;
            try { p = Surface.PointFromScreen(new Point(c.X, c.Y)); } catch { return null; }
            FrameworkElement hit = null;
            _clickable.RemoveAll(w =>
            {
                FrameworkElement e;
                if (!w.TryGetTarget(out e)) return true;
                if (hit != null || !e.IsLoaded || !e.IsVisible || e.ActualWidth <= 0 || e.ActualHeight <= 0) return false;
                try
                {
                    if (!Surface.IsAncestorOf(e)) return false;
                    var r = e.TransformToAncestor(Surface).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                    if (r.Contains(p)) hit = e;
                }
                catch { }
                return false;
            });
            return hit;
        }

        private void SetClickThrough(bool on, FrameworkElement over)
        {
            if (on == _clickThrough) return;
            _clickThrough = on;
            var h = Hwnd;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
            Log?.Invoke(on ? "click-through on" : "clickable: " + (over?.GetType().Name ?? "?") + (string.IsNullOrEmpty(over?.Name) ? "" : " " + over.Name));
        }

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

        private void MakeClickThrough()
        {
            var h = Hwnd;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        }

        private void Glue()
        {
            int x, y, w, h; bool fg;
            bool hs = HsWindow.ClientArea(out x, out y, out w, out h, out fg);
            bool want = hs && (fg || IgnoreFocus);
            if (!want)
            {
                if (Visibility == Visibility.Visible) { SetClickThrough(true, null); Hide(); Log?.Invoke("overlay hidden (hs=" + hs + " foreground=" + fg + ")"); }
                if (_glue.Interval != HiddenRate) _glue.Interval = HiddenRate;
                return;
            }
            if (_glue.Interval != ShownRate) _glue.Interval = ShownRate;
            if (x != _lastX || y != _lastY || w != _lastW || h != _lastH || Visibility != Visibility.Visible)
            {
                if (Visibility != Visibility.Visible) { Show(); Log?.Invoke("overlay shown (foreground=" + fg + ")"); }
                SetWindowPos(Hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                _lastX = x; _lastY = y; _lastW = w; _lastH = h;
            }
            // While a button is held, keep whatever state the press started in: a drag that outruns its
            // element must not turn the window click-through mid-gesture.
            // (GetAsyncKeyState, not WPF's Mouse: that only knows about presses this window received.)
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0) return;
            var over = ClickableUnderCursor();
            SetClickThrough(over == null, over);
        }

        /// <summary>Test aid: Hearthstone's own frame (PrintWindow) with this overlay drawn on top, as a PNG.</summary>
        public string SaveComposite(string path)
        {
            int x, y, w, h; bool fg;
            if (!HsWindow.ClientArea(out x, out y, out w, out h, out fg)) return "no HS window";
            using (var bmp = new System.Drawing.Bitmap(w, h))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok = PrintWindow(HsWindow.Find(), dc, 0x1 | 0x2);   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                    g.ReleaseHdc(dc);
                    if (!ok) return "PrintWindow failed";
                    if (ActualWidth > 0 && Visibility == Visibility.Visible)
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                        rtb.Render(Surface);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var ms = new MemoryStream())
                        {
                            enc.Save(ms); ms.Position = 0;
                            using (var over = System.Drawing.Image.FromStream(ms)) g.DrawImage(over, 0, 0, w, h);
                        }
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return "saved " + w + "x" + h + (Visibility == Visibility.Visible ? "" : " (overlay hidden)");
        }
    }
}
