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
    /// anomaly right, then a band reading rank · MMR · turn · hero · highest creature · duration.
    /// He is a co-author of this feature. The layout below is his; what differs is that the artwork
    /// is <b>sliced</b> instead of stretched (see <see cref="Slice"/>), because his panel is one
    /// fixed height and ours has to hold either a board or a tall stats table.
    ///
    /// Layout is authored in reference pixels and scaled as a whole (<see cref="Scale"/>), the way
    /// every other canvas element in this codebase works.
    /// </summary>
    internal sealed class FinalBoardPanel
    {
        // ── the artwork, and the rows measured out of it ────────────────────────────────────────
        // Measured off the PNG itself rather than guessed: the gold rail occupies y20–34 and the
        // band's two gold rules sit at y155 and y217. Those rules are the seams, so they are where
        // the image is cut.
        private const double PanelW = 920;
        private const double ArtH = 410;

        // The header is cut in TWO so the row can be taller than the artwork's own header zone
        // without the rail growing with it: the rail keeps its painted height, and only the flat
        // purple beneath it stretches. Reign's header is 204 tall and his portrait is 268 — it is
        // meant to break the rail and overhang the band, and it cannot do either from a 155 row.
        private const double RailH = 60;       // art rows 0-59: the gold rail and its scroll ends
        private const double RailArtEnd = 60;
        private const double HeaderFillTop = 60;   // art rows 60-154: flat, safe to stretch
        private const double HeaderFillArtH = 95;
        private const double HeaderH = 204;    // total header height, Reign's
        private const double BandTop = 155;
        private const double BandH = 63;       // the stat strip, between the two gold rules
        private const double BodyTop = 218;    // flat gradient below — the only stretchable part

        // Reign's sizes, and they matter: HDT's Trinket/HeroPower are drawn for 110x110.
        private const double MinionSize = 134;
        private const double MinionOverlap = -5;
        private const double PortraitW = 190, PortraitH = 268;
        private const double TrinketSize = 100;
        private const double HeroPowerSize = 130;
        private const double AnomalyW = 90, AnomalyH = 130;

        private const double DetailsW = 600;   // 3 columns of 200, centred
        private const double BandContentW = 740;

        private readonly Border _root;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);

        private readonly StackPanel _trinkets, _board, _powers;
        private readonly HdtControls.CardImage _portrait;
        private readonly TextBlock _emptyBoard;
        private readonly TextBlock _playerName, _playedAt;

        private TextBlock _rank, _mmr, _turn, _hero, _highest, _duration;

        private readonly FinalBoardStatsView _stats = new FinalBoardStatsView();
        private readonly FinalBoardOptions _options;
        private StackPanel _boardView;
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

        /// <param name="options">
        /// What to draw. Null means everything, which is what the PNG export and any caller with no
        /// config wants — a record that has been filtered down on screen is still whole on disk.
        /// </param>
        public FinalBoardPanel(FinalBoardOptions options = null)
        {
            _options = options ?? new FinalBoardOptions();

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
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RailH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH - RailH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BandH) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Backgrounds first, each its own slice. The rail and the band are drawn at the height
            // they were painted at, so their ornament is pixel-exact; the two flat zones are the only
            // ones that stretch, and they have nothing in them to distort.
            AddArt(grid, 0, Slice(art, 0, RailArtEnd));
            AddArt(grid, 1, Slice(art, HeaderFillTop, HeaderFillArtH));
            AddArt(grid, 2, Slice(art, BandTop, BandH));
            AddArt(grid, 3, Slice(art, BodyTop, ArtH - BodyTop));

            // The details span both header rows and are allowed to overflow: the portrait is taller
            // than the header, breaks the rail above it and rests on the band below, which is the
            // whole reason the composition reads as a hero and not as a row of icons.
            var details = Details();
            Grid.SetRow(details, 0);
            Grid.SetRowSpan(details, 2);
            Panel.SetZIndex(details, 10);
            grid.Children.Add(details);

            var band = Band();
            Grid.SetRow(band, 2);
            Panel.SetZIndex(band, 5);
            grid.Children.Add(band);

            var body = Body();
            Grid.SetRow(body, 3);
            Panel.SetZIndex(body, 5);
            grid.Children.Add(body);

            return new Border
            {
                Width = PanelW,
                BorderBrush = Frozen(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = art == null ? (Brush)new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90) : Brushes.Transparent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Child = grid,
            };
        }

        // ── row 0: trinkets · hero · hero power + anomaly ───────────────────────────────────────

        private UIElement Details()
        {
            var details = new Grid
            {
                Width = DetailsW,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            for (int i = 0; i < 3; i++)
                details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DetailsW / 3) });

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
        private UIElement Band()
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
            Put(band, 3, Cell("HERO", out _hero, 18));
            Put(band, 4, Cell("HIGHEST CREATURE", out _highest, 17));
            Put(band, 5, Cell("DURATION", out _duration, 17));

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

        private UIElement Body()
        {
            var rows = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };

            _boardView = new StackPanel();
            var boardBox = new Grid { MinHeight = MinionSize };
            boardBox.Children.Add(_board);
            boardBox.Children.Add(_emptyBoard);
            _boardView.Children.Add(boardBox);
            rows.Children.Add(_boardView);

            rows.Children.Add(_stats.Root);

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
                ApplyView();
                e.Handled = true;
            };
            return b;
        }

        /// <param name="action">Read late: the host wires the callbacks after construction.</param>
        private static Border GlyphButton(Shape glyph, string tooltip, Brush hover, Func<Action> action)
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
                ToolTip = tooltip,
                Child = glyph,
            };
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

            Show(_tabStats, stats && board);
            Show(_tabBoard, stats && board);

            _boardView.Visibility = (!_showStats && board) ? Visibility.Visible : Visibility.Collapsed;
            _stats.Root.Visibility = (_showStats && stats) ? Visibility.Visible : Visibility.Collapsed;
            Paint(_tabStats, _showStats);
            Paint(_tabBoard, !_showStats);
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

            SetCell(_rank, Ordinal(rec.Placement), PlacementBrush(rec.Placement, rec.Duos), true);
            SetCell(_hero, hero, Gold, true);
            SetCell(_turn, rec.Turns > 0 ? rec.Turns.ToString(CultureInfo.InvariantCulture) : null,
                    UiKit.TextPrimary, _options.MatchMeta);
            SetCell(_duration, Duration(rec), UiKit.TextPrimary, _options.MatchMeta);
            SetCell(_highest, Highest(rec), UiKit.TextPrimary, true);

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

            _trinkets.Children.Clear();
            _powers.Children.Clear();
            if (_options.DetailRow)
            {
                foreach (var t in rec.Trinkets ?? new List<MinionRecord>())
                {
                    if (t == null || string.IsNullOrEmpty(t.CardId)) continue;
                    Add(_trinkets, new HdtControls.Trinket(EntityFor(t.CardId, t.Tags)),
                        TrinketSize, TrinketSize, new Thickness(2, 0, 2, 0));
                }
                if (!string.IsNullOrEmpty(rec.HeroPowerCardId))
                    Add(_powers, new HdtControls.HeroPower(EntityFor(rec.HeroPowerCardId, null)),
                        HeroPowerSize, HeroPowerSize, new Thickness(0));
                var anomaly = CardOf(rec.AnomalyCardId);
                if (anomaly != null)
                {
                    var img = new HdtControls.CardImage();
                    img.SetCardIdFromCard(anomaly, CardAssetType.FullImage);
                    Add(_powers, img, AnomalyW, AnomalyH, new Thickness(6, 0, 0, 0));
                }
            }

            _board.Children.Clear();
            foreach (var m in rec.Board ?? new List<MinionRecord>())
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                Add(_board, new HdtControls.BattlegroundsMinion(EntityFor(m.CardId, m.Tags)),
                    MinionSize, MinionSize, new Thickness(MinionOverlap, 0, MinionOverlap, 0));
            }
            _emptyBoard.Visibility = _board.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var biggest = rec.Stats != null ? CardOf(rec.Stats.HighestMinionCardId) : null;
            _stats.Show(rec.Stats, biggest != null ? biggest.Name : null, _options);
            ApplyView();
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

        private static string Highest(FinalBoardRecord rec)
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

        /// <summary>
        /// One horizontal band of the artwork, as a brush. The ImageBrush Viewbox selects the source
        /// rows and Fill paints them across the target, so a row drawn at its painted height is
        /// untouched and only the row we deliberately stretch is stretched.
        /// </summary>
        private static Brush Slice(BitmapSource art, double top, double height)
        {
            if (art == null) return null;
            return new ImageBrush(art)
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(0, top / ArtH, 1, height / ArtH),
                Stretch = Stretch.Fill,
            };
        }

        private static void AddArt(Grid grid, int row, Brush brush)
        {
            if (brush == null) return;
            var r = new Rectangle { Fill = brush, IsHitTestVisible = false };
            Grid.SetRow(r, row);
            Panel.SetZIndex(r, 0);
            grid.Children.Add(r);
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

        // Top half of the lobby reads as a good result; duos has four teams, so the line sits at 2nd.
        private static Brush PlacementBrush(int placement, bool duos)
        {
            if (placement <= 0) return UiKit.TextSecondary;
            bool good = duos ? placement <= 2 : placement <= 4;
            return good ? (placement == 1 ? UiKit.AccentBrush : Green) : Red;
        }

        private static void Put(Grid g, int col, UIElement child)
        {
            Grid.SetColumn(child, col);
            g.Children.Add(child);
        }

        private static readonly Brush Green = Frozen(Color.FromRgb(0x6D, 0xEB, 0x6C));
        private static readonly Brush Red = Frozen(Color.FromRgb(0xEC, 0x69, 0x69));
        private static readonly Brush Gold = Frozen(Color.FromRgb(0xDA, 0xB8, 0x6C));
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
