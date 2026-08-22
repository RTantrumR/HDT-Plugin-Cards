using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// The separate opponents'-MMR standings panel: one draggable/resizable list on HDT's overlay
    /// canvas (the alternative — or complement — to the on-portrait labels; both are content-gated by
    /// the same per-part toggles: names / rating / deltas / tiers / ⚔ / dead-dimming). Interactivity
    /// works exactly like <see cref="HudCanvasCard"/>: the root is registered hit-test-visible with
    /// HDT, a WH_MOUSE_LL hook drives the drag/resize gesture (WPF capture can't be trusted — HDT
    /// re-enables click-through the instant the cursor outruns the element), and Hearthstone never
    /// loses foreground. Placement persists as canvas fractions (config `MmrPanelHud`); resizing
    /// drags the top-right grabber and scales the whole panel (fonts included) via its width.
    /// </summary>
    public sealed class MmrSidePanel
    {
        private const int MaxSlots = 8;
        private const double RefW = 210;          // panel width at scale 1 (fonts are tuned to this)
        private const double MinW = 140, MaxW = 480;
        private const double DefaultXF = 0.005, DefaultYF = 0.30;

        /// <summary>Content switches — same meaning as on <see cref="LeaderboardOverlay"/>.</summary>
        public bool ShowNames { get; set; }
        public bool ShowRating { get; set; } = true;
        public bool ShowDeltas { get; set; } = true;
        public bool ShowTiers { get; set; } = true;
        public bool ShowLastOpp { get; set; } = true;
        public bool DimDead { get; set; } = true;
        /// <summary>Duos: rows arrive team-ordered (pairs 0+1, 2+3, …) — a wider gap separates teams.</summary>
        public bool IsDuos { get; set; }

        /// <summary>A move/resize gesture ended — receives the new placement fractions (xf, yf, wf).</summary>
        public Action<double, double, double> GeometryChanged;

        private readonly Border _root;
        private readonly StackPanel _list;
        private readonly Grid[] _rows = new Grid[MaxSlots];
        private readonly TextBlock[] _swords = new TextBlock[MaxSlots];
        private readonly TextBlock[] _names = new TextBlock[MaxSlots];
        private readonly TextBlock[] _ratings = new TextBlock[MaxSlots];
        private readonly TextBlock[] _arrows = new TextBlock[MaxSlots];
        private readonly Image[] _tiers = new Image[MaxSlots];
        private readonly Border _handle;
        private readonly Rectangle _editOutline;
        private readonly Border _editLabel;
        private readonly DispatcherTimer _handleHide;

        private bool _attached;
        private bool _editing;
        private bool _hasPos;
        private double _xf = DefaultXF, _yf = DefaultYF, _wf;

        private bool _dragging, _resizing, _moved;
        private Point _startCursor;
        private double _startLeft, _startTop, _startW;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xDC, 0x0A, 0x0D, 0x14));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB4));
        private static readonly Brush Dead = Frozen(Color.FromRgb(0x91, 0x91, 0x91));
        private static readonly Brush Up = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Down = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly Brush Swords = Frozen(Color.FromRgb(0xF5, 0xC4, 0x51));

        public MmrSidePanel()
        {
            _list = new StackPanel();
            for (int i = 0; i < MaxSlots; i++)
            {
                var g = new Grid { Visibility = Visibility.Collapsed };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ⚔
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // rating
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ▲/▼
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // tier

                var sw = new TextBlock { Text = "⚔", Foreground = Swords, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(sw, 0); g.Children.Add(sw);
                var name = new TextBlock
                {
                    Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0)
                };
                Grid.SetColumn(name, 1); g.Children.Add(name);
                var rating = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(rating, 2); g.Children.Add(rating);
                var arrow = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
                Grid.SetColumn(arrow, 3); g.Children.Add(arrow);
                var tier = new Image { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
                RenderOptions.SetBitmapScalingMode(tier, BitmapScalingMode.HighQuality);
                Grid.SetColumn(tier, 4); g.Children.Add(tier);

                _swords[i] = sw; _names[i] = name; _ratings[i] = rating; _arrows[i] = arrow; _tiers[i] = tier;
                _rows[i] = g;
                _list.Children.Add(g);
            }

            var arrowPath = new Path
            {
                Data = Geometry.Parse("M3 9 L9 3 M9 3 L9 6 M9 3 L6 3"),
                Stroke = Brushes.White, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Margin = new Thickness(4)
            };
            _handle = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                BorderBrush = UiKit.AccentBrush, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.SizeNESW,
                Child = arrowPath
            };
            _editOutline = new Rectangle
            {
                Stroke = UiKit.AccentBrush, StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent, RadiusX = 6, RadiusY = 6,
                IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            var editLabelText = new TextBlock
            {
                Text = "Opponents' MMR",
                Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                FontSize = 12, FontWeight = FontWeights.SemiBold
            };
            _editLabel = new Border
            {
                Background = UiKit.AccentBrush, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                Child = editLabelText
            };

            var grid = new Grid();
            grid.Children.Add(_list);
            grid.Children.Add(_editOutline);
            grid.Children.Add(_editLabel);
            grid.Children.Add(_handle);

            _root = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.75 },
                Child = grid
            };

            _handleHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _handleHide.Tick += (s, e) =>
            {
                _handleHide.Stop();
                if (!_editing && !_resizing) _handle.Visibility = Visibility.Collapsed;
            };

            _handle.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: true, e); };
            _root.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: false, e); };
            _root.MouseMove += (s, e) => { _handle.Visibility = Visibility.Visible; _handleHide.Stop(); _handleHide.Start(); };

            try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        // ── Attach / show / hide (canvas thread) ─────────────────────────────────────────────────

        private bool Attach()
        {
            if (_attached) return true;
            var canvas = Core.OverlayCanvas;
            if (canvas == null) return false;
            canvas.Children.Add(_root);
            canvas.SizeChanged += OnCanvasSizeChanged;
            _attached = true;
            return true;
        }

        /// <summary>Restore the saved placement (call before the first show; wf &lt;= 0 = defaults).</summary>
        public void Place(double xf, double yf, double wf)
        {
            if (wf > 0) { _xf = xf; _yf = yf; _wf = wf; _hasPos = true; }
        }

        public void Hide()
        {
            EndGesture(persist: false);
            _root.Visibility = Visibility.Collapsed;
        }

        public void Close()
        {
            EndGesture(persist: false);
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas != null)
                {
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                }
            }
            catch { /* HDT may already be tearing down */ }
            _attached = false;
        }

        /// <summary>Fill + show the panel (row 0 = 1st place). Canvas thread. Empty/null — or every
        /// content part switched off — hides it.</summary>
        public void SetStandings(IReadOnlyList<LeaderboardOverlay.Row> rows)
        {
            bool anyContent = ShowNames || ShowRating || ShowDeltas || ShowTiers || ShowLastOpp;
            int n = rows?.Count ?? 0;
            if (n == 0 || !anyContent || !Attach()) { Hide(); return; }

            for (int i = 0; i < MaxSlots; i++)
            {
                if (i >= n) { _rows[i].Visibility = Visibility.Collapsed; continue; }
                var r = rows[i];
                bool dim = DimDead && r.IsDead;

                _rows[i].Visibility = Visibility.Visible;
                _rows[i].Opacity = dim ? 0.6 : 1.0;

                _swords[i].Visibility = ShowLastOpp ? Visibility.Visible : Visibility.Collapsed;
                _swords[i].Opacity = r.IsLastOpponent ? 1.0 : 0.0;   // keeps the column aligned

                _names[i].Text = r.Name;
                _names[i].Foreground = dim ? Dead : Brushes.White;
                _names[i].Visibility = ShowNames ? Visibility.Visible : Visibility.Collapsed;

                _ratings[i].Text = r.RatingPending ? "…" : (r.Rating > 0 ? r.Rating.ToString() : "8000↓");
                _ratings[i].Foreground = dim ? Dead : (!r.RatingPending && r.Rating > 0 ? UiKit.AccentBrush : Muted);
                _ratings[i].Visibility = ShowRating ? Visibility.Visible : Visibility.Collapsed;

                if (ShowDeltas && r.Delta != 0 && !dim)
                {
                    _arrows[i].Text = (r.Delta > 0 ? "▲" : "▼") + Math.Abs(r.Delta);
                    _arrows[i].Foreground = r.Delta > 0 ? Up : Down;
                    _arrows[i].Visibility = Visibility.Visible;
                }
                else _arrows[i].Visibility = Visibility.Collapsed;

                var icon = ShowTiers && r.TavernTier >= 1 && r.TavernTier <= 7 ? TierIcon(r.TavernTier) : null;
                _tiers[i].Source = icon;
                _tiers[i].Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            }

            _root.Visibility = Visibility.Visible;
            Layout();
        }

        /// <summary>Arrange mode: show the panel with sample standings + edit chrome, so it can be
        /// placed out of a match. Exiting hides it (the next poll restores live data if any).</summary>
        public void SetEditMode(bool on)
        {
            _editing = on;
            if (on)
            {
                SetStandings(SampleRows());
                if (_root.Visibility != Visibility.Visible) return;   // canvas not ready
                _editOutline.Visibility = Visibility.Visible;
                _editLabel.Visibility = Visibility.Visible;
                _handle.Visibility = Visibility.Visible;
            }
            else
            {
                _editOutline.Visibility = Visibility.Collapsed;
                _editLabel.Visibility = Visibility.Collapsed;
                _handle.Visibility = Visibility.Collapsed;
                Hide();
            }
        }

        private static List<LeaderboardOverlay.Row> SampleRows()
        {
            var outp = new List<LeaderboardOverlay.Row>();
            string[] names = { "Sevel", "DoGBiscuit", "Saphirel", "Maks7k", "Beterbabbit", "Pockyplays", "XiaoT", "Fasteddyhaha" };
            int[] ratings = { 14872, 13561, 12208, 11440, 10653, 9781, 8944, 0 };
            for (int i = 0; i < names.Length; i++)
                outp.Add(new LeaderboardOverlay.Row
                {
                    Name = names[i],
                    Rating = ratings[i],
                    Delta = i == 1 ? 213 : i == 4 ? -96 : 0,
                    TavernTier = 1 + (i * 5) % 7,
                    IsDead = i == 7,
                    IsLastOpponent = i == 2
                });
            return outp;
        }

        // ── Layout (fractions → canvas units; fonts scale with width) ────────────────────────────

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_root.Visibility == Visibility.Visible) Layout();
        }

        private void Layout()
        {
            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            if (_wf <= 0) _wf = RefW / cw;
            double w = Clamp(_wf * cw, MinW, MaxW);
            double s = w / RefW;

            _root.Width = w;
            _root.Padding = new Thickness(7 * s, 4 * s, 7 * s, 4 * s);
            for (int i = 0; i < MaxSlots; i++)
            {
                // Duos: a wider gap above rows 2/4/6 separates the team pairs.
                double topGap = IsDuos && i > 0 && i % 2 == 0 ? 7.0 : 1.5;
                _rows[i].Margin = new Thickness(0, topGap * s, 0, 1.5 * s);
                _swords[i].FontSize = 11.5 * s;
                _swords[i].Margin = new Thickness(0, 0, 3 * s, 0);
                _names[i].FontSize = 12.5 * s;
                _ratings[i].FontSize = 12.5 * s;
                _arrows[i].FontSize = 10.5 * s;
                _tiers[i].Height = 20 * s;
            }

            double left = Clamp(_xf * cw, 0, Math.Max(0, cw - w));
            double top = Clamp(_yf * ch, 0, Math.Max(0, ch * 0.97));
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);
            if (!_hasPos) _hasPos = true;
        }

        // ── Move / resize gestures (LL mouse hook, installed only for the gesture) ───────────────

        private void BeginGesture(bool resize, MouseButtonEventArgs e)
        {
            if (!_attached || _dragging || _resizing) return;
            var canvas = Core.OverlayCanvas;
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
            var canvas = Core.OverlayCanvas;
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
                Canvas.SetLeft(_root, Clamp(_startLeft + dx, 0, Math.Max(0, cw - _root.Width)));
                Canvas.SetTop(_root, Clamp(_startTop + dy, 0, Math.Max(0, ch - _root.ActualHeight)));
            }
            else if (_resizing)
            {
                // Width-only resize anchored at the top-left; fonts rescale via Layout.
                _wf = Clamp(_startW + dx, MinW, MaxW) / cw;
                Layout();
            }
        }

        private void EndGesture(bool persist = true)
        {
            if (!_dragging && !_resizing) return;
            _dragging = _resizing = false;
            RemoveHook();
            if (!persist || !_moved) return;

            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            _xf = Canvas.GetLeft(_root) / cw;
            _yf = Canvas.GetTop(_root) / ch;
            _wf = _root.Width / cw;
            try { GeometryChanged?.Invoke(_xf, _yf, _wf); } catch { }
        }

        private static ImageSource TierIcon(int tier)
        {
            try { return ImageCache.Load(CardStore.TierIconPath(tier), 64); }
            catch { return null; }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // ── Gesture hook plumbing (canvas thread; never swallows) ────────────────────────────────

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelMouseProc _proc;

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
