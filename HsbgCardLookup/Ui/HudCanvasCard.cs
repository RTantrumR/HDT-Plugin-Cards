using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// One HUD card (a trinket or the anomaly) drawn INSIDE HDT's overlay canvas — not a window.
    /// The root is registered via <c>OverlayExtensions.SetIsOverlayHitTestVisible</c>, so HDT drops
    /// <c>WS_EX_TRANSPARENT</c> while the cursor is on the card: left-drag moves it, dragging the
    /// top-right grabber resizes it (proportional, anchored bottom-left), right-click opens the HUD
    /// menu — all while the overlay window stays <c>WS_EX_NOACTIVATE</c>, so Hearthstone never loses
    /// foreground. HDT also handles window tracking / foreground gating / DPI for free.
    ///
    /// Placement is stored as CANVAS FRACTIONS (x/y of the top-left, width — all relative to the
    /// canvas size), so the layout survives resolution changes and scales with the game window.
    /// A drag/resize gesture is driven by a WH_MOUSE_LL hook installed only for the gesture:
    /// WPF mouse-capture can't be trusted here — the instant the cursor outruns the card, HDT's 60 Hz
    /// loop re-enables click-through and the window stops receiving input (it's also how HDT itself
    /// implements its "unlock overlay" dragging). The hook never swallows events.
    /// </summary>
    public sealed class HudCanvasCard
    {
        private const double MinWidth = 100;      // canvas units; floor so it can't shrink into a speck
        private const double MaxScale = 1.5;      // ceiling vs native art px so it stays crisp
        private const double DefaultWidth = 170;  // starting width when nothing is saved
        private const double DefAspect = 1.4;     // card aspect before real art is known

        // Resize grabber: proportional to the card, inset onto the art's (transparent-cornered)
        // top-right so it stays on the visible art at any scale — same tuning as FloatingCard.
        private const double HandleFrac = 0.16, HandleMin = 16, HandleMax = 44;
        private const double InsetFracX = 0.05, InsetFracY = 0.07;

        private readonly int _index;         // trinket slot index (drives the default stack position)
        private readonly bool _isAnomaly;
        private readonly Canvas _host;       // null = HDT's overlay canvas
        private readonly Grid _root;
        private readonly Image _img;
        private readonly Border _handle;
        private readonly Rectangle _editOutline;
        private readonly Border _editLabel;
        private readonly TextBlock _editLabelText;
        private readonly DispatcherTimer _handleHide;   // collapses the grabber shortly after the cursor leaves

        /// <summary>Right-click on the card (opens the HUD context menu). Canvas thread.</summary>
        public Action RightClicked;
        /// <summary>A move/resize gesture ended — receives the new placement fractions (xf, yf, wf).</summary>
        public Action<double, double, double> GeometryChanged;

        private bool _attached;
        private bool _editing;
        private int _nativeW = 256;
        private double _aspect = DefAspect;
        private bool _hasPos;
        private double _xf, _yf, _wf;        // canvas-fraction placement (top-left + art width)

        // Gesture state (all touched on the canvas thread only — the LL hook fires there too).
        private bool _dragging, _resizing, _moved;
        private Point _startCursor;          // canvas units at gesture start
        private double _startLeft, _startTop, _startW;

        /// <summary>The canvas this card lives in: HDT's game overlay by default, or a caller's own
        /// canvas for an off-game preview. Held per instance rather than swapped globally, so a preview
        /// can never capture a live in-match card's attach (and vice versa).</summary>
        private Canvas Host => _host ?? Core.OverlayCanvas;

        /// <summary>The card's current on-canvas size, in canvas units — what it will really measure in
        /// game. NaN until the first layout pass.</summary>
        public double LayoutWidth => _root.Width;
        public double LayoutHeight => _root.Height;

        public HudCanvasCard(int index, bool isAnomaly) : this(index, isAnomaly, null) { }

        /// <param name="host">Render into this canvas instead of HDT's overlay. A hosted card is a
        /// passive preview: no overlay hit-testing, no drag/resize gestures (so it can never install
        /// the global mouse hook) and no right-click menu.</param>
        public HudCanvasCard(int index, bool isAnomaly, Canvas host)
        {
            _index = index;
            _isAnomaly = isAnomaly;
            _host = host;

            _img = new Image { Stretch = Stretch.Fill };   // the root box holds the native aspect
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);

            var arrow = new Path
            {
                Data = Geometry.Parse("M3 9 L9 3 M9 3 L9 6 M9 3 L6 3"),  // ↗ out of the corner
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4)
            };
            _handle = new Border
            {
                Width = HandleMin, Height = HandleMin,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.SizeNESW,
                Child = arrow
            };

            _editOutline = new Rectangle
            {
                Stroke = UiKit.AccentBrush,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent,
                RadiusX = 6, RadiusY = 6,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _editLabelText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                FontSize = 12, FontWeight = FontWeights.SemiBold
            };
            _editLabel = new Border
            {
                Background = UiKit.AccentBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = _editLabelText
            };

            _root = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
            _root.Children.Add(_img);
            _root.Children.Add(_editOutline);
            _root.Children.Add(_editLabel);
            _root.Children.Add(_handle);

            _handleHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _handleHide.Tick += (s, e) =>
            {
                _handleHide.Stop();
                if (!_editing && !_resizing) _handle.Visibility = Visibility.Collapsed;
            };

            if (_host == null)
            {
                _handle.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: true, e); };
                _root.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: false, e); };
                _root.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (!_dragging && !_resizing) { try { RightClicked?.Invoke(); } catch { } }
                };
                _root.MouseMove += (s, e) => PokeHandle();

                // Registers with HDT's hover loop so the overlay window becomes clickable over this
                // card. Meaningless for a preview, which is not in HDT's overlay at all.
                try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
            }
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        // ── Attach / show / hide (canvas thread) ─────────────────────────────────────────────────

        private bool Attach()
        {
            if (_attached) return true;
            var canvas = Host;
            if (canvas == null) return false;
            canvas.Children.Add(_root);
            canvas.SizeChanged += OnCanvasSizeChanged;
            _attached = true;
            return true;
        }

        /// <summary>Show at the saved placement (wf &gt; 0), else at the slot's default spot.</summary>
        public void ShowAt(double xf, double yf, double wf)
        {
            if (!Attach()) return;
            if (wf > 0) { _xf = xf; _yf = yf; _wf = wf; _hasPos = true; }
            _root.Visibility = Visibility.Visible;
            Layout();
        }

        public void Hide()
        {
            EndGesture(persist: false);
            _root.Visibility = Visibility.Collapsed;
        }

        /// <summary>Remove from the canvas entirely (plugin unload).</summary>
        public void Close()
        {
            EndGesture(persist: false);
            try
            {
                var canvas = Host;
                if (canvas != null)
                {
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                }
            }
            catch { /* HDT may already be tearing down */ }
            _attached = false;
        }

        public void SetArt(BitmapSource art)
        {
            if (art == null || art.PixelWidth <= 0) return;
            _img.Source = art;
            _nativeW = art.PixelWidth;
            _aspect = (double)art.PixelHeight / art.PixelWidth;
            Layout();
        }

        public void SetEditChrome(string label)
        {
            _editing = true;
            _editLabelText.Text = label ?? "";
            _editOutline.Visibility = Visibility.Visible;
            _editLabel.Visibility = Visibility.Visible;
            _handle.Visibility = Visibility.Visible;   // pinned while arranging
        }

        public void ClearEditChrome()
        {
            _editing = false;
            _editOutline.Visibility = Visibility.Collapsed;
            _editLabel.Visibility = Visibility.Collapsed;
            _handle.Visibility = Visibility.Collapsed;
        }

        // ── Layout from fractions ────────────────────────────────────────────────────────────────

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_root.Visibility == Visibility.Visible) Layout();
        }

        private void Layout()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            if (!_hasPos)
            {
                DefaultFrac(cw, ch, out _xf, out _yf, out _wf);
                _hasPos = true;
            }

            double w = ClampWidth(_wf * cw, ch);
            double h = w * _aspect;
            double left = Clamp(_xf * cw, 0, Math.Max(0, cw - w));
            double top = Clamp(_yf * ch, 0, Math.Max(0, ch - h));

            _root.Width = w;
            _root.Height = h;
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);
            UpdateHandle(w, h);
        }

        private double ClampWidth(double w, double canvasH)
        {
            double max = Math.Max(MinWidth, _nativeW * MaxScale);
            w = Clamp(w, MinWidth, max);
            if (w * _aspect > canvasH && canvasH > 0) w = canvasH / _aspect;   // never taller than the canvas
            return w;
        }

        // Default layout mirrors the old window HUD: trinkets stack down the RIGHT edge of the game
        // area (wrapping to a new column when they'd run past the bottom), anomaly top-left.
        private void DefaultFrac(double cw, double ch, out double xf, out double yf, out double wf)
        {
            double w = Math.Min(DefaultWidth, cw * 0.2);
            wf = w / cw;
            if (_isAnomaly) { xf = 24 / cw; yf = 24 / ch; return; }

            double h = w * DefAspect;
            double step = h + 14;
            int perCol = Math.Max(1, (int)((ch - 48) / step));
            int col = _index / perCol;
            int row = _index % perCol;
            xf = (cw - w - 24 - col * (w + 14)) / cw;
            yf = (24 + row * step) / ch;
        }

        private void UpdateHandle(double w, double h)
        {
            double size = Clamp(w * HandleFrac, HandleMin, HandleMax);
            _handle.Width = size;
            _handle.Height = size;
            _handle.Margin = new Thickness(0, h * InsetFracY, w * InsetFracX, 0);
        }

        private void PokeHandle()
        {
            _handle.Visibility = Visibility.Visible;
            _handleHide.Stop();
            _handleHide.Start();
        }

        // ── Move / resize gestures (LL mouse hook, installed only for the gesture) ──────────────

        private void BeginGesture(bool resize, MouseButtonEventArgs e)
        {
            if (_host != null) return;   // preview: never install the global mouse hook
            if (!_attached || _dragging || _resizing) return;
            var canvas = Host;
            if (canvas == null) return;
            try { _startCursor = e.GetPosition(canvas); } catch { return; }
            _startLeft = Canvas.GetLeft(_root);
            _startTop = Canvas.GetTop(_root);
            _startW = _root.Width;
            if (double.IsNaN(_startLeft) || double.IsNaN(_startTop) || double.IsNaN(_startW)) return;
            _moved = false;
            _dragging = !resize;
            _resizing = resize;
            InstallHook();
        }

        private void OnGestureMove()
        {
            var canvas = Host;
            if (canvas == null) { EndGesture(persist: false); return; }
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            Point cur;
            try
            {
                GetCursorPos(out POINT p);
                cur = canvas.PointFromScreen(new Point(p.X, p.Y));
            }
            catch { return; }
            double dx = cur.X - _startCursor.X, dy = cur.Y - _startCursor.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 1) _moved = true;

            if (_dragging)
            {
                double w = _root.Width, h = _root.Height;
                Canvas.SetLeft(_root, Clamp(_startLeft + dx, 0, Math.Max(0, cw - w)));
                Canvas.SetTop(_root, Clamp(_startTop + dy, 0, Math.Max(0, ch - h)));
            }
            else if (_resizing)
            {
                // Proportional, anchored bottom-left: left + bottom edges stay put.
                double bottom = _startTop + _startW * _aspect;
                double w = ClampWidth(_startW + dx, ch);
                if (w > cw - _startLeft) w = cw - _startLeft;
                double h = w * _aspect;
                double top = bottom - h;
                if (top < 0) { top = 0; h = bottom; w = h / _aspect; }
                _root.Width = w;
                _root.Height = w * _aspect;
                Canvas.SetTop(_root, top);
                UpdateHandle(w, w * _aspect);
            }
        }

        private void EndGesture(bool persist = true)
        {
            if (!_dragging && !_resizing) return;
            _dragging = _resizing = false;
            RemoveHook();
            if (!persist || !_moved) return;

            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            _xf = Canvas.GetLeft(_root) / cw;
            _yf = Canvas.GetTop(_root) / ch;
            _wf = _root.Width / cw;
            try { GeometryChanged?.Invoke(_xf, _yf, _wf); } catch { }
        }

        // ── Legacy placement conversion (old screen-DIP X/Y/W → canvas fractions) ───────────────

        /// <summary>Convert a pre-canvas placement (window top-left + art width, screen DIPs) into
        /// canvas fractions, using the live canvas. False when the canvas isn't ready.</summary>
        public static bool LegacyToFrac(double xDip, double yDip, double wDip,
            out double xf, out double yf, out double wf)
        {
            xf = yf = wf = 0;
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas == null) return false;
                double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
                if (cw <= 0 || ch <= 0) return false;
                var ct = PresentationSource.FromVisual(canvas)?.CompositionTarget;
                if (ct == null) return false;
                double m = ct.TransformToDevice.M11;               // device px per DIP
                var pt = canvas.PointFromScreen(new Point(xDip * m, yDip * m));
                xf = Clamp(pt.X / cw, 0, 0.97);
                yf = Clamp(pt.Y / ch, 0, 0.97);
                wf = Clamp(wDip / cw, MinWidth / cw, 0.9);
                return true;
            }
            catch { return false; }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // ── Gesture hook plumbing (canvas thread; never swallows) ────────────────────────────────

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelMouseProc _proc;   // keep the delegate alive while hooked

        private void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _proc = HookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            }
            catch { _hook = IntPtr.Zero; _proc = null; _dragging = _resizing = false; }
        }

        private void RemoveHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _proc = null;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                if (wParam == (IntPtr)WM_MOUSEMOVE) { try { OnGestureMove(); } catch { } }
                else if (wParam == (IntPtr)WM_LBUTTONUP) { try { EndGesture(); } catch { } }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
