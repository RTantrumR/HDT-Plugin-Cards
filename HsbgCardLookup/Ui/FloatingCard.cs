using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HsbgCardLookup.Ui
{
    /// <summary>Owner of a <see cref="FloatingCard"/> — receives close + geometry-change callbacks.
    /// Implemented by <see cref="FloatingCardManager"/> (dragged cards) and the BG HUD (auto-cards),
    /// so the same window serves both without knowing which it belongs to.</summary>
    public interface IFloatingCardHost
    {
        void Remove(FloatingCard card);
        void GeometryChanged(FloatingCard card);   // fired when a move/resize gesture ends
    }

    /// <summary>
    /// A single card dragged onto the screen as raw art (no panel/background). A borderless,
    /// transparent, topmost, NO-ACTIVATE window: it never takes the OS foreground, so Hearthstone
    /// stays foreground and HDT keeps showing its own overlay while these float on top.
    ///
    /// Move/resize are driven by Windows itself through <c>WM_NCHITTEST</c> (the whole body reports
    /// <c>HTCAPTION</c> so a left-drag moves it; the top-right corner reports <c>HTTOPRIGHT</c> so a
    /// drag there resizes it). That is far more reliable than WPF mouse-capture for a non-foreground
    /// transparent window — capture there drops the moment the cursor crosses another window, clicks
    /// fall through, and mouse-up gets missed. Resizing is constrained to a proportional scale anchored
    /// at the bottom-left corner via <c>WM_SIZING</c>, clamped between <see cref="MinWidthDip"/> and
    /// 1.5x the art's native pixel width. Right-click dismisses the card.
    /// </summary>
    public sealed class FloatingCard : Window
    {
        private const double MinWidthDip = 100;     // floor so it can't shrink into a buggy speck
        private const double MaxScale = 1.5;        // ceiling vs native px so the static render stays crisp
        private const double Pad = 5;               // transparent grab ring (DIP) around the art on every
                                                    // side, so a 1-px-off click still lands on the card
                                                    // (reports HTCAPTION) instead of focusing the app behind

        private readonly IFloatingCardHost _owner;
        private readonly bool _closable;            // HUD cards are persistent (not user-closable)
        private int _nativeW;                       // art native pixel width (the blur ceiling reference)
        private double _aspect;                     // height / width of the art

        private readonly Image _img;
        private readonly Border _handle;
        private double _dpiScale = 1.0;             // device px per DIP (set once the HWND exists)
        private HwndSource _src;

        public FloatingCard(IFloatingCardHost owner, BitmapSource art, double initialWidth, bool closable = true)
        {
            _owner = owner;
            _closable = closable;

            _nativeW = art.PixelWidth > 0 ? art.PixelWidth : 256;
            _aspect = art.PixelWidth > 0 ? (double)art.PixelHeight / art.PixelWidth : 1.4;

            double w = initialWidth > 0 ? initialWidth : _nativeW;
            w = Clamp(w, MinWidthDip, _nativeW * MaxScale);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;                  // never grab activation/foreground
            ResizeMode = ResizeMode.CanResize;      // keeps WS_THICKFRAME so HTTOPRIGHT sizing works (frame stays invisible under WindowStyle.None)
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = w + 2 * Pad;
            Height = w * _aspect + 2 * Pad;

            _img = new Image
            {
                Source = art,
                Stretch = Stretch.Fill,             // the (window − Pad ring) area holds the native aspect → no distortion
                Margin = new Thickness(Pad)         // inset so the art keeps its size; the ring stays transparent
            };

            // Top-right scale grabber: a rounded square with a diagonal arrow. Purely visual —
            // hit-testing is done in the WndProc; we toggle it on non-client hover. The arrow fills the
            // border (Stretch.Uniform, padded) so it scales with the handle; the handle scales with the
            // card (UpdateHandle). Anchored top-right so its position tracks the corner automatically.
            var arrow = new Path
            {
                Data = Geometry.Parse("M3 9 L9 3 M9 3 L9 6 M9 3 L6 3"),  // ↗ pointing out of the corner
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _handle = new Border
            {
                Width = _handleDip, Height = _handleDip,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, Pad, Pad, 0),   // sit at the art's top-right corner, inside the grab ring
                IsHitTestVisible = false,           // the system handles the hit via WM_NCHITTEST
                Visibility = Visibility.Collapsed,
                Child = arrow
            };

            var root = new Grid();
            root.Children.Add(_img);
            root.Children.Add(_handle);
            Content = root;

            SizeChanged += (s, e) => UpdateHandle();   // keep the handle proportional as the card resizes
            SourceInitialized += OnSourceInitialized;
        }

        // Resize handle sizes itself relative to the card so it's always proportional (small card →
        // small handle, big card → big handle) and stays catchable without eating a small card.
        private const double HandleFrac = 0.16;     // handle ≈ 16% of the card's width…
        private const double HandleMinDip = 16;     // …clamped so it's never a useless speck…
        private const double HandleMaxDip = 44;     // …nor absurdly large on a big card
        private double _handleDip = HandleMinDip;    // current handle size (DIP); tracks the card size

        // The card art's top-right corner is transparent (HS cards are rounded/ornate there), so a handle
        // pinned to the window corner drifts off the visible card as it scales. Inset it ONTO the card by
        // a fraction of the card size so it tracks the art's corner at any scale (X smaller than Y — the
        // top transparent margin is taller than the right one).
        private const double InsetFracX = 0.05;
        private const double InsetFracY = 0.07;
        private double _hitRightDip = Pad;           // handle's right/top inset from the window edges (DIP)…
        private double _hitTopDip = Pad;             // …mirrored by the WM_NCHITTEST grab rect

        /// <summary>Current art display width (DIPs), excluding the grab ring — the manager remembers it
        /// so the next card matches (and it's fed back as the next card's art width).</summary>
        public double DisplayWidth => (ActualWidth > 0 ? ActualWidth : Width) - 2 * Pad;

        /// <summary>Place the window so its center sits under the OS cursor (used while it's being
        /// dragged out of the detail pane, before it's dropped).</summary>
        public void CenterOnCursor()
        {
            var c = CursorDip();
            Left = c.X - Width / 2;
            Top = c.Y - Height / 2;
        }

        /// <summary>Position the window's top-left at the given DIP coordinates (HUD cards restore their
        /// saved placement). The grab ring means the art sits Pad in from this corner.</summary>
        public void Place(double left, double top)
        {
            Left = left;
            Top = top;
        }

        /// <summary>Swap in a higher-res bitmap (e.g. native art that finished loading after a grid
        /// drag spawned from the visible thumbnail). Re-derives the native-px scale ceiling and keeps
        /// the current width, re-clamping it to the new bounds.</summary>
        public void SetArt(BitmapSource art)
        {
            if (art == null || art.PixelWidth <= 0) return;
            _img.Source = art;
            _nativeW = art.PixelWidth;
            _aspect = (double)art.PixelHeight / art.PixelWidth;
            double artW = Clamp((ActualWidth > 0 ? ActualWidth : Width) - 2 * Pad, MinWidthDip, _nativeW * MaxScale);
            Width = artW + 2 * Pad;
            Height = artW * _aspect + 2 * Pad;
        }

        // Size AND position the resize handle relative to the card so it stays proportional and sits on
        // the visible art's top-right corner at any scale (not drifting into the transparent margin).
        private void UpdateHandle()
        {
            double artW = (ActualWidth > 0 ? ActualWidth : Width) - 2 * Pad;
            double artH = (ActualHeight > 0 ? ActualHeight : Height) - 2 * Pad;
            _handleDip = Clamp(artW * HandleFrac, HandleMinDip, HandleMaxDip);
            _handle.Width = _handleDip;
            _handle.Height = _handleDip;
            _hitRightDip = Pad + artW * InsetFracX;
            _hitTopDip = Pad + artH * InsetFracY;
            _handle.Margin = new Thickness(0, _hitTopDip, _hitRightDip, 0);
        }

        private void CloseCard()
        {
            _owner.Remove(this);
            try { Close(); } catch { }
        }

        // ── Win32 plumbing ───────────────────────────────────────────────────────────────────────

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _src = (HwndSource)PresentationSource.FromVisual(this);
                if (_src?.CompositionTarget != null)
                    _dpiScale = _src.CompositionTarget.TransformToDevice.M11;
                if (_src != null)
                {
                    _src.AddHook(WndProc);
                    int ex = GetWindowLong(_src.Handle, GWL_EXSTYLE);
                    SetWindowLong(_src.Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    // Never activate on a click — so clicking a card can't steal foreground from the
                    // overlay/game (which would hide the overlay and, by default, these cards).
                    handled = true;
                    return (IntPtr)MA_NOACTIVATE;

                case WM_NCHITTEST:
                {
                    handled = true;
                    int sx = unchecked((short)(long)lParam);
                    int sy = unchecked((short)((long)lParam >> 16));
                    if (GetWindowRect(hwnd, out RECT r))
                    {
                        // Grab rect = the handle's on-card position (relative inset + size) + a few px of
                        // tolerance, so what you click matches the visible handle at any card scale.
                        double s = _dpiScale, tol = 5 * s;
                        double hx1 = r.right - _hitRightDip * s + tol;
                        double hx0 = hx1 - _handleDip * s - 2 * tol;
                        double hy0 = r.top + _hitTopDip * s - tol;
                        double hy1 = hy0 + _handleDip * s + 2 * tol;
                        if (sx >= hx0 && sx <= hx1 && sy >= hy0 && sy <= hy1)
                            return (IntPtr)HTTOPRIGHT;   // corner → system resize (constrained in WM_SIZING)
                    }
                    return (IntPtr)HTCAPTION;            // body → system move
                }

                case WM_NCRBUTTONUP:
                    handled = true;
                    if (_closable) CloseCard();   // HUD cards swallow it (persistent, no per-card dismiss)
                    return IntPtr.Zero;

                case WM_NCMOUSEMOVE:
                    _handle.Visibility = Visibility.Visible;
                    TrackLeave(hwnd);
                    break;

                case WM_NCMOUSELEAVE:
                    _handle.Visibility = Visibility.Collapsed;
                    break;

                case WM_SIZING:
                    ConstrainSizing(lParam);
                    handled = true;
                    return (IntPtr)1;   // TRUE — we adjusted the rect

                case WM_EXITSIZEMOVE:
                    _owner.GeometryChanged(this);   // a move OR resize gesture just ended → persist
                    break;
            }
            return IntPtr.Zero;
        }

        // Proportional resize anchored at the bottom-left corner (WMSZ_TOPRIGHT keeps left+bottom fixed).
        // All math in device pixels (WM_SIZING's RECT is screen px); clamp width to [100 DIP, 1.5x native].
        private void ConstrainSizing(IntPtr lParam)
        {
            var r = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
            double minPx = MinWidthDip * _dpiScale;
            double maxPx = _nativeW * MaxScale;
            if (maxPx < minPx) maxPx = minPx;
            double padPx = Pad * _dpiScale;

            // The proposed rect includes the grab ring; clamp the ART width, then add the ring back.
            double w = Clamp((r.right - r.left) - 2 * padPx, minPx, maxPx);
            double h = w * _aspect;
            r.right = r.left + (int)Math.Round(w + 2 * padPx);
            r.top = r.bottom - (int)Math.Round(h + 2 * padPx);
            Marshal.StructureToPtr(r, lParam, false);
        }

        private void TrackLeave(IntPtr hwnd)
        {
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)),
                dwFlags = TME_LEAVE | TME_NONCLIENT,
                hwndTrack = hwnd,
                dwHoverTime = 0
            };
            try { TrackMouseEvent(ref tme); } catch { }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // OS cursor in this window's device-independent units (handles the window's monitor DPI).
        private Point CursorDip()
        {
            GetCursorPos(out POINT p);
            if (_src?.CompositionTarget != null)
                return _src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));
            return new Point(p.X, p.Y);
        }

        // ── Constants / interop ──────────────────────────────────────────────────────────────────

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCRBUTTONUP = 0x00A5;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const int WM_SIZING = 0x0214;
        private const int WM_EXITSIZEMOVE = 0x0232;

        private const int MA_NOACTIVATE = 3;
        private const int HTCAPTION = 2;
        private const int HTTOPRIGHT = 14;

        private const uint TME_LEAVE = 0x00000002;
        private const uint TME_NONCLIENT = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
