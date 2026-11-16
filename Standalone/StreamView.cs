using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Toolkit.Standalone
{
    /// <summary>
    /// A window for OBS. Game Capture records only Hearthstone's own frames, and the toolkit's real windows are
    /// tool windows (kept out of the taskbar and Alt-Tab), which OBS's Window Capture won't list; the overlay
    /// also hides whenever Hearthstone isn't in front. This is an ordinary window instead: sized like
    /// Hearthstone's client area, a solid key colour behind, and on it a live mirror (<see cref="VisualBrush"/>)
    /// of the overlay canvas and of every other toolkit window that sits over the game (the F3 search,
    /// floating cards), each at its place relative to the game. OBS captures it under Game Capture with a
    /// Color Key filter. Its title is fixed and never translated: OBS finds the window again by it.
    /// </summary>
    internal sealed class StreamView : Window
    {
        public const string WindowTitle = "Tantrum's BG Toolkit - Stream view";

        [System.Runtime.InteropServices.DllImport("user32")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);

        private readonly OverlayWindow _overlay;
        private readonly Canvas _root = new Canvas { ClipToBounds = true };
        private readonly Rectangle _surface = new Rectangle();
        private readonly Dictionary<Window, Rectangle> _mirrors = new Dictionary<Window, Rectangle>();
        private readonly DispatcherTimer _tick;
        private string _key;

        /// <summary>The player closed the window (title bar, Alt+F4, taskbar): not the toolkit closing it.</summary>
        public bool UserClosed { get; private set; }

        public StreamView(OverlayWindow overlay, string key)
        {
            _overlay = overlay;
            Title = WindowTitle;
            ShowInTaskbar = true;
            ShowActivated = false;
            Topmost = false;
            // Borderless: with a title bar the window outgrows a 1080p screen and Windows cuts its bottom off.
            // It stays on the taskbar, which is where the player closes it.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            try { Icon = BitmapFrame.Create(new Uri(System.IO.Path.Combine(Shared.ExeDir, "toolkit.ico"))); } catch { }
            _root.Width = 1280; _root.Height = 720;
            _surface.Fill = new VisualBrush(overlay.Surface)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            };
            _root.Children.Add(_surface);
            Content = _root;
            SetKey(key);

            // 10 Hz is plenty for placement and size; the mirrored content itself updates as it renders.
            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += (s, e) => Sync();
            Left = 0; Top = 0;
            // Opened behind everything: it is for OBS, not for the player, and must never cover the game.
            Loaded += (s, e) =>
            {
                Sync(); _tick.Start();
                SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);  // HWND_BOTTOM; NOSIZE|NOMOVE|NOACTIVATE
            };
            // A close request from the player arrives as WM_SYSCOMMAND/SC_CLOSE; Close() from code and the
            // app shutting down don't send it.
            SourceInitialized += (s, e) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == 0x0112 && ((long)wParam & 0xFFF0) == 0xF060) UserClosed = true;
                    return IntPtr.Zero;
                });
            Closed += (s, e) => _tick.Stop();
        }

        /// <summary>"Green" or "Magenta": the colour OBS's Color Key filter removes.</summary>
        public void SetKey(string key)
        {
            if (key == _key) return;
            _key = key;
            var c = string.Equals(key, "Magenta", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(255, 0, 255) : Color.FromRgb(0, 255, 0);
            var brush = new SolidColorBrush(c); brush.Freeze();
            Background = brush;
            _root.Background = brush;
        }

        private void Sync()
        {
            // Hearthstone's client area in device pixels → this window's DIPs.
            var src = PresentationSource.FromVisual(this);
            var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            int x, y, w, h; bool fg;
            bool hs = HsWindow.ClientArea(out x, out y, out w, out h, out fg);
            if (hs)
            {
                var size = toDip.Transform(new Vector(w, h));
                if (Math.Abs(_root.Width - size.X) > 0.5) _root.Width = size.X;
                if (Math.Abs(_root.Height - size.Y) > 0.5) _root.Height = size.Y;
            }
            var origin = toDip.Transform(new Point(x, y));

            // The overlay canvas fills the game area (the overlay window is glued to it).
            _surface.Width = _overlay.Surface.ActualWidth;
            _surface.Height = _overlay.Surface.ActualHeight;

            // Other toolkit windows over the game: mirrored where they sit relative to it.
            var seen = new HashSet<Window>();
            foreach (Window win in Application.Current.Windows)
            {
                if (!IsMirrored(win)) continue;
                var visual = win.Content as Visual;
                if (visual == null) continue;
                seen.Add(win);
                Rectangle r;
                if (!_mirrors.TryGetValue(win, out r))
                {
                    r = new Rectangle { Fill = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } };
                    _mirrors[win] = r;
                    _root.Children.Add(r);
                }
                r.Width = win.ActualWidth; r.Height = win.ActualHeight;
                Canvas.SetLeft(r, win.Left - origin.X);
                Canvas.SetTop(r, win.Top - origin.Y);
            }
            foreach (var gone in new List<Window>(_mirrors.Keys))
                if (!seen.Contains(gone)) { _root.Children.Remove(_mirrors[gone]); _mirrors.Remove(gone); }
        }

        // The F3 search, floating cards and the like: visible toolkit windows that draw over the game. Not
        // this window, the overlay (mirrored whole above), the settings, or menus.
        private bool IsMirrored(Window win)
        {
            if (win == this || win == _overlay || !win.IsVisible || win.WindowState == WindowState.Minimized) return false;
            string type = win.GetType().Name;
            return type != "SettingsWindow" && type != "HudContextMenu";
        }
    }
}
