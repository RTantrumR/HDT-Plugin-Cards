using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// The Dark Gift list panel — summoned while the player hovers the in-game Dark Discovery button
    /// (see <see cref="Game.DarkGiftWatcher"/>). It is NOT a window: the whole panel
    /// lives inside HDT's own overlay canvas (<c>Core.OverlayCanvas</c>), registered with
    /// <c>OverlayExtensions.SetIsOverlayHitTestVisible</c> so HDT drops <c>WS_EX_TRANSPARENT</c> while
    /// the cursor is on it — real wheel/right-click/drag reach us while the overlay window stays
    /// <c>WS_EX_NOACTIVATE</c>, so Hearthstone never loses foreground.
    ///
    /// Placement: left-drag moves the panel and the top-right grabber scales it, and once either has
    /// happened the saved spot wins every summon. Until then the original cursor-anchored rule stands —
    /// the panel lands well left of the hovered button, so the button stays clickable and its tooltip
    /// readable — which is what a player who never touches it keeps getting.
    ///
    /// Rows per the design sketch: a rounded container per gift — name | separator line | effect text —
    /// stacked vertically, each sized to its text. Gifts offerable THIS turn glow (accent border + soft
    /// shadow, bold name); future gifts are dimmed; expired gifts aren't passed in at all. Emphasis
    /// levels recolor a current row: 1 = relevant to the guaranteed most-common-type offer (green),
    /// 2 = unique enabler pairing (purple, reserved for the minion-pool feature).
    ///
    /// While visible, a low-level mouse hook forwards the wheel to the list even when the cursor is on
    /// the game (hovering the button) — so the user can scroll the panel without mousing onto it. The
    /// hook is installed ONLY while visible, never swallows events, and defers to normal WPF wheel
    /// handling when the cursor is over the panel itself.
    /// </summary>
    public sealed class DarkGiftPanel
    {
        private const double ContentWidth = 450;   // gift-list column width (panel width = this + art column)
        // Scale: _wf is a NOMINAL width fraction of the canvas (the same idea as MmrSidePanel), turned
        // into one uniform LayoutTransform. RefW is the design width, so an unarranged panel comes out
        // at exactly 1.0 whatever the resolution — identical to how it has always rendered.
        private const double RefW = ContentWidth + 22;
        private const double MinNomW = 300, MaxNomW = 940;   // scale ≈ 0.64 … 2.0
        // Minion-art grid (left column): full card renders auto-fit into ≤4 columns — 200px wide for a
        // small pool, shrinking (never below ~110) as the pool grows so everything stays visible with
        // no scrolling. Card aspect ≈ the production full renders (404×558).
        private const double ArtMaxW = 200, ArtMinW = 110, ArtGap = 6, ArtMaxH = 560, ArtAspect = 558.0 / 404.0;

        private readonly Border _root;             // the canvas child holding everything
        private readonly TextBlock _headerSub;
        private readonly StackPanel _rows;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _giftColumn;   // header + gift list (collapsed in minions-only mode)
        private readonly StackPanel _artColumn;
        private readonly TextBlock _artCaption;
        private readonly WrapPanel _artWrap;
        private readonly TextBlock _artMore;
        private readonly Canvas _host;             // null = HDT's overlay canvas
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly Border _handle;           // top-right resize grabber
        private readonly Rectangle _editOutline;
        private readonly Border _editLabel;
        private readonly DispatcherTimer _handleHide;
        private bool _attached;

        private bool _hasPos;                      // the panel has been arranged at least once
        private bool _autoCentre;                  // no saved spot and no cursor to anchor to (arrange)
        private double _xf, _yf, _wf;              // saved placement (canvas fractions)
        private double _nominalW = RefW;           // what Layout turns into the scale factor
        private bool _editing;
        private bool _dragging, _resizing, _moved;
        private Point _startCursor;
        private double _startLeft, _startTop, _startW;

        /// <summary>The canvas this panel lives in: HDT's game overlay by default, or a caller's own
        /// canvas for an off-game preview. Held per instance rather than swapped globally, so a preview
        /// can never capture the live in-match panel's attach (and vice versa).</summary>
        private Canvas Host => _host ?? Core.OverlayCanvas;

        /// <summary>A move/resize gesture ended — receives the new placement fractions (xf, yf, wf).</summary>
        public Action<double, double, double> GeometryChanged;

        /// <summary>True while the panel is being dragged or resized. The watcher keeps it up for the
        /// duration: the hover signal that summoned it is long gone by then, and having the panel
        /// vanish out from under a drag is the one thing that would make it unpositionable.</summary>
        public bool IsGesturing => _dragging || _resizing;

        /// <summary>The rendered size, i.e. after the scale transform — what the panel really occupies
        /// on the canvas, and so what every clamp has to be measured against.
        ///
        /// Width comes from <c>_root.Width</c>, not ActualWidth, and that matters: SetContent assigns
        /// the new width and the cursor-anchored placement runs in the SAME pass, before WPF has
        /// measured — ActualWidth would still be the previous content's width and the panel would land
        /// offset by the difference. Height has no explicit value, so it can only be read back after a
        /// layout pass (hence the deferred correction in PlaceForSummon).</summary>
        private double RenderedW =>
            (double.IsNaN(_root.Width) ? (_root.ActualWidth > 0 ? _root.ActualWidth : _nominalW) : _root.Width) * _scale.ScaleX;
        private double RenderedH => _root.ActualHeight * _scale.ScaleY;

        // The panel's on-screen box in DEVICE pixels, refreshed after every placement/layout. Read from
        // the OnUpdate thread (IsUnderMouse) and the mouse hook, so it's swapped as one immutable object
        // instead of maintained by MouseEnter/MouseLeave — those can't be trusted here, since HDT makes
        // the overlay click-through again the moment the cursor leaves us (the leave event may never
        // arrive, which would strand the panel visible forever).
        private sealed class Box { public double L, T, R, B; }
        private volatile Box _box;

        /// <summary>True while the cursor is over the panel (read cross-thread by the watcher so the
        /// panel survives the mouse travelling from the game button onto it).</summary>
        public bool IsUnderMouse
        {
            get
            {
                var b = _box;
                if (b == null) return false;
                try
                {
                    GetCursorPos(out POINT p);
                    return p.X >= b.L && p.X <= b.R && p.Y >= b.T && p.Y <= b.B;
                }
                catch { return false; }
            }
        }

        /// <summary>Raised on a right-click anywhere on the panel — the watcher cycles the display
        /// mode (gifts+minions / gifts only / minions only). Fired on the UI thread.</summary>
        public event Action ModeCycleRequested;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xEE, 0x10, 0x14, 0x1C));
        private static readonly Brush RowBg = Frozen(Color.FromArgb(0xFF, 0x1A, 0x21, 0x30));
        private static readonly Brush RowBgDim = Frozen(Color.FromArgb(0xFF, 0x14, 0x19, 0x24));
        private static readonly Brush StatBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));    // +X/+Y buffs
        private static readonly Brush KeywordBrush = Frozen(Color.FromRgb(0xE8, 0xB5, 0x4B)); // keywords (accent gold)
        private static readonly Color TribeColor = Color.FromRgb(0x4A, 0xDE, 0x80);   // Emph 1 (green)
        private static readonly Color UniqueColor = Color.FromRgb(0xC0, 0x84, 0xFC);  // Emph 2 (purple)
        private static readonly Brush TribeBrush = Frozen(TribeColor);
        private static readonly Brush UniqueBrush = Frozen(UniqueColor);

        public DarkGiftPanel() : this(null) { }

        /// <param name="host">Render into this canvas instead of HDT's overlay. A hosted panel is a
        /// passive preview: no overlay hit-testing, no drag/resize gestures (so it can never install
        /// the global mouse hook) and no geometry written back to config.</param>
        public DarkGiftPanel(Canvas host)
        {
            _host = host;
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 7) };
            var title = new TextBlock
            {
                Text = "Dark Gifts",
                Foreground = UiKit.AccentBrush,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 10, 0)
            };
            _headerSub = new TextBlock
            {
                Foreground = UiKit.TextMuted,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextWrapping = TextWrapping.Wrap
            };
            header.Children.Add(title);
            header.Children.Add(_headerSub);

            _rows = new StackPanel();
            _scroll = new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 500
            };

            _giftColumn = new StackPanel { Width = ContentWidth };
            _giftColumn.Children.Add(header);
            _giftColumn.Children.Add(_scroll);

            // Left column: the guaranteed-tribe pool as actual card renders (collapsed when absent).
            _artCaption = new TextBlock
            {
                Foreground = UiKit.TextSecondary,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 6),
                TextWrapping = TextWrapping.Wrap
            };
            _artWrap = new WrapPanel();
            _artMore = new TextBlock
            {
                Foreground = UiKit.TextMuted,
                FontSize = 12,
                Margin = new Thickness(2, 1, 2, 0)
            };
            _artColumn = new StackPanel { Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
            _artColumn.Children.Add(_artCaption);
            _artColumn.Children.Add(_artWrap);
            _artColumn.Children.Add(_artMore);

            var outer = new StackPanel { Orientation = Orientation.Horizontal };
            outer.Children.Add(_artColumn);
            outer.Children.Add(_giftColumn);

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
            _editLabel = new Border
            {
                Background = UiKit.AccentBrush, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "Dark Gifts",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                    FontSize = 12, FontWeight = FontWeights.SemiBold
                }
            };

            var stack = new Grid();
            stack.Children.Add(outer);
            stack.Children.Add(_editOutline);
            stack.Children.Add(_editLabel);
            stack.Children.Add(_handle);

            _root = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 9),
                Width = ContentWidth + 22,          // content + padding/border; SetContent adjusts
                Visibility = Visibility.Collapsed,
                LayoutTransform = _scale,
                Child = stack
            };
            try { _root.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = UiKit.ThinScrollBarStyle(); } catch { }

            // The panel's width changes with content (a mode switch collapses a whole column), so a
            // saved placement has to be re-clamped whenever that happens — but never mid-gesture, which
            // would fight the cursor.
            _root.SizeChanged += (s, e) => { if (!_dragging && !_resizing) { ClampPosition(); UpdateBox(); } };

            _handleHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _handleHide.Tick += (s, e) =>
            {
                _handleHide.Stop();
                if (!_editing && !_resizing) _handle.Visibility = Visibility.Collapsed;
            };

            _root.MouseRightButtonUp += (s, e) => { e.Handled = true; try { ModeCycleRequested?.Invoke(); } catch { } };

            if (_host == null)
            {
                _handle.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: true, e); };
                _root.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginGesture(resize: false, e); };
                _root.MouseMove += (s, e) =>
                {
                    _handle.Visibility = Visibility.Visible;
                    _handleHide.Stop(); _handleHide.Start();
                };

                // Ask HDT to treat this element as clickable: its 60Hz hover loop drops WS_EX_TRANSPARENT
                // from the overlay window while the cursor is inside us, so wheel/right-click land here.
                // Meaningless for a preview, which is not in HDT's overlay at all.
                try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
            }
        }

        /// <summary>Restore the saved placement (call before the first show). <paramref name="wf"/> &lt;= 0
        /// means the panel has never been arranged, and summons stay cursor-anchored.</summary>
        public void Place(double xf, double yf, double wf)
        {
            if (wf <= 0) return;
            _xf = xf; _yf = yf; _wf = wf; _hasPos = true;
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        /// <summary>Where the panel currently sits in its canvas, at rendered (post-scale) size.
        /// Zero-size until it has been laid out.</summary>
        public Rect Bounds
        {
            get
            {
                double x = Canvas.GetLeft(_root), y = Canvas.GetTop(_root);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                return new Rect(x, y, RenderedW, RenderedH);
            }
        }

        /// <summary>Arrange mode: dashed outline, name tag and a pinned grabber.</summary>
        public void SetEditChrome()
        {
            _editing = true;
            _editOutline.Visibility = Visibility.Visible;
            _editLabel.Visibility = Visibility.Visible;
            _handle.Visibility = Visibility.Visible;
        }

        public void ClearEditChrome()
        {
            _editing = false;
            _editOutline.Visibility = Visibility.Collapsed;
            _editLabel.Visibility = Visibility.Collapsed;
            _handle.Visibility = Visibility.Collapsed;
        }

        /// <summary>Show the panel (adding it to the canvas on first use). Canvas thread.</summary>
        public void Show()
        {
            if (!Attach()) return;
            _root.Visibility = Visibility.Visible;
            if (_host == null) InstallMouseHook();   // a settings preview must never hook the mouse
            Layout();
            // Content changes resize the panel, so re-clamp and refresh the hover box after this layout
            // pass too — not only on a fresh summon (a stale box makes the panel vanish under the cursor).
            _root.Dispatcher.BeginInvoke(new Action(() => { ClampPosition(); UpdateBox(); }), DispatcherPriority.Loaded);
        }

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

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_root.Visibility == Visibility.Visible) Layout();
        }

        /// <summary>Hide the panel but keep it attached for the next summon. Canvas thread.</summary>
        public void Hide()
        {
            EndGesture(persist: false);
            _root.Visibility = Visibility.Collapsed;
            _box = null;
            RemoveMouseHook();
        }

        /// <summary>Remove the panel from its canvas (plugin unload). Canvas thread.</summary>
        public void Close()
        {
            EndGesture(persist: false);
            RemoveMouseHook();
            _box = null;
            try
            {
                var canvas = Host;
                if (canvas != null)
                {
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                }
            }
            catch { /* HDT may be tearing down */ }
            _attached = false;
        }

        /// <summary>Summon placement, in canvas coordinates. Once the panel has been ARRANGED, the
        /// saved spot wins outright — being able to rely on where it appears is the whole point of
        /// making it positionable.
        ///
        /// Until then the original rule stands: the CURSOR is the reference point (it sits on the
        /// hovered button) — the panel's right edge lands ~350px left of it, scaled by the canvas'
        /// 16:9 content width (350 tuned at 1920×1080 → about-centered), so the button and the game's
        /// own tooltip stay clear. Vertical: centered (height isn't known until layout → corrected
        /// right after the next layout pass). Fully clamped inside the canvas.</summary>
        public void PlaceForSummon()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            Layout();
            if (_hasPos) return;   // Layout has already put it back on its saved fractions

            _autoCentre = false;   // there is a cursor to anchor to; the centring request is off
            double contentW = Math.Min(cw, ch * (16.0 / 9.0));
            double offset = 350.0 * contentW / 1920.0;

            double w = RenderedW;
            var cursor = new Point(cw / 2, ch / 2);
            try
            {
                GetCursorPos(out POINT c);
                cursor = canvas.PointFromScreen(new Point(c.X, c.Y));
            }
            catch { }

            Canvas.SetLeft(_root, Clamp(cursor.X - offset - w, 0, Math.Max(0, cw - w)));
            Canvas.SetTop(_root, Clamp((ch - 380) / 2, 0, Math.Max(0, ch - 100)));   // estimate; fixed below

            canvas.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    double h = RenderedH;
                    if (h > 0) Canvas.SetTop(_root, Clamp((ch - h) / 2, 0, Math.Max(0, ch - h)));
                    UpdateBox();
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Resolve the scale from the saved nominal width and re-clamp. Canvas thread.</summary>
        private void Layout()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // An unarranged panel derives its fraction from the CURRENT canvas, so it always resolves
            // to scale 1.0 — exactly how it rendered before it was scalable. Only a saved _wf makes the
            // panel track the resolution.
            double wf = _wf > 0 ? _wf : RefW / cw;
            _nominalW = Clamp(wf * cw, MinNomW, MaxNomW);
            double s = _nominalW / RefW;
            if (Math.Abs(_scale.ScaleX - s) > 0.001) { _scale.ScaleX = s; _scale.ScaleY = s; }

            // The list cap is in UNSCALED units — the transform multiplies it back up, so a scaled-up
            // panel would otherwise run off the bottom of the game window.
            _scroll.MaxHeight = Math.Min(500, Math.Max(160, (ch - 120 * s) / s));

            ClampPosition();
        }

        /// <summary>Keep an arranged panel on its saved fractions and inside the canvas. A panel that
        /// has never been arranged owns its own position (the cursor put it there), so this only
        /// guarantees it HAS one.</summary>
        private void ClampPosition()
        {
            if (_dragging || _resizing) return;
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double w = RenderedW, h = RenderedH;
            if (w <= 0) w = _nominalW;

            if (_hasPos)
            {
                Canvas.SetLeft(_root, Clamp(_xf * cw, 0, Math.Max(0, cw - w)));
                Canvas.SetTop(_root, Clamp(_yf * ch, 0, Math.Max(0, ch - Math.Max(80, h))));
                return;
            }

            // Never arranged. The cursor rule owns the position whenever there IS a cursor to anchor
            // to; arrange mode has none and asks to be centred instead. Either way the position must
            // never be left UNSET: an unset Canvas.Left/Top reads back as NaN, which draws the panel
            // at the canvas origin AND makes it undraggable — BeginGesture has nothing to offset from
            // and refuses. That is exactly what arrange mode hit: the panel sat in the top-left corner
            // and neither moved nor resized.
            if (!_autoCentre && !double.IsNaN(Canvas.GetLeft(_root)) && !double.IsNaN(Canvas.GetTop(_root))) return;
            Canvas.SetLeft(_root, Clamp((cw - w) / 2, 0, Math.Max(0, cw - w)));
            Canvas.SetTop(_root, Clamp((ch - h) / 2, 0, Math.Max(0, ch - h)));
            // The height is only real after a layout pass, so the first centring is provisional — hold
            // the request open until one has run and Show's deferred pass can correct it.
            if (h > 0) _autoCentre = false;
        }

        /// <summary>Centre the panel on the next layout pass. For arrange mode, which puts the panel up
        /// with no hovered button to anchor it to.</summary>
        public void CentreInCanvas()
        {
            if (_hasPos) return;   // an arranged panel already has a spot of its own
            _autoCentre = true;
        }

        // ── Move / resize gestures ───────────────────────────────────────────────────────────────
        // No hook of its own: the low-level mouse hook is already up for the whole time the panel is
        // visible (it forwards the wheel), which is exactly the window in which a gesture can happen.

        private void BeginGesture(bool resize, MouseButtonEventArgs e)
        {
            if (_host != null) return;   // preview: never drag, never write geometry
            if (!_attached || _dragging || _resizing) return;
            // The hook is what ends the gesture. Without it a drag would latch on forever, and since a
            // live gesture keeps the panel from hiding, the panel would never go away again.
            if (_mouseHook == IntPtr.Zero) return;
            var canvas = Host;
            if (canvas == null) return;
            try { _startCursor = e.GetPosition(canvas); } catch { return; }
            _startLeft = Canvas.GetLeft(_root);
            _startTop = Canvas.GetTop(_root);
            _startW = _nominalW;
            if (double.IsNaN(_startLeft) || double.IsNaN(_startTop) || _startW <= 0) return;
            _moved = false;
            _dragging = !resize;
            _resizing = resize;
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
                double w = RenderedW, h = RenderedH;
                Canvas.SetLeft(_root, Clamp(_startLeft + dx, 0, Math.Max(0, cw - w)));
                Canvas.SetTop(_root, Clamp(_startTop + dy, 0, Math.Max(0, ch - h)));
            }
            else if (_resizing)
            {
                // Drag right to scale up: this drives the nominal width Layout turns into the scale.
                _wf = Clamp(_startW + dx, MinNomW, MaxNomW) / cw;
                Layout();
            }
            // The panel is moving without a fresh layout pass, so the cached hover box would go stale
            // and the watcher would decide the cursor had left — hiding the panel mid-drag.
            UpdateBox();
        }

        private void EndGesture(bool persist = true)
        {
            if (!_dragging && !_resizing) return;
            _dragging = _resizing = false;
            if (!persist || !_moved) return;

            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double left = Canvas.GetLeft(_root), top = Canvas.GetTop(_root);
            if (double.IsNaN(left) || double.IsNaN(top)) return;
            _xf = left / cw;
            _yf = top / ch;
            _wf = _nominalW / cw;
            _hasPos = true;   // from here on the panel summons where the user put it, not at the cursor
            try { GeometryChanged?.Invoke(_xf, _yf, _wf); } catch { }
        }

        // Cache the panel's screen box (device px) for the cross-thread hover test + the wheel hook.
        private void UpdateBox()
        {
            try
            {
                if (_root.Visibility != Visibility.Visible || _root.ActualWidth <= 0) { _box = null; return; }
                var tl = _root.PointToScreen(new Point(0, 0));
                var br = _root.PointToScreen(new Point(_root.ActualWidth, _root.ActualHeight));
                _box = new Box { L = tl.X, T = tl.Y, R = br.X, B = br.Y };
            }
            catch { _box = null; }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        public struct Row
        {
            public string Name; public string Text; public string Note; public bool Current;
            public int Emph;   // 0 = normal, 1 = guaranteed-tribe relevant (green), 2 = unique enabler (purple)
        }

        /// <summary>One guaranteed-tribe pool minion, rendered as its card art in the left column.
        /// Emph: 1 = pool member (green outline), 2 = sole enabler of some gift (purple outline).</summary>
        public struct MinionArt { public BgCard Card; public int Emph; }

        /// <summary>Replace the content: contextual header line (may be empty → hidden), one container
        /// per still-obtainable gift, and optionally the guaranteed-tribe pool as card renders in the
        /// left column (auto-fit ≤4 columns; poolTotal &gt; shown count adds a "+N more" note). Sets
        /// the panel width — callers reposition AFTER calling this.</summary>
        public void SetContent(string headerSub, IReadOnlyList<Row> rows, string poolCaption,
            IReadOnlyList<MinionArt> minions, int poolTotal)
        {
            _headerSub.Text = headerSub ?? "";
            _headerSub.Visibility = string.IsNullOrEmpty(headerSub) ? Visibility.Collapsed : Visibility.Visible;
            _rows.Children.Clear();
            foreach (var r in rows) _rows.Children.Add(BuildRow(r));
            try { _scroll.ScrollToTop(); } catch { }
            // Minions-only mode passes an empty row list — the whole gift column collapses and the
            // panel is just the art column.
            bool giftsShown = rows != null && rows.Count > 0;
            _giftColumn.Visibility = giftsShown ? Visibility.Visible : Visibility.Collapsed;

            double artWidth = 0;
            _artWrap.Children.Clear();
            if (minions != null && minions.Count > 0)
            {
                // Auto-fit: fewest columns unless another one buys meaningfully bigger cards (+15px),
                // capped at 200 wide / 4 columns, floored at ~110 so a huge pool stays recognizable.
                int n = minions.Count;
                double bestW = 0; int bestC = 1;
                for (int c = 1; c <= 4; c++)
                {
                    int rws = (n + c - 1) / c;
                    double w = Math.Min(ArtMaxW, (ArtMaxH / rws - ArtGap) / ArtAspect);
                    if (w > bestW + 15) { bestW = w; bestC = c; }
                }
                double cardW = Math.Max(ArtMinW, bestW);
                int decode = (int)Math.Min(256, cardW * 1.25);

                _artWrap.Width = bestC * (cardW + ArtGap + 8);   // +8 ≈ per-cell border/padding
                foreach (var m in minions)
                {
                    var img = new Image { Width = cardW, Stretch = Stretch.Uniform };
                    ArtImage.SetDecode(img, decode);
                    ArtImage.SetCard(img, m.Card);
                    _artWrap.Children.Add(new Border
                    {
                        BorderBrush = m.Emph == 2 ? UniqueBrush : TribeBrush,
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(2),
                        Margin = new Thickness(0, 0, ArtGap, ArtGap),
                        Child = img
                    });
                }

                _artCaption.Text = poolCaption ?? "";
                _artCaption.Visibility = string.IsNullOrEmpty(poolCaption) ? Visibility.Collapsed : Visibility.Visible;
                _artCaption.MaxWidth = _artWrap.Width;
                if (poolTotal > n) { _artMore.Text = "+" + (poolTotal - n) + " more in the pool"; _artMore.Visibility = Visibility.Visible; }
                else _artMore.Visibility = Visibility.Collapsed;

                _artColumn.Visibility = Visibility.Visible;
                artWidth = _artWrap.Width + 10;                  // + column right margin
            }
            else _artColumn.Visibility = Visibility.Collapsed;

            _root.Width = (giftsShown ? ContentWidth : 0) + 22 + artWidth;
        }

        private static UIElement BuildRow(Row r)
        {
            Brush accent = r.Emph == 2 ? UniqueBrush : r.Emph == 1 ? TribeBrush : UiKit.AccentBrush;
            Color glow = r.Emph == 2 ? UniqueColor : r.Emph == 1 ? TribeColor : UiKit.Accent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = r.Name,
                Foreground = r.Current ? accent : UiKit.TextSecondary,
                FontSize = 14,
                FontWeight = r.Current ? FontWeights.Bold : FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var sep = new Rectangle
            {
                Width = 1,
                Fill = r.Current ? accent : UiKit.StrokeBrush,
                Opacity = r.Current ? 0.55 : 1.0,
                Margin = new Thickness(8, 1, 9, 1),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(sep, 1);
            grid.Children.Add(sep);

            var text = new TextBlock
            {
                FontSize = 14,
                Foreground = r.Current ? UiKit.TextPrimary : UiKit.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            AddColoredRuns(text, r.Text);
            if (!string.IsNullOrEmpty(r.Note))
                text.Inlines.Add(new Run("  — " + r.Note)
                {
                    Foreground = UiKit.TextSecondary,   // italic already distinguishes it — don't dim
                    FontStyle = FontStyles.Italic,
                    FontSize = 13
                });
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var box = new Border
            {
                Background = r.Current ? RowBg : RowBgDim,
                BorderBrush = r.Current ? accent : UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 5, 9, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid
            };
            if (r.Current)
                box.Effect = new DropShadowEffect
                {
                    Color = glow,
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.45
                };
            else
                box.Opacity = 0.5;
            return box;
        }

        // Word-level color highlighting: stat gains in green, keyword-ish terms in gold, rest default.
        private static readonly Regex Highlight = new Regex(
            @"(?<stat>\+\d+(?:/\+\d+)?(?:\s+(?:Attack|Health))?)|" +
            @"(?<kw>Divine Shield|Windfury|Stealth|Venomous|Reborn|Golden|Immune|Deathrattles?|Battlecr(?:y|ies)|Rally|Spellcrafts?|Start of Combat|Magnetize|Blood Gems|Taunt)",
            RegexOptions.Compiled);

        private static void AddColoredRuns(TextBlock tb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int pos = 0;
            foreach (Match m in Highlight.Matches(text))
            {
                if (m.Index > pos) tb.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                tb.Inlines.Add(new Run(m.Value)
                {
                    Foreground = m.Groups["stat"].Success ? StatBrush : KeywordBrush,
                    FontWeight = FontWeights.SemiBold
                });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) tb.Inlines.Add(new Run(text.Substring(pos)));
        }

        // ── Low-level mouse hook (active only while the panel is visible) ───────────────────────────
        // Two duties, one hook, because both need exactly the same lifetime:
        //   • wheel forwarding — the cursor sits on the game's button while the panel is up, so WPF
        //     never receives the wheel. The hook watches WM_MOUSEWHEEL globally and scrolls our list;
        //     when the cursor IS over the panel, native WPF wheel handling takes over (no double-scroll).
        //   • move/resize — WPF mouse capture can't be trusted on HDT's overlay: the instant the cursor
        //     outruns the panel, HDT re-enables click-through and the window stops receiving input.
        // It NEVER swallows an event.
        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelMouseProc _hookProc;    // keep the delegate alive while hooked

        private void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            try
            {
                _hookProc = MouseHookProc;
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
            }
            catch { _mouseHook = IntPtr.Zero; _hookProc = null; }
        }

        private void RemoveMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
            _hookProc = null;
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                if (wParam == (IntPtr)WM_MOUSEWHEEL && !IsUnderMouse)
                {
                    try
                    {
                        // MSLLHOOKSTRUCT: POINT pt (8 bytes) then DWORD mouseData — wheel delta in the high word.
                        int mouseData = Marshal.ReadInt32(lParam, 8);
                        int delta = (short)((mouseData >> 16) & 0xFFFF);
                        _root.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - delta / 120.0 * 64.0); } catch { }
                        }));
                    }
                    catch { }
                }
                else if (_dragging || _resizing)
                {
                    if (wParam == (IntPtr)WM_MOUSEMOVE) { try { OnGestureMove(); } catch { } }
                    else if (wParam == (IntPtr)WM_LBUTTONUP) { try { EndGesture(); } catch { } }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
