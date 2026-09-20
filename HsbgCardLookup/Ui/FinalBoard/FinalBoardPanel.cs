using System;
using System.Collections.Generic;
using System.Globalization;
using IoPath = System.IO.Path;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HearthDb.Enums;
using HdtControls = Hearthstone_Deck_Tracker.Controls;
using Hearthstone_Deck_Tracker.Hearthstone;            // Database
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Utility.Assets;         // CardAssetType
using HsbgCardLookup.Config;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// Renders one <see cref="FinalBoardRecord"/> — the end-of-match warband, who played it and how
    /// it went. THE single renderer: every surface (the match-history click, the settings preview,
    /// the PNG export) shows this same visual, so a change to what a final board looks like happens
    /// once, here.
    ///
    /// The minions, hero power and trinkets are drawn by <b>HDT's own controls</b>
    /// (<c>BattlegroundsMinion</c>, <c>HeroPower</c>, <c>Trinket</c>), which take an
    /// <see cref="Entity"/> and paint the real thing — attack/health, golden frame, taunt, divine
    /// shield, poison, reborn. Our own <c>CardArt</c> pipeline draws static card images and cannot
    /// express any of that, which is exactly why a stored board is rehydrated into entities here
    /// rather than into our own card records. It is the same trick HDT's session tooltip uses on the
    /// same stored data, so it is proven to work from a record with no match running.
    ///
    /// The frame is Reign-in-blood's artwork from HDT-FinalStatsPlugin, and so is the composition it
    /// was drawn for: trinkets left, a tall hero portrait breaking the top rail, hero power and
    /// anomaly right, then a band reading rank · MMR · turn · hero · biggest creature · duration.
    /// He is a co-author of this feature. The layout below is his, and so is the way the artwork is
    /// drawn: <b>whole, at the size it was painted</b>, never resampled. His panel is one fixed
    /// height and ours has to hold either a board or a tall stats table, so where he can let the
    /// image fill the panel we pin it to the top and let the flat zone under it carry on downwards.
    ///
    /// Layout is authored in reference pixels and scaled as a whole (<see cref="Scale"/>), the way
    /// every other canvas element in this codebase works.
    /// </summary>
    internal sealed class FinalBoardPanel
    {
        // ── the artwork ─────────────────────────────────────────────────────────────────────────
        // Drawn once, whole, at 920x410, and never scaled in either direction. Cutting it into rows
        // and stretching them was tried first and it does not survive contact with the painting: the
        // gold rail, the radial glow behind the hero and above all the red rank shield — which hangs
        // BELOW the band's lower rule, from y152 to y235 — straddle the seams, so every seam that
        // moves distorts something that was drawn to sit still.
        private const double PanelW = 920;
        private const double ArtH = 410;

        // Reign's rows, and they are what makes the untouched image work: they do not describe the
        // painting, they are laid over it, and two lifts put the content back on the painted seams.
        // Header 204 / band 58 / the rest, the whole thing raised 15, the band raised a further 30 —
        // which lands the band's six cells between the gold rules at y157 and y216, and the RANK cell
        // squarely on the shield. Verified against the PNG, not copied on faith.
        private const double HeaderH = 204;
        private const double BandH = 58;
        private const double ContentLift = 15;
        private const double BandLift = ContentLift + 30;
        // The turn sub-view is pulled up into the strip the band lift leaves empty under the band.
        private const double TurnViewLift = 30;

        // Reign's sizes, and they matter: HDT's Trinket/HeroPower are drawn for 110x110.
        private const double MinionSize = 134;
        private const double MinionOverlap = -5;
        private const double PortraitW = 190, PortraitH = 268;
        // The box, not the medallion. HDT draws the trinket's ring smaller inside its 110 natural
        // square than the hero power draws its own, so equal boxes leave the trinkets visibly the
        // smaller pair: measured off the render, 100 and 130 came out as rings of 77 and 104. 135
        // is what makes the two rings the same size, which is what "the same size" means to the eye.
        private const double TrinketSize = 135;

        // The Dark Gift medallion, which stands where an anomaly would. Square, so it is sized to
        // the anomaly card's height rather than its width, and it carries no transparent margin of
        // its own — 110 puts its ring level with the hero power's beside it.
        private const double DarkGiftMarkSize = 110;
        private const double HeroPowerSize = 130;

        // How far a medallion fades when the turn on screen predates owning it.
        private const double NotYetOwned = 0.28;
        private const double AnomalyW = 90, AnomalyH = 130;

        // Two trinkets at 135 no longer fit Reign's 200 column, and neither did a hero power beside
        // an anomaly (130 + 90 + 6) — that one was overflowing already. The outer columns take the
        // extra; the middle one keeps the portrait's 200, so the portrait sits where it always did.
        private const double DetailsW = 700;
        private static readonly double[] DetailCols = { 250, 200, 250 };
        private const double BandContentW = 740;

        private readonly Border _root;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);

        private readonly StackPanel _trinkets, _board, _powers;
        private readonly HdtControls.CardImage _portrait;
        private readonly TextBlock _emptyBoard;
        private readonly TextBlock _playerName, _playedAt;

        private TextBlock _rank, _mmr, _turn, _hero, _biggest, _duration;

        private readonly FinalBoardStatsView _stats = new FinalBoardStatsView();
        private readonly FinalBoardTurnView _turnView = new FinalBoardTurnView();
        private readonly FinalBoardOptions _options;
        private MatchStats _currentStats;
        /// <summary>The record on screen — kept because the turn sub-view re-skins the header's
        /// medallions per turn and has to be able to put them back.</summary>
        private FinalBoardRecord _currentRec;
        private StackPanel _boardView;
        private ScrollViewer _scroll;
        private Border _tabBoard, _tabStats;
        private StackPanel _chrome;
        private Border _chromeBar;

        /// <summary>The ✕ was clicked. The panel never closes itself — whoever hosts it owns that.</summary>
        public Action CloseRequested;

        /// <summary>The camera was clicked.</summary>
        public Action ScreenshotRequested;

        /// <summary>
        /// Which view the panel opens on, remembered for the session. Stats is the default because it
        /// is the half the game does not already show: a player who has just watched their own final
        /// board fill the screen does not need us to redraw it, but nothing anywhere tells them they
        /// left nine gold unspent. The board stays one click away for when it is the board they want.
        /// </summary>
        private static bool _rememberedStats = true;

        /// <summary>This panel's current view. Seeded from the remembered choice, but a panel whose
        /// options leave one view empty falls back to the other WITHOUT writing that back: a preview
        /// showing only a board is not the player deciding they prefer boards.</summary>
        private bool _showStats = _rememberedStats;

        /// <summary>
        /// The turn sub-view is open, under the Stats tab. It has no tab of its own and is never
        /// remembered: a panel opens on the table, and the turn is one click and one Esc deep.
        /// </summary>
        private bool _showTurn;

        public bool InTurnView => _showTurn;

        /// <param name="options">
        /// What to draw. Null means everything, which is what the PNG export and any caller with no
        /// config wants — a record that has been filtered down on screen is still whole on disk.
        /// </param>
        public FinalBoardPanel(FinalBoardOptions options = null)
        {
            _options = options ?? new FinalBoardOptions();
            _stats.TurnClicked = OpenTurn;
            _turnView.Back = () => Back();

            _trinkets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _powers = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _board = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _portrait = new HdtControls.CardImage
            {
                Width = PortraitW,
                Height = PortraitH,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _emptyBoard = Line("No minions on the final board", 16, UiKit.TextMuted);
            _emptyBoard.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyBoard.VerticalAlignment = VerticalAlignment.Center;
            _emptyBoard.Visibility = Visibility.Collapsed;

            _playerName = Line("", 13, Muted);
            _playerName.FontWeight = FontWeights.SemiBold;
            _playerName.HorizontalAlignment = HorizontalAlignment.Left;
            _playedAt = Line("", 11.5, Muted);
            _playedAt.FontWeight = FontWeights.SemiBold;
            _playedAt.HorizontalAlignment = HorizontalAlignment.Right;

            _root = new Border
            {
                Width = PanelW,
                Background = Brushes.Transparent,
                RenderTransform = _scale,
                RenderTransformOrigin = new Point(0, 0),
                Child = BuildBody(),
            };
        }

        public FrameworkElement Root => _root;

        /// <summary>Rendered size in canvas units, i.e. reference size times the current scale.</summary>
        public Size RenderedSize => new Size(PanelW * _scale.ScaleX, _root.ActualHeight * _scale.ScaleY);

        /// <summary>
        /// Keep the panel no taller than <paramref name="maxRef"/> reference pixels by giving the
        /// scrolling body only the height that is left, after everything that does not scroll —
        /// the chrome, the header, the band, the footer — has taken its own. Called by the host
        /// after every layout that could change the height; the export never calls it.
        /// </summary>
        public bool LimitHeight(double maxRef)
        {
            if (_scroll == null || maxRef <= 0) return false;
            // Read, never re-lay out: the first version reset MaxHeight to unlimited and forced a
            // layout to measure, which raised SizeChanged, which called this again — HDT's UI thread
            // spun on that forever (2026-09-06). The scroll viewer already knows its content's full
            // height (ExtentHeight) whatever its current limit, and the rest of the panel is the
            // root's height less the viewer's own.
            double others = _root.ActualHeight - _scroll.ActualHeight;
            double content = _scroll.ExtentHeight;
            if (others < 0 || content <= 0) return false;
            double allowed = Math.Max(120, maxRef - others);
            double wanted = content > allowed ? allowed : double.PositiveInfinity;
            bool same = double.IsPositiveInfinity(wanted)
                ? double.IsPositiveInfinity(_scroll.MaxHeight)
                : Math.Abs(wanted - _scroll.MaxHeight) < 0.5;
            if (same) return false;
            _scroll.MaxHeight = wanted;
            return true;   // a layout follows; the host fits again on its SizeChanged
        }

        public double Scale
        {
            get { return _scale.ScaleX; }
            set
            {
                double s = Math.Max(0.35, Math.Min(2.5, value));
                if (Math.Abs(s - _scale.ScaleX) < 0.002) return;
                _scale.ScaleX = _scale.ScaleY = s;
            }
        }

        /// <summary>Hidden while the panel is being captured, so a shared picture carries the match
        /// and not our buttons — and by the export path, which has nothing for them to do.</summary>
        public bool ChromeVisible
        {
            get { return _chromeBar != null && _chromeBar.Visibility == Visibility.Visible; }
            set { if (_chromeBar != null) _chromeBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }

        // ── structure ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The chrome sits ABOVE the frame rather than on it. Everything inside the border is Reign's
        /// composition, his artwork is drawn to be looked at rather than to have buttons parked on its
        /// ornament, and a control over painted gold is hard to see anyway.
        /// </summary>
        private UIElement BuildBody()
        {
            var outer = new StackPanel();
            outer.Children.Add(Chrome());
            outer.Children.Add(Frame());
            return outer;
        }

        private UIElement Frame()
        {
            var art = LoadArt();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BandH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // The background, and it is one picture: the artwork at 1:1, pinned to the top, over a
            // ground taken from its own bottom edge. A stats table can push the panel past 410 and
            // all of that growth happens below the painting, in a zone the painting had left flat.
            // The lifts are on the content, never on this — the image is the fixed thing here.
            var bg = Background(art);
            Grid.SetRow(bg, 0);
            Grid.SetRowSpan(bg, 3);
            Panel.SetZIndex(bg, 0);
            grid.Children.Add(bg);

            // The portrait is 268 tall in a 204 row and is meant to be: it breaks the rail above it
            // and comes down to rest on the band, which is what makes the composition read as a hero
            // rather than as a row of icons. Nothing here clips.
            var details = Details();
            Grid.SetRow(details, 0);
            Panel.SetZIndex(details, 10);
            details.RenderTransform = new TranslateTransform(0, -ContentLift);
            grid.Children.Add(details);

            var band = Band();
            Grid.SetRow(band, 1);
            Panel.SetZIndex(band, 5);
            band.RenderTransform = new TranslateTransform(0, -BandLift);
            grid.Children.Add(band);

            var body = Body();
            Grid.SetRow(body, 2);
            Panel.SetZIndex(body, 5);
            body.RenderTransform = new TranslateTransform(0, -ContentLift);
            grid.Children.Add(body);

            return new Border
            {
                Width = PanelW,
                MinHeight = ArtH,   // never crop the painting; a short body just leaves flat ground
                BorderBrush = Frozen(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = art == null ? (Brush)new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90) : Brushes.Transparent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Child = grid,
            };
        }

        // ── row 0: trinkets · hero · hero power + anomaly ───────────────────────────────────────

        private FrameworkElement Details()
        {
            var details = new Grid
            {
                Width = DetailsW,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var w in DetailCols)
                details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            Put(details, 0, _trinkets);

            Grid.SetColumn(_portrait, 1);
            Panel.SetZIndex(_portrait, 10);
            details.Children.Add(_portrait);

            Put(details, 2, _powers);
            return details;
        }

        // ── row 1: the stat band ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Six cells over the painted strip, in Reign's widths — the first lands on the red heraldic
        /// shield the artwork already has waiting for it, which is why the geometry is copied rather
        /// than re-invented.
        /// </summary>
        private FrameworkElement Band()
        {
            var band = new Grid
            {
                Width = BandContentW,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var w in new double[] { 80, 110, 90, 180, 170, 110 })
                band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            Put(band, 0, Cell("RANK", out _rank, 17));
            Put(band, 1, Cell("MMR", out _mmr, 17));
            Put(band, 2, Cell("TURN", out _turn, 17));
            var heroCell = Cell("HERO", out _hero, 18);
            Put(band, 3, heroCell);
            Put(band, 4, Cell("BIGGEST CREATURE", out _biggest, 17));
            Put(band, 5, Cell("DURATION", out _duration, 17));

            // The portrait comes down over this one cell and lands on its label. That is the
            // composition working, not failing — the name reads as the nameplate under the picture —
            // but a caption sliced in half by a silver frame is not. Hidden, not removed: the line
            // still holds its space, so the six values stay on one baseline.
            ((StackPanel)heroCell).Children[0].Visibility = Visibility.Hidden;

            _hero.Foreground = Gold;
            return band;
        }

        private static UIElement Cell(string label, out TextBlock value, double size)
        {
            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var lbl = Line(label, 9.5, Frozen(Color.FromRgb(0x9A, 0xA1, 0xA9)));
            lbl.FontWeight = FontWeights.SemiBold;
            lbl.TextAlignment = TextAlignment.Center;
            cell.Children.Add(lbl);

            value = Line("—", size, UiKit.TextPrimary);
            value.FontWeight = FontWeights.SemiBold;
            value.TextAlignment = TextAlignment.Center;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            cell.Children.Add(value);
            return cell;
        }

        // ── row 2: whichever view is open, then the footer ──────────────────────────────────────

        private FrameworkElement Body()
        {
            var rows = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };

            _boardView = new StackPanel();
            // A taunt frame draws past its minion's box — the shield's foot lands ~10px below the
            // row — and the footer sat 6px under it, so the date ran into the last minion (and the
            // name would run into the first). The clearance lives here, not on the footer: only
            // the board view has anything that overflows.
            var boardBox = new Grid { MinHeight = MinionSize, Margin = new Thickness(0, 0, 0, 20) };
            boardBox.Children.Add(_board);
            boardBox.Children.Add(_emptyBoard);
            _boardView.Children.Add(boardBox);
            rows.Children.Add(_boardView);

            // The stats table and the turn view scroll when the panel would otherwise be taller than
            // the screen: a 19-turn table at the panel's preferred scale does not fit a 1080p
            // client, and shrinking the whole panel to fit it made every number smaller for the
            // sake of the last rows. The host sets the limit (LimitHeight); unlimited by default,
            // which is what the PNG export wants.
            var scrolling = new StackPanel();
            scrolling.Children.Add(_stats.Root);
            scrolling.Children.Add(_turnView.Root);
            _scroll = new ScrollViewer
            {
                Content = scrolling,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,   // hit-testable, so the wheel reaches it
                Padding = new Thickness(0),
            };
            try { _scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = UiKit.ThinScrollBarStyle(4); } catch { }
            rows.Children.Add(_scroll);

            var footer = new Grid { Margin = new Thickness(4, 6, 4, 0) };
            footer.Children.Add(_playerName);
            footer.Children.Add(_playedAt);
            rows.Children.Add(footer);

            ApplyView();
            return rows;
        }

        // ── the corner strip: which view, and what to do with it ────────────────────────────────

        private UIElement Chrome()
        {
            _tabBoard = Tab("Board", false);
            _tabStats = Tab("Stats", true);

            // The strip carries its own dark ground. Sitting above the frame it has no panel behind
            // it any more, and the selected tab is a 17%-alpha gold tint meant to sit ON something --
            // over nothing it washed out and took the gold label with it.
            _chrome = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _chrome.Children.Add(_tabStats);
            _chrome.Children.Add(_tabBoard);
            _chrome.Children.Add(GlyphButton(CameraGlyph(), "Save a picture of this panel and copy it",
                                             UiKit.AccentBrush, () => ScreenshotRequested));
            _chrome.Children.Add(GlyphButton(CloseGlyph(), "Close",
                                             UiKit.Br(Color.FromRgb(0xFF, 0x6B, 0x6B)), () => CloseRequested));

            _chromeBar = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = UiKit.Br(UiKit.PanelBg),
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 2, 6),
                Child = _chrome,
            };
            return _chromeBar;
        }

        private Border Tab(string label, bool stats)
        {
            var b = new Border
            {
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Child = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false },
            };
            ((TextBlock)b.Child).Text = label;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;   // never starts a panel drag
            b.MouseLeftButtonUp += (s, e) =>
            {
                _showStats = _rememberedStats = stats;   // a real choice, so it is the one remembered
                _showTurn = false;
                ApplyView();
                e.Handled = true;
            };
            return b;
        }

        /// <param name="action">Read late: the host wires the callbacks after construction.</param>
        internal static Border GlyphButton(Shape glyph, string tooltip, Brush hover, Func<Action> action)
        {
            var rest = UiKit.TextMuted;
            glyph.Stroke = rest;
            var b = new Border
            {
                Width = 30,
                Height = 26,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = glyph,
            };
            UiKit.Tip(b, tooltip);
            b.MouseEnter += (s, e) => { glyph.Stroke = hover; b.Background = UiKit.Br(UiKit.PanelActive); };
            b.MouseLeave += (s, e) => { glyph.Stroke = rest; b.Background = Brushes.Transparent; };
            // Swallow the press as well as the release: the host starts a drag on MouseLeftButtonDown
            // anywhere on the panel, and a button that let it through would move the panel instead.
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try { action()?.Invoke(); } catch { }
            };
            return b;
        }

        private static Path CameraGlyph() => new Path
        {
            Data = Geometry.Parse("M1 6.2 A1.7 1.7 0 0 1 2.7 4.5 H5.2 L6.5 2.4 H11.5 L12.8 4.5 H15.3 "
                                + "A1.7 1.7 0 0 1 17 6.2 V13.6 A1.7 1.7 0 0 1 15.3 15.3 H2.7 "
                                + "A1.7 1.7 0 0 1 1 13.6 Z "
                                + "M9 12.4 A2.6 2.6 0 1 1 9 7.2 A2.6 2.6 0 1 1 9 12.4 Z"),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        private static Path CloseGlyph() => new Path
        {
            Data = Geometry.Parse("M2 2 L12 12 M12 2 L2 12"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform,
            Width = 11,
            Height = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        /// <summary>
        /// A view whose every block is switched off has no tab: an empty half is not a choice, and
        /// offering it is how a panel ends up looking broken. With one view left the switch itself
        /// goes too, and that view is the one shown regardless of what was picked last.
        /// </summary>
        private void ApplyView()
        {
            if (_boardView == null) return;

            bool stats = _options.AnyStats, board = _options.AnyBoard;
            if (!stats && board) _showStats = false;
            else if (stats && !board) _showStats = true;

            bool turn = _showTurn && _showStats && stats;

            Show(_tabStats, stats && board);
            Show(_tabBoard, stats && board);

            _boardView.Visibility = (!_showStats && board) ? Visibility.Visible : Visibility.Collapsed;
            _stats.Root.Visibility = (_showStats && stats && !turn) ? Visibility.Visible : Visibility.Collapsed;
            _turnView.Root.Visibility = turn ? Visibility.Visible : Visibility.Collapsed;
            // The band sits 30px above its row's floor (BandLift), and that empty strip is air the
            // turn view does not need: three boards with their headers are tall, so the sub-view
            // is pulled up into it (user, 2026-09-06). The lift is on the scroll viewer, not inside
            // it — content above a scroll viewer's origin is clipped, a child of the Grid drawn
            // over the band row is not. Hiding the header and painting for the sub-view was tried
            // the same day and turned down: the hero and Reign's art stay.
            if (_scroll != null) _scroll.Margin = turn ? new Thickness(0, -TurnViewLift, 0, 0) : new Thickness(0);
            Paint(_tabStats, _showStats);
            Paint(_tabBoard, !_showStats);
        }

        // ── the turn sub-view ───────────────────────────────────────────────────────────────────

        internal void OpenTurn(int index)
        {
            var s = _currentStats;
            if (s == null || s.Turns == null || index < 0 || index >= s.Turns.Count) return;
            _turnView.TurnShown = _options.TurnDetailRow ? (Action<TurnStat>)ShowTurnMedallions : null;
            _turnView.Hand = _options.TurnHand ? (Func<ShopSnap, MinionRecord, UIElement>)HandVisual : null;
            _turnView.Show(s, index, MinionVisual);
            _showStats = true;
            _showTurn = true;
            ApplyView();
        }

        /// <summary>
        /// One stored minion, drawn and made hoverable when it has enchantments to show.
        ///
        /// The view asks for this rather than building it, because resolving a card id to the words
        /// Hearthstone would print is a Database/HearthDb job and the view is deliberately free of
        /// both. A minion with nothing on it comes back plain and un-hoverable, so the panel never
        /// grows a hover target that opens an empty box.
        /// </summary>
        private UIElement MinionVisual(ShopSnap snap, MinionRecord m)
        {
            var ctl = new HdtControls.BattlegroundsMinion(EntityFor(m.CardId, m.Tags));
            ctl.IsHitTestVisible = false;   // HDT's control never takes input; the wrapper does
            var tip = FinalBoardTips.Enchants(NameOf(m.CardId), snap, FinalBoardTips.HostId(m));
            if (tip == null) return ctl;

            // Transparent, not null: a Border with no brush is not hit-testable, so there would be
            // nothing under the cursor to own the tooltip (same trap as the stats table's rows).
            // The press is deliberately NOT handled — it bubbles to the panel root, which is what
            // lets the player still grab the panel by its board.
            var hit = new Border { Background = Brushes.Transparent, Child = ctl };
            UiKit.Tip(hit, tip, _scale);
            return hit;
        }

        /// <summary>
        /// One card held in hand. A hand carries spells as well as minions, and they want different
        /// renderers: HDT's <c>BattlegroundsMinion</c> is what shows a minion's BUFFED stats and its
        /// golden frame — the whole reason a hand is worth drawing, since a Flighty Scout that sat
        /// there for four turns growing and then tripling is a story the board alone never tells —
        /// while a spell has no stats and wants its card face.
        /// </summary>
        private UIElement HandVisual(ShopSnap snap, MinionRecord m)
        {
            FrameworkElement ctl;
            if (TagOf(m, GameTag.CARDTYPE) == (int)CardType.MINION)
                ctl = new HdtControls.BattlegroundsMinion(EntityFor(m.CardId, m.Tags));
            else
            {
                var card = CardOf(m.CardId);
                if (card == null) return null;
                var img = new HdtControls.CardImage();
                img.SetCardIdFromCard(card, CardAssetType.FullImage);
                ctl = img;
            }
            ctl.IsHitTestVisible = false;

            var tip = FinalBoardTips.Enchants(NameOf(m.CardId), snap, FinalBoardTips.HostId(m));
            if (tip == null) return ctl;
            var hit = new Border { Background = Brushes.Transparent, Child = ctl };
            UiKit.Tip(hit, tip, _scale);
            return hit;
        }

        private static int TagOf(MinionRecord m, GameTag tag)
        {
            int v;
            return m?.Tags != null && m.Tags.TryGetValue((int)tag, out v) ? v : 0;
        }

        /// <summary>
        /// Re-point the header's medallions at one turn: what the hero power and the trinkets were
        /// doing THEN, on the pieces the composition already draws beside the portrait.
        ///
        /// Drawing a second set per turn was the obvious thing and the wrong one — the medallions
        /// are right there, and the sub-view's scarcest resource is height. The cost is that the
        /// header stops being purely end-of-match while a turn is open: a trinket bought on turn 10
        /// is faded on turn 3 and says so. That is the timeline the panel could not show before.
        /// </summary>
        private void ShowTurnMedallions(TurnStat t)
        {
            if (t == null || _currentRec == null) return;
            FillMedallions(_currentRec, t);
        }

        /// <summary>The first of A/B/C that has the thing. Absence at one moment is a failed read,
        /// not an absent trinket, so this asks the next moment rather than reporting nothing.</summary>
        private static T First<T>(TurnStat t, Func<ShopSnap, T> pick) where T : class
        {
            foreach (var s in new[] { t.SnapStart, t.SnapEnd, t.SnapPreCombat })
            {
                if (s == null) continue;
                var v = pick(s);
                if (v != null) return v;
            }
            return null;
        }

        private static List<MinionRecord> TrinketsAt(TurnStat t) =>
            First(t, s => s.Trinkets != null && s.Trinkets.Count > 0 ? s.Trinkets : null);

        /// <summary>The first turn on which a trinket is in the record's hands, or 0 when the turn
        /// series never mentions it — which is what an old record without per-turn trinkets looks
        /// like, and is why the tooltip then says nothing about when it arrived.</summary>
        private int AcquiredOn(string cardId)
        {
            var turns = _currentStats?.Turns;
            if (turns == null || string.IsNullOrEmpty(cardId)) return 0;
            foreach (var t in turns)
            {
                var held = TrinketsAt(t);
                if (held == null) continue;
                foreach (var tr in held)
                    if (tr != null && string.Equals(tr.CardId, cardId, StringComparison.Ordinal)) return t.Turn;
            }
            return 0;
        }

        /// <summary>Place one of HDT's clipping controls and give it its tooltip. The Viewbox is
        /// what makes Trinket/HeroPower scale instead of crop (see <see cref="Add"/>); a faded one
        /// is something the player did not have yet, and still says so on hover.</summary>
        private void Hover(Panel host, FrameworkElement control, double w, double h,
                           Thickness margin, FrameworkElement tip, bool owned)
        {
            control.IsHitTestVisible = false;
            var box = new Viewbox { Width = w, Height = h, Stretch = Stretch.Uniform, Margin = margin, Child = control };
            if (!owned) box.Opacity = NotYetOwned;
            if (tip == null) { box.IsHitTestVisible = false; host.Children.Add(box); return; }
            var hit = new Border { Background = Brushes.Transparent, Child = box };
            UiKit.Tip(hit, tip, _scale);
            host.Children.Add(hit);
        }

        /// <summary>A stored card's display name for a tooltip header, with the golden fallback the
        /// rest of the plugin uses: a tripled minion's <c>_G</c> id has no record of its own, so the
        /// base card supplies the name.</summary>
        private static string NameOf(string cardId)
        {
            var n = HsbgCardLookup.Data.CardText.Name(cardId);
            if (!string.Equals(n, cardId, StringComparison.Ordinal)) return n;
            var baseId = HsbgCardLookup.Data.CardText.StripGold(cardId);
            return string.Equals(baseId, cardId, StringComparison.Ordinal)
                ? n
                : HsbgCardLookup.Data.CardText.Name(baseId);
        }

        /// <summary>Step out of the turn sub-view. False when there was nothing to step out of —
        /// which is how the host knows an Esc should close the panel instead.</summary>
        public bool Back()
        {
            if (!_showTurn) return false;
            _showTurn = false;
            // The header's medallions were showing one turn's state; the match owns them again.
            if (_currentRec != null) FillMedallions(_currentRec, null);
            ApplyView();
            return true;
        }

        private static void Show(UIElement el, bool on)
        {
            if (el != null) el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void Paint(Border tab, bool selected)
        {
            if (tab == null) return;
            tab.Background = selected ? UiKit.Br(UiKit.PanelActive) : Brushes.Transparent;
            tab.BorderBrush = selected ? UiKit.AccentBrush : UiKit.StrokeBrush;
            tab.BorderThickness = new Thickness(1);
            var tb = tab.Child as TextBlock;
            if (tb != null) tb.Foreground = selected ? UiKit.AccentBrush : UiKit.TextMuted;
        }

        // ── content ─────────────────────────────────────────────────────────────────────────────

        public void Show(FinalBoardRecord rec)
        {
            if (rec == null) return;

            string hero = rec.HeroName;
            if (string.IsNullOrEmpty(hero)) hero = FinalBoardCapture.HeroNameOf(rec.HeroCardId);

            SetCell(_rank, Ordinal(rec.Placement), PlacementBrush(rec), true);
            SetCell(_hero, hero, Gold, true);
            SetCell(_turn, rec.Turns > 0 ? rec.Turns.ToString(CultureInfo.InvariantCulture) : null,
                    UiKit.TextPrimary, _options.MatchMeta);
            SetCell(_duration, Duration(rec), UiKit.TextPrimary, _options.MatchMeta);
            SetCell(_biggest, Biggest(rec), UiKit.TextPrimary, true);

            var delta = rec.MmrDelta;
            bool showMmr = _options.MmrDelta && delta.HasValue && !rec.FriendlyGame;
            SetCell(_mmr,
                    showMmr ? (rec.SeasonReset ? "" : (delta.Value > 0 ? "+" : "")) + delta.Value.ToString(CultureInfo.InvariantCulture) : null,
                    showMmr ? (delta.Value > 0 ? Green : (delta.Value < 0 ? Red : UiKit.TextSecondary)) : UiKit.TextPrimary,
                    showMmr);

            _playerName.Text = rec.PlayerName ?? "";
            Show(_playerName, _options.PlayerName && !string.IsNullOrEmpty(_playerName.Text));
            _playedAt.Text = Timestamp(rec) ?? "";
            Show(_playedAt, _options.MatchMeta && !string.IsNullOrEmpty(_playedAt.Text));

            // Setting CardId alone paints NOTHING: CardImage only fetches through
            // SetCardIdFromCard, which runs HDT's async asset downloader and then assigns
            // CardAsset. Verified by decompiling the control after a first render came out blank.
            var heroCard = CardOf(rec.HeroCardId);
            _portrait.SetCardIdFromCard(heroCard, CardAssetType.Hero);
            Show(_portrait, heroCard != null && _options.HeroPortrait);

            FillMedallions(rec, null);

            _board.Children.Clear();
            foreach (var m in rec.Board ?? new List<MinionRecord>())
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                Add(_board, new HdtControls.BattlegroundsMinion(EntityFor(m.CardId, m.Tags)),
                    MinionSize, MinionSize, new Thickness(MinionOverlap, 0, MinionOverlap, 0));
            }
            _emptyBoard.Visibility = _board.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var biggest = rec.Stats != null ? CardOf(rec.Stats.HighestMinionCardId) : null;
            _currentRec = rec;
            _currentStats = rec.Stats;
            _showTurn = false;
            _stats.Show(rec.Stats, biggest != null ? biggest.Name : null, _options);
            ApplyView();
        }

        /// <summary>
        /// The medallions either side of the portrait: trinkets left, hero power and the anomaly
        /// (or the Dark Gift mark) right.
        ///
        /// With <paramref name="turn"/> null this is the match — what the player finished holding.
        /// With a turn it is that moment instead: each trinket carries the text the shop would have
        /// shown then, a trinket bought later is faded and says which turn it arrived on, and the
        /// hero power is the one that was actually in play. That last part matters more than it
        /// sounds: a hero power can be SPENT mid-match (Growing Collection is consumed on turn 8
        /// and the end-of-match record correctly has none), so the match's own slot is empty for
        /// the whole run while turns 1-8 each had one to show.
        ///
        /// The anomaly slot never varies by turn — it is a fact about the lobby.
        /// </summary>
        private void FillMedallions(FinalBoardRecord rec, TurnStat turn)
        {
            _trinkets.Children.Clear();
            _powers.Children.Clear();
            if (!_options.DetailRow) return;

            var held = turn != null ? TrinketsAt(turn) : null;
            foreach (var t in Medallions(rec, held))
            {
                if (t == null || string.IsNullOrEmpty(t.CardId)) continue;
                var now = Held(held, t.CardId);
                bool owned = turn == null || now != null;
                var tip = turn == null
                    ? FinalBoardTips.Trinket(t)
                    : (now != null ? FinalBoardTips.Trinket(now) : FinalBoardTips.Unowned(t.CardId, AcquiredOn(t.CardId)));
                // Negative, and it has to be: a third of the 135 box is the transparent margin
                // HDT leaves around the ring, so a positive gap between the boxes reads as a
                // large one between the medallions. −8 a side leaves about 15px of real air.
                Hover(_trinkets, new HdtControls.Trinket(EntityFor(t.CardId, t.Tags)),
                      TrinketSize, TrinketSize, new Thickness(-8, 0, -8, 0), tip, owned);
            }

            var power = turn != null ? First(turn, s => s.HeroPower) : null;
            if (power != null)
                Hover(_powers, new HdtControls.HeroPower(EntityFor(power.CardId, power.Tags)),
                      HeroPowerSize, HeroPowerSize, new Thickness(0), FinalBoardTips.Power(power), true);
            else if (!string.IsNullOrEmpty(rec.HeroPowerCardId))
                Hover(_powers, new HdtControls.HeroPower(EntityFor(rec.HeroPowerCardId, null)),
                      HeroPowerSize, HeroPowerSize, new Thickness(0),
                      turn == null ? FinalBoardTips.Power(rec.HeroPowerCardId) : FinalBoardTips.Spent(rec.HeroPowerCardId),
                      turn == null);

            var anomaly = CardOf(rec.AnomalyCardId);
            if (anomaly != null)
            {
                var img = new HdtControls.CardImage();
                img.SetCardIdFromCard(anomaly, CardAssetType.FullImage);
                Hover(_powers, img, AnomalyW, AnomalyH, new Thickness(6, 0, 0, 0),
                      FinalBoardTips.Anomaly(rec.AnomalyCardId), true);
            }
            else
            {
                // A Dark Gift lobby leaves this slot empty: the mechanic is a button entity, not
                // an anomaly, so there is no card for HDT to draw and the board came out with a
                // blank beside the hero power. The medallion goes there — it is the one place in
                // the composition that says what kind of lobby this was, which is what tells two
                // seasons apart. A real anomaly still wins the slot: that is a fact about the
                // match, and the Dark Gift is the season it was played in.
                var mark = rec.DarkGiftLobby ? LoadDarkGiftMark() : null;
                if (mark != null)
                    // The one medallion that is not a card, so it is the one that most needs saying
                    // out loud: nothing else on the panel explains what the mark stands for.
                    Hover(_powers, new Image { Source = mark }, DarkGiftMarkSize, DarkGiftMarkSize,
                          new Thickness(6, 0, 0, 0), FinalBoardTips.DarkGift(), true);
            }
        }

        /// <summary>Which trinket slots to draw: the match's, in its order, plus anything the turn
        /// holds that the match's list does not mention. The extra case is not hypothetical — a
        /// Greater trinket can replace a lesser one, so a turn can hold a medallion the final
        /// record never shows.</summary>
        private static IEnumerable<MinionRecord> Medallions(FinalBoardRecord rec, List<MinionRecord> held)
        {
            var outp = new List<MinionRecord>(rec.Trinkets ?? new List<MinionRecord>());
            if (held != null)
                foreach (var h in held)
                    if (h != null && !string.IsNullOrEmpty(h.CardId) && Held(outp, h.CardId) == null)
                        outp.Add(h);
            return outp;
        }

        private static MinionRecord Held(List<MinionRecord> list, string cardId)
        {
            if (list == null) return null;
            foreach (var m in list)
                if (m != null && string.Equals(m.CardId, cardId, StringComparison.Ordinal)) return m;
            return null;
        }

        /// <summary>A cell with nothing to say shows an em dash rather than an empty gap: the band is
        /// a fixed set of facts about the match, and a blank one reads as a rendering fault.</summary>
        private static void SetCell(TextBlock cell, string text, Brush ink, bool on)
        {
            if (cell == null) return;
            bool has = on && !string.IsNullOrEmpty(text);
            cell.Text = has ? text : "—";
            cell.Foreground = has ? ink : Muted;
        }

        private static string Biggest(FinalBoardRecord rec)
        {
            var s = rec.Stats;
            if (s == null || (s.HighestMinionAttack <= 0 && s.HighestMinionHealth <= 0)) return null;
            return s.HighestMinionAttack + " / " + s.HighestMinionHealth;
        }

        /// <summary>
        /// Place one of HDT's controls at OUR size, through a Viewbox rather than by assigning
        /// Width/Height.
        ///
        /// Measured 2026-08-28: Trinket and HeroPower have a natural size of 110x110 and their
        /// DesiredSize does follow an assigned Width/Height — but their templates CLIP their content
        /// instead of scaling it, so at 74 a third of the medallion was simply cut off.
        /// BattlegroundsMinion is natural 256 and does scale, which is why the board always looked
        /// right and only the detail row did not.
        ///
        /// A Viewbox renders the control at whatever size it wants and scales the RESULT, so it is
        /// correct for all three — and for a non-square child like the anomaly card it letterboxes
        /// rather than squashing.
        /// </summary>
        private static void Add(Panel host, UIElement child, double w, double h, Thickness margin)
        {
            var fe = child as FrameworkElement;
            if (fe != null) fe.IsHitTestVisible = false;   // the panel as a whole handles input, never its parts
            host.Children.Add(new Viewbox
            {
                Width = w,
                Height = h,
                Stretch = Stretch.Uniform,
                Margin = margin,
                IsHitTestVisible = false,
                Child = child,
            });
        }

        // ── the artwork ─────────────────────────────────────────────────────────────────────────

        private static BitmapSource _art;
        private static bool _artTried;
        private static BitmapSource _darkGiftMark;
        private static bool _darkGiftMarkTried;

        /// <summary>Loaded once per process, and a failure is remembered: a missing file must cost one
        /// attempt, not one per panel. The panel falls back to a flat gradient and still works.</summary>
        private static BitmapSource LoadArt()
        {
            if (_artTried) return _art;
            _artTried = true;
            try
            {
                var dir = IoPath.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var file = IoPath.Combine(dir, "data", "panel", "FinalBoardBackground.png");
                if (System.IO.File.Exists(file)) _art = ImageCache.Load(file, (int)PanelW);
            }
            catch { _art = null; }
            return _art;
        }


        /// <summary>The Dark Gift medallion, loaded once on the same terms as the panel artwork.
        /// Reign's, converted from his WebP; a bigger source can replace the file alone.</summary>
        private static BitmapSource LoadDarkGiftMark()
        {
            if (_darkGiftMarkTried) return _darkGiftMark;
            _darkGiftMarkTried = true;
            try
            {
                var dir = IoPath.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var file = IoPath.Combine(dir, "data", "panel", "DarkGiftMark.png");
                if (System.IO.File.Exists(file)) _darkGiftMark = ImageCache.Load(file, (int)DarkGiftMarkSize * 2);
            }
            catch { _darkGiftMark = null; }
            return _darkGiftMark;
        }

        /// <summary>
        /// The artwork, undistorted, plus whatever ground is needed under it. The image rectangle is
        /// exactly 920x410 so Stretch.Fill is a 1:1 blit; behind it a second rectangle carries the
        /// painting's own last two rows, which are flat, so a panel of any height ends on the colour
        /// the artist ended on. Returns an empty Grid when the file is missing — the caller has
        /// already put a gradient behind us for that case.
        /// </summary>
        private static FrameworkElement Background(BitmapSource art)
        {
            var bg = new Grid { IsHitTestVisible = false };
            if (art == null) return bg;

            bg.Children.Add(new Rectangle
            {
                IsHitTestVisible = false,
                Fill = new ImageBrush(art)
                {
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect(0, (ArtH - 2) / ArtH, 1, 2 / ArtH),
                    Stretch = Stretch.Fill,
                },
            });
            bg.Children.Add(new Rectangle
            {
                Height = ArtH,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Fill = new ImageBrush(art) { Stretch = Stretch.Fill },
            });
            return bg;
        }

        /// <summary>
        /// Rehydrate a stored minion into the entity HDT's controls expect. This mirrors what HDT
        /// does when it rebuilds a board from its own XML store (BattlegroundsGameViewModel), so the
        /// tag ints we persisted map straight back onto GameTags.
        /// </summary>
        private static Entity EntityFor(string cardId, Dictionary<int, int> tags)
        {
            var dict = new Dictionary<GameTag, int>();
            if (tags != null)
                foreach (var kv in tags) dict[(GameTag)kv.Key] = kv.Value;
            return new Entity { CardId = cardId, Tags = dict };
        }

        private static Hearthstone_Deck_Tracker.Hearthstone.Card CardOf(string cardId)
        {
            try { return string.IsNullOrEmpty(cardId) ? null : Database.GetCardFromId(cardId); }
            catch { return null; }
        }

        /// <summary>
        /// How long the match ran, "m:ss", or null when the record's two timestamps cannot say —
        /// which is why the caller gets a null rather than a "0:00" that would read as a real answer.
        /// Shared with the settings-window history list so both spell a match length the same way.
        /// </summary>
        internal static string Duration(FinalBoardRecord rec)
        {
            DateTime a, b;
            if (rec == null || !DateTime.TryParse(rec.StartedAt, out a) || !DateTime.TryParse(rec.EndedAt, out b)) return null;
            var d = b - a;
            if (d <= TimeSpan.Zero || d > TimeSpan.FromHours(4)) return null;
            return ((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00");
        }

        /// <summary>When it was played, local time, "yyyy.MM.dd HH:mm". Null when unknown.</summary>
        internal static string Timestamp(FinalBoardRecord rec)
        {
            var started = rec?.StartedAtUtc;
            if (!started.HasValue) return null;
            return started.Value.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture);
        }

        internal static string Ordinal(int n)
        {
            if (n <= 0) return "";
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }

        /// <summary>
        /// What the finish was worth — and the rating change is the thing that knows, so it decides.
        /// A gain is green, a loss is red, and a finish that moved nothing is amber. First place is
        /// green whatever the lobby paid, because a win is a win.
        ///
        /// Gold used to mark 1st and it was the wrong colour for it: amber/gold is what a result
        /// reads as when it neither gained nor lost you anything, which is the opposite of a win.
        ///
        /// Falls back to position when there is no rating to read — an import, a friendly game, a
        /// season reset (where the number is the new rating, not a difference), or a match that
        /// ended before HDT resolved one. Top half is good; duos has four teams, so its line is 2nd.
        /// </summary>
        private static Brush PlacementBrush(FinalBoardRecord rec)
        {
            int placement = rec.Placement;
            if (placement <= 0) return UiKit.TextSecondary;
            if (placement == 1) return Green;

            var delta = rec.MmrDelta;
            if (delta.HasValue && !rec.FriendlyGame && !rec.SeasonReset)
                return delta.Value > 0 ? Green : (delta.Value < 0 ? Red : Amber);

            bool good = rec.Duos ? placement <= 2 : placement <= 4;
            return good ? Green : Red;
        }

        private static void Put(Grid g, int col, UIElement child)
        {
            Grid.SetColumn(child, col);
            g.Children.Add(child);
        }

        private static readonly Brush Green = Frozen(Color.FromRgb(0x6D, 0xEB, 0x6C));
        private static readonly Brush Red = Frozen(Color.FromRgb(0xEC, 0x69, 0x69));
        private static readonly Brush Gold = Frozen(Color.FromRgb(0xDA, 0xB8, 0x6C));
        private static readonly Brush Amber = Frozen(Color.FromRgb(0xF0, 0xB0, 0x4A));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x68, 0x6D, 0x74));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static TextBlock Line(string text, double size, Brush brush) => new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }
}
