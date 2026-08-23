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
        private const double MaxNameW = 108;      // reference px; longer names ellipsize

        /// <summary>What fills the name column: "Players", "Heroes" or "Off". See
        /// <see cref="PluginConfig.OpponentNameMode"/>.</summary>
        public string NameMode { get; set; } = "Heroes";
        public bool ShowRating { get; set; } = true;
        public bool ShowDeltas { get; set; } = true;
        public bool ShowTiers { get; set; } = true;
        public bool DimDead { get; set; } = true;
        /// <summary>Duos: rows arrive team-ordered (pairs 0+1, 2+3, …) — a wider gap separates teams.</summary>
        public bool IsDuos { get; set; }

        /// <summary>A move/resize gesture ended — receives the new placement fractions (xf, yf, wf).</summary>
        public Action<double, double, double> GeometryChanged;

        // Column order. The delta gets a column of its OWN, left of the rating, so a row with a delta
        // can't shove the rating and tier sideways — every rating in the list starts at the same x.
        private const int ColPlace = 0, ColName = 1, ColDelta = 2, ColRating = 3, ColTier = 4, ColCount = 5;

        private readonly Border _root;
        private readonly Grid _list;                           // ONE grid: columns are shared by every row
        private readonly ColumnDefinition[] _cols = new ColumnDefinition[ColCount];
        private readonly Line[] _teamDividers = new Line[3];   // duos: dashed line between team pairs
        private readonly TextBlock[] _places = new TextBlock[MaxSlots];        // solo: one per player
        private readonly TextBlock[] _placeSpans = new TextBlock[MaxSlots / 2];// duos: one per team, spanning
        private readonly TextBlock[] _names = new TextBlock[MaxSlots];
        private readonly TextBlock[] _ratings = new TextBlock[MaxSlots];
        private readonly TextBlock[] _arrows = new TextBlock[MaxSlots];
        private readonly Image[] _tiers = new Image[MaxSlots];
        private readonly FrameworkElement[][] _rowCells = new FrameworkElement[MaxSlots][];
        private readonly bool[] _colUsed = new bool[ColCount];
        private readonly Border _handle;
        private readonly Rectangle _editOutline;
        private readonly Border _editLabel;
        private readonly DispatcherTimer _handleHide;

        private readonly Canvas _host;      // null = HDT's overlay canvas
        private bool _attached;
        private bool _editing;
        private bool _hasPos;
        private double _xf = DefaultXF, _yf = DefaultYF, _wf;

        private bool _dragging, _resizing, _moved;
        private Point _startCursor;
        private double _startLeft, _startTop, _startW;
        private double _nominalW = RefW;    // drives the scale; the real width comes from the content

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xDC, 0x0A, 0x0D, 0x14));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB4));
        private static readonly Brush Dead = Frozen(Color.FromRgb(0x91, 0x91, 0x91));
        private static readonly Brush Up = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Down = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));

        /// <summary>The canvas this instance lives in: HDT's game overlay by default, or a caller's own
        /// canvas for an off-game preview. Held per instance rather than swapped globally, so a preview
        /// can never capture the live in-match panel's attach (and vice versa) — _host is readonly and
        /// _attached caches, so an instance is bound to one canvas for its whole life.</summary>
        private Canvas Host => _host ?? Core.OverlayCanvas;

        public MmrSidePanel() : this(null) { }

        /// <param name="host">Render into this canvas instead of HDT's overlay. A hosted instance is a
        /// passive preview: it registers no overlay hit-testing and wires no drag/resize gestures, so it
        /// can never install the global mouse hook or write geometry back to config.</param>
        public MmrSidePanel(Canvas host)
        {
            _host = host;
            // ONE grid for the whole list, so a column means the same thing on every row. Per-row
            // grids (what this used to be) size their Auto columns independently, so a delta on one row
            // would push that row's rating and tier out of line with the rest — the list stopped reading
            // as columns at all.
            _list = new Grid();
            _cols[ColPlace] = new ColumnDefinition { Width = GridLength.Auto };
            _cols[ColName] = new ColumnDefinition { Width = GridLength.Auto };
            _cols[ColDelta] = new ColumnDefinition { Width = GridLength.Auto };
            _cols[ColRating] = new ColumnDefinition { Width = GridLength.Auto };
            _cols[ColTier] = new ColumnDefinition { Width = GridLength.Auto };
            foreach (var c in _cols) _list.ColumnDefinitions.Add(c);

            // Rows run team, team-divider, team, … so a duos place number can span its pair.
            for (int r = 0; r < MaxSlots + _teamDividers.Length; r++)
                _list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int t = 0; t < _placeSpans.Length; t++)
            {
                var span = new TextBlock
                {
                    Foreground = Muted, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Visibility = Visibility.Collapsed
                };
                Grid.SetColumn(span, ColPlace); Grid.SetRow(span, RowOf(t * 2)); Grid.SetRowSpan(span, 2);
                _list.Children.Add(span);
                _placeSpans[t] = span;

                for (int k = 0; k < 2; k++)
                {
                    int i = t * 2 + k;
                    int row = RowOf(i);

                    var place = new TextBlock
                    {
                        Foreground = Muted, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Right
                    };
                    var name = new TextBlock
                    {
                        Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var arrow = new TextBlock
                    {
                        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right
                    };
                    var rating = new TextBlock
                    {
                        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right
                    };
                    var tier = new Image { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(tier, BitmapScalingMode.HighQuality);

                    Place(place, ColPlace, row);
                    Place(name, ColName, row);
                    Place(arrow, ColDelta, row);
                    Place(rating, ColRating, row);
                    Place(tier, ColTier, row);

                    _places[i] = place; _names[i] = name; _arrows[i] = arrow;
                    _ratings[i] = rating; _tiers[i] = tier;
                    _rowCells[i] = new FrameworkElement[] { place, name, arrow, rating, tier };
                }

                // Duos: a dashed divider after every pair, so the list reads as 4 teams, not 8 solos.
                if (t < _teamDividers.Length)
                {
                    var d = new Line
                    {
                        X1 = 0, Y1 = 0, X2 = 1, Y2 = 0,
                        Stretch = Stretch.Fill,
                        Stroke = Muted, StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 3, 3 },
                        Opacity = 0.6,
                        Visibility = Visibility.Collapsed
                    };
                    Grid.SetRow(d, RowOf(t * 2 + 1) + 1);
                    Grid.SetColumn(d, 0); Grid.SetColumnSpan(d, ColCount);
                    _teamDividers[t] = d;
                    _list.Children.Add(d);
                }
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

            // Content-driven width: re-clamp whenever it actually changes (a column switched off, a
            // longer name, a rescale). Never during a drag — that would fight the cursor.
            _root.SizeChanged += (s, e) => { if (!_dragging && !_resizing) ClampPosition(); };

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
                _root.MouseMove += (s, e) => { _handle.Visibility = Visibility.Visible; _handleHide.Stop(); _handleHide.Start(); };

                // Registers with HDT's hover loop so the overlay stops being click-through over this
                // element. Meaningless for a preview, which is not in HDT's overlay at all.
                try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
            }
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        /// <summary>Keep the panel inside the canvas. Split out of Layout and also driven by the
        /// panel's own SizeChanged, because the width now depends on the content: it isn't known until
        /// WPF has measured, and calling UpdateLayout to force that would run a layout pass over the
        /// WHOLE tree from inside a layout callback.</summary>
        private void ClampPosition()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double actualW = _root.ActualWidth > 0 ? _root.ActualWidth : _nominalW;
            Canvas.SetLeft(_root, Clamp(_xf * cw, 0, Math.Max(0, cw - actualW)));
            Canvas.SetTop(_root, Clamp(_yf * ch, 0, Math.Max(0, ch * 0.97)));
            if (!_hasPos) _hasPos = true;
        }

        // Grid row for player slot i: two players per team, then a divider row between teams.
        private static int RowOf(int slot) => slot + slot / 2;

        private void SetRowVisible(int i, bool on)
        {
            var v = on ? Visibility.Visible : Visibility.Collapsed;
            foreach (var c in _rowCells[i]) c.Visibility = v;
        }

        private void Place(UIElement el, int col, int row)
        {
            Grid.SetColumn(el, col); Grid.SetRow(el, row);
            _list.Children.Add(el);
        }

        /// <summary>Where the panel currently sits in its canvas, once laid out (zero-size until then).</summary>
        public Rect Bounds
        {
            get
            {
                double x = Canvas.GetLeft(_root), y = Canvas.GetTop(_root);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                return new Rect(x, y, _root.ActualWidth, _root.ActualHeight);
            }
        }

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

        /// <summary>Fill + show the panel (row 0 = 1st place). Canvas thread. Empty/null — or every
        /// content part switched off — hides it.</summary>
        public void SetStandings(IReadOnlyList<LeaderboardOverlay.Row> rows)
        {
            bool showNames = !string.Equals(NameMode, "Off", StringComparison.OrdinalIgnoreCase);
            bool heroes = string.Equals(NameMode, "Heroes", StringComparison.OrdinalIgnoreCase);
            // The place number is always worth showing here: unlike the portrait labels, this list is
            // detached from the board, so nothing else says who is 1st. It is not part of anyContent
            // for the same reason a row of bare numbers is not a standings panel.
            // While arranging, the box shows regardless — otherwise turning every part off would make
            // Arrange bring the game forward and then display nothing to position.
            bool anyContent = showNames || ShowRating || ShowDeltas || ShowTiers || _editing;
            int n = rows?.Count ?? 0;
            if (n == 0 || !anyContent || !Attach()) { Hide(); return; }

            for (int i = 0; i < MaxSlots; i++)
            {
                if (i >= n) { SetRowVisible(i, false); continue; }
                var r = rows[i];
                bool dim = DimDead && r.IsDead;

                SetRowVisible(i, true);
                foreach (var c in _rowCells[i]) c.Opacity = dim ? 0.6 : 1.0;

                // Solo: a number per row. Duos: one number spanning the pair (set below), so the
                // per-row ones stand down rather than repeating the team's place twice.
                _places[i].Text = r.Place >= 1 ? r.Place.ToString() : "";
                _places[i].Foreground = dim ? Dead : Muted;
                _places[i].Opacity = dim ? 0.6 : 1.0;
                _places[i].Visibility = IsDuos ? Visibility.Collapsed : Visibility.Visible;

                _names[i].Text = heroes ? (r.HeroName ?? "") : (r.Name ?? "");
                _names[i].Foreground = dim ? Dead : Brushes.White;
                _names[i].Visibility = showNames ? Visibility.Visible : Visibility.Collapsed;

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

            for (int t = 0; t < _placeSpans.Length; t++)
            {
                int first = t * 2;
                bool live = IsDuos && first < n;
                _placeSpans[t].Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                if (!live) continue;
                var r = rows[first];
                _placeSpans[t].Text = r.Place >= 1 ? r.Place.ToString() : "";
                // Dim the team number only when the WHOLE team is out — one dead teammate does not
                // knock the team off the board.
                bool teamDead = DimDead && r.IsDead && (first + 1 >= n || rows[first + 1].IsDead);
                _placeSpans[t].Foreground = teamDead ? Dead : Muted;
                _placeSpans[t].Opacity = teamDead ? 0.6 : 1.0;
            }

            for (int k = 0; k < _teamDividers.Length; k++)
                _teamDividers[k].Visibility = IsDuos && k * 2 + 2 < n ? Visibility.Visible : Visibility.Collapsed;

            // A switched-off part gives its column back rather than leaving a gap: the panel sizes to
            // its content, so dropping the names really does make it narrower.
            _colUsed[ColPlace] = true;
            _colUsed[ColName] = showNames;
            _colUsed[ColDelta] = ShowDeltas;
            _colUsed[ColRating] = ShowRating;
            _colUsed[ColTier] = ShowTiers;

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

        internal static List<LeaderboardOverlay.Row> SampleRows() => SampleRows(false);

        /// <param name="duos">Duos standings: rows arrive team-ordered and each PAIR shares one
        /// leaderboard place, which is what the spanning place number in the list renders.</param>
        internal static List<LeaderboardOverlay.Row> SampleRows(bool duos)
        {
            var outp = new List<LeaderboardOverlay.Row>();
            string[] names = { "Sevel", "DoGBiscuit", "Saphirel", "Maks7k", "Beterbabbit", "Pockyplays", "XiaoT", "Fasteddyhaha" };
            string[] heroes = { "Sire Denathrius", "Rafaam", "Queen Azshara", "Cariel Roame",
                                "The Curator", "Illidan Stormrage", "Tess Greymane", "Reno Jackson" };
            int[] ratings = { 14872, 13561, 12208, 11440, 10653, 9781, 8944, 0 };
            for (int i = 0; i < names.Length; i++)
                outp.Add(new LeaderboardOverlay.Row
                {
                    Name = names[i],
                    HeroName = heroes[i],
                    Place = duos ? i / 2 + 1 : i + 1,
                    Rating = ratings[i],
                    Delta = i == 1 ? 213 : i == 4 ? -96 : 0,
                    TavernTier = 1 + (i * 5) % 7,
                    IsDead = duos ? i >= 6 : i == 7
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
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            if (_wf <= 0) _wf = RefW / cw;
            // _wf keeps its old meaning (a nominal width fraction) so saved placements still scale the
            // same, but it now drives SCALE only — the panel's actual width comes from its content, so
            // switching a column off genuinely narrows it instead of leaving dead space.
            double w = Clamp(_wf * cw, MinW, MaxW);
            double s = w / RefW;
            _nominalW = w;

            _root.Padding = new Thickness(7 * s, 4 * s, 7 * s, 4 * s);
            foreach (var d in _teamDividers)
                d.Margin = new Thickness(2 * s, 2.5 * s, 2 * s, 1.0 * s);
            // Column gaps are LEFT margins, never right: a collapsed column then takes its gap with
            // it, and the last visible column never leaves a trailing strip of dead space.
            foreach (var span in _placeSpans)
            {
                span.FontSize = 13.5 * s;
                span.MinWidth = 13 * s;
                span.Margin = new Thickness(0);
            }
            for (int i = 0; i < MaxSlots; i++)
            {
                _places[i].FontSize = 11.5 * s;
                _places[i].MinWidth = 13 * s;   // so 1..8 all occupy one column width
                _places[i].Margin = new Thickness(0, 1.5 * s, 0, 1.5 * s);
                _names[i].FontSize = 12.5 * s;
                _names[i].Margin = new Thickness(7 * s, 1.5 * s, 0, 1.5 * s);
                // Cap the name rather than letting it widen the panel without limit: a long battletag
                // is trimmed with an ellipsis instead of pushing the rating off to the right.
                _names[i].MaxWidth = MaxNameW * s;
                _arrows[i].FontSize = 10.5 * s;
                _arrows[i].Margin = new Thickness(9 * s, 1.5 * s, 0, 1.5 * s);
                _ratings[i].FontSize = 12.5 * s;
                _ratings[i].Margin = new Thickness(6 * s, 1.5 * s, 0, 1.5 * s);
                _tiers[i].Height = 20 * s;
                _tiers[i].Margin = new Thickness(6 * s, 1.5 * s, 0, 1.5 * s);
            }

            // Collapsing the ColumnDefinition itself (not just the cells) is what actually reclaims the
            // space — an Auto column with only Collapsed children still keeps any margin its children own.
            for (int c = 0; c < ColCount; c++)
                _cols[c].Width = _colUsed[c] ? GridLength.Auto : new GridLength(0);

            ClampPosition();
        }

        // ── Move / resize gestures (LL mouse hook, installed only for the gesture) ───────────────

        private void BeginGesture(bool resize, MouseButtonEventArgs e)
        {
            if (_host != null) return;   // preview: never install the global mouse hook
            if (!_attached || _dragging || _resizing) return;
            var canvas = Host;
            if (canvas == null) return;
            try { _startCursor = e.GetPosition(canvas); } catch { return; }
            _startLeft = Canvas.GetLeft(_root);
            _startTop = Canvas.GetTop(_root);
            _startW = _nominalW;
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
                Canvas.SetLeft(_root, Clamp(_startLeft + dx, 0, Math.Max(0, cw - _root.ActualWidth)));
                Canvas.SetTop(_root, Clamp(_startTop + dy, 0, Math.Max(0, ch - _root.ActualHeight)));
            }
            else if (_resizing)
            {
                // Drag right to scale up: the panel has no fixed width any more, so this drives the
                // nominal width that Layout turns into the scale factor.
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

            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            _xf = Canvas.GetLeft(_root) / cw;
            _yf = Canvas.GetTop(_root) / ch;
            _wf = _nominalW / cw;
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
