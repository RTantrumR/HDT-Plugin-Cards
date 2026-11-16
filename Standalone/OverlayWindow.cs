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
    /// A transparent, never-activated window kept exactly over Hearthstone's client area, drawn only
    /// while Hearthstone (or one of the toolkit's own windows over it, like the F3 search) is the
    /// foreground window. Its <see cref="Surface"/> canvas is what the plugin's own features draw on:
    /// the standalone counterpart of HDT's overlay canvas.
    ///
    /// Streaming: this window is what OBS captures, the way HDT's own overlay is captured. It is not a
    /// tool window (OBS's Window Capture list skips those), its title is fixed and never translated (OBS
    /// finds the window again by it), and it is never hidden, only faded to fully transparent, because
    /// OBS stops capturing a hidden window. The other toolkit windows over the game (F3 search, floating
    /// cards) are mirrored onto it under their real windows, so one capture carries everything.
    /// The UI thread's name puts "Chrome" into WPF's window class, which OBS's defaults read as "capture
    /// this with Windows Graphics Capture, find it by title" (see Program.Main).
    ///
    /// Clicks: the window is click-through (WS_EX_TRANSPARENT) except while the cursor is over an
    /// element registered with <see cref="RegisterClickable"/>; then it takes real mouse input. It is
    /// WS_EX_NOACTIVATE and answers WM_MOUSEACTIVATE with MA_NOACTIVATE, so a click never moves the
    /// foreground off Hearthstone. The same idea HDT's overlay uses for plugin elements.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        public const string WindowTitle = "Tantrum's BG Toolkit - Overlay";

        public readonly Canvas Surface = new Canvas();
        private readonly Canvas _mirrorLayer = new Canvas { IsHitTestVisible = false };
        private readonly Dictionary<Window, System.Windows.Shapes.Rectangle> _mirrors = new Dictionary<Window, System.Windows.Shapes.Rectangle>();
        private readonly int _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        private bool _drawn;

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
            Title = WindowTitle;
            Opacity = 0;
            Left = -10000; Top = -10000; Width = 10; Height = 10;
            var root = new Grid();
            root.Children.Add(Surface);
            root.Children.Add(_mirrorLayer);
            Content = root;

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
            // No WS_EX_TOOLWINDOW: OBS lists only non-tool windows. It stays out of the taskbar and Alt-Tab
            // anyway: WPF gives a ShowInTaskbar=false window a hidden owner, and it is WS_EX_NOACTIVATE.
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        }

        [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4;

        // The foreground window is one of ours that sits over the game (the F3 search, a HUD menu): the
        // overlay keeps drawing under it. Not the settings, which the overlay must not cover.
        private bool OwnWindowInFront()
        {
            var fg = GetForegroundWindow();
            int pid;
            if (fg == IntPtr.Zero || GetWindowThreadProcessId(fg, out pid) == 0 || pid != _pid) return false;
            var win = HwndSource.FromHwnd(fg)?.RootVisual as Window;
            return win != null && win != this && win.GetType().Name != "SettingsWindow";
        }

        private void Glue()
        {
            int x, y, w, h; bool fg;
            bool hs = HsWindow.ClientArea(out x, out y, out w, out h, out fg);
            bool ownInFront = hs && !fg && OwnWindowInFront();
            bool want = hs && (fg || ownInFront || IgnoreFocus);
            if (!IsVisible) Show();     // once; from then on it is faded, never hidden (see the class note)
            // Follow Hearthstone even while faded, so a capture keeps the game's size. Raise it to the top
            // of the topmost band only while Hearthstone itself is in front: over our own windows it stays
            // where it is, under the F3 search.
            if (hs && (x != _lastX || y != _lastY || w != _lastW || h != _lastH))
            {
                SetWindowPos(Hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | (fg ? 0 : SWP_NOZORDER));
                _lastX = x; _lastY = y; _lastW = w; _lastH = h;
            }
            if (!want)
            {
                if (_drawn) { _drawn = false; Opacity = 0; SetClickThrough(true, null); Log?.Invoke("overlay faded (hs=" + hs + " foreground=" + fg + ")"); }
                if (_glue.Interval != HiddenRate) _glue.Interval = HiddenRate;
                return;
            }
            if (_glue.Interval != ShownRate) _glue.Interval = ShownRate;
            if (!_drawn)
            {
                _drawn = true; Opacity = 1;
                if (fg) SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                Log?.Invoke("overlay drawn (foreground=" + (fg ? "hs" : ownInFront ? "toolkit" : "ignored") + ")");
            }
            SyncMirrors();
            // While a button is held, keep whatever state the press started in: a drag that outruns its
            // element must not turn the window click-through mid-gesture.
            // (GetAsyncKeyState, not WPF's Mouse: that only knows about presses this window received.)
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0) return;
            var over = ClickableUnderCursor();
            SetClickThrough(over == null, over);
        }

        // Mirrors of the toolkit's other windows over the game, at their places relative to the game. On
        // screen each sits under its real window; in a capture of this window it is the only copy.
        private void SyncMirrors()
        {
            HashSet<Window> seen = null;
            foreach (Window win in Application.Current.Windows)
            {
                if (win == this || !win.IsVisible || win.WindowState == WindowState.Minimized) continue;
                string type = win.GetType().Name;
                if (type == "SettingsWindow") continue;
                var visual = win.Content as Visual;
                if (visual == null) continue;
                Point at;
                try { at = Surface.PointFromScreen(win.PointToScreen(new Point(0, 0))); } catch { continue; }
                (seen ?? (seen = new HashSet<Window>())).Add(win);
                System.Windows.Shapes.Rectangle r;
                if (!_mirrors.TryGetValue(win, out r))
                {
                    r = new System.Windows.Shapes.Rectangle
                    {
                        Fill = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }
                    };
                    _mirrors[win] = r;
                    _mirrorLayer.Children.Add(r);
                }
                if (r.Width != win.ActualWidth) r.Width = win.ActualWidth;
                if (r.Height != win.ActualHeight) r.Height = win.ActualHeight;
                if (Canvas.GetLeft(r) != at.X) Canvas.SetLeft(r, at.X);
                if (Canvas.GetTop(r) != at.Y) Canvas.SetTop(r, at.Y);
            }
            if (_mirrors.Count == (seen?.Count ?? 0)) return;
            foreach (var gone in new List<Window>(_mirrors.Keys))
                if (seen == null || !seen.Contains(gone)) { _mirrorLayer.Children.Remove(_mirrors[gone]); _mirrors.Remove(gone); }
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
                    if (ActualWidth > 0 && _drawn)
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                        rtb.Render((Visual)Content);     // the surface and the mirrors: what a capture gets
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
            return "saved " + w + "x" + h + (_drawn ? "" : " (overlay faded)");
        }
    }
}
