using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HearthDb.Enums;
using HdtControls = Hearthstone_Deck_Tracker.Controls;
using Hearthstone_Deck_Tracker.Hearthstone;            // Database
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Utility.Assets;         // CardAssetType
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// Renders one <see cref="FinalBoardRecord"/> — the end-of-match warband, who played it and how
    /// it went. THE single renderer: every surface (the session-row click, the post-match screen,
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
    /// Layout is authored in reference pixels and scaled as a whole (<see cref="Scale"/>), the way
    /// every other canvas element in this codebase works.
    /// </summary>
    internal sealed class FinalBoardPanel
    {
        // Reference geometry, 1920x1080 basis.
        private const double PanelW = 920;
        private const double MinionSize = 126;
        private const double MinionOverlap = -4;   // 7 of them have to fit the width
        private const double PortraitH = 74;
        private const double TrinketSize = 74;
        private const double HeroPowerSize = 74;

        private readonly Border _root;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);

        private readonly TextBlock _placement, _heroName, _mmr, _meta;
        private readonly StackPanel _trinkets, _board, _powers;
        private readonly HdtControls.CardImage _portrait;
        private readonly TextBlock _emptyBoard;
        private Grid _detailRow;
        private Border _detailDivider;
        private readonly TextBlock _playerName;

        public FinalBoardPanel()
        {
            _placement = Text(30, FontWeights.Bold, UiKit.AccentBrush);
            _heroName = Text(19, FontWeights.SemiBold, UiKit.TextPrimary);
            _mmr = Text(19, FontWeights.SemiBold, UiKit.TextSecondary);
            _meta = Text(14, FontWeights.Normal, UiKit.TextMuted);
            _playerName = Text(13, FontWeights.SemiBold, UiKit.TextMuted);

            _trinkets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _powers = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _board = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _portrait = new HdtControls.CardImage
            {
                Height = PortraitH,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };

            _emptyBoard = Text(16, FontWeights.Normal, UiKit.TextMuted);
            _emptyBoard.Text = "No minions on the final board";
            _emptyBoard.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyBoard.Visibility = Visibility.Collapsed;

            _root = new Border
            {
                Width = PanelW,
                Background = new LinearGradientBrush(UiKit.PanelBg2, UiKit.PanelBg, 90),
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
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

        private UIElement BuildBody()
        {
            var rows = new StackPanel();

            // ── header: placement, hero, rating change, and the run's shape ─────────────────────
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(_portrait);
            header.Children.Add(_placement);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            names.Children.Add(_heroName);
            names.Children.Add(_meta);
            header.Children.Add(names);
            _mmr.VerticalAlignment = VerticalAlignment.Center;
            _mmr.HorizontalAlignment = HorizontalAlignment.Right;
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(header);
            Grid.SetColumn(_mmr, 1);
            headerGrid.Children.Add(_mmr);
            rows.Children.Add(headerGrid);

            // ── detail: trinkets left, hero centred, hero power + anomaly right ────────────────
            _detailDivider = Divider();

            var detail = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            _detailRow = detail;
            detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _trinkets.HorizontalAlignment = HorizontalAlignment.Left;
            detail.Children.Add(_trinkets);

            _powers.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_powers, 1);
            detail.Children.Add(_powers);
            rows.Children.Add(_detailDivider);
            rows.Children.Add(detail);

            rows.Children.Add(Divider());

            // ── the warband itself ─────────────────────────────────────────────────────────────
            var boardBox = new Grid { Margin = new Thickness(0, 10, 0, 4), MinHeight = MinionSize };
            boardBox.Children.Add(_board);
            boardBox.Children.Add(_emptyBoard);
            rows.Children.Add(boardBox);

            _playerName.HorizontalAlignment = HorizontalAlignment.Left;
            rows.Children.Add(_playerName);
            return rows;
        }

        private static Border Divider() => new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x39, 0x47, 0x5E)),
        };

        // ── content ─────────────────────────────────────────────────────────────────────────────
        public void Show(FinalBoardRecord rec)
        {
            if (rec == null) return;

            _placement.Text = Ordinal(rec.Placement);
            _placement.Foreground = PlacementBrush(rec.Placement, rec.Duos);
            _heroName.Text = rec.HeroName ?? FinalBoardCapture.HeroNameOf(rec.HeroCardId) ?? "";
            _meta.Text = MetaLine(rec);

            var delta = rec.MmrDelta;
            if (delta.HasValue && !rec.FriendlyGame)
            {
                _mmr.Text = (rec.SeasonReset ? "" : (delta.Value > 0 ? "+" : "")) + delta.Value.ToString(CultureInfo.InvariantCulture);
                _mmr.Foreground = delta.Value > 0 ? Green : (delta.Value < 0 ? Red : UiKit.TextSecondary);
            }
            else
            {
                _mmr.Text = "";
            }

            _playerName.Text = rec.PlayerName ?? "";

            // Setting CardId alone paints NOTHING: CardImage only fetches through
            // SetCardIdFromCard, which runs HDT's async asset downloader and then assigns
            // CardAsset. Verified by decompiling the control after a first render came out blank.
            var heroCard = CardOf(rec.HeroCardId);
            _portrait.SetCardIdFromCard(heroCard, CardAssetType.Hero);
            _portrait.Visibility = heroCard != null ? Visibility.Visible : Visibility.Collapsed;

            FillEntities(_trinkets, rec.Trinkets, TrinketSize, e => new HdtControls.Trinket(e));

            _powers.Children.Clear();
            if (!string.IsNullOrEmpty(rec.HeroPowerCardId))
                Add(_powers, new HdtControls.HeroPower(EntityFor(rec.HeroPowerCardId, null)), HeroPowerSize, new Thickness(6, 0, 0, 0));
            var anomaly = CardOf(rec.AnomalyCardId);
            if (anomaly != null)
            {
                var img = new HdtControls.CardImage();
                img.SetCardIdFromCard(anomaly, CardAssetType.FullImage);
                Add(_powers, img, HeroPowerSize, new Thickness(6, 0, 0, 0));
            }

            _board.Children.Clear();
            var board = rec.Board ?? new List<MinionRecord>();
            foreach (var m in board)
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                Add(_board, new HdtControls.BattlegroundsMinion(EntityFor(m.CardId, m.Tags)), MinionSize,
                    new Thickness(MinionOverlap, 0, MinionOverlap, 0));
            }
            _emptyBoard.Visibility = _board.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // A tier-A record has no hero power, trinkets or anomaly. Rather than leave a band of
            // empty space where they would go, the whole detail row stands down.
            bool anyDetail = _trinkets.Children.Count > 0 || _powers.Children.Count > 0;
            _detailRow.Visibility = anyDetail ? Visibility.Visible : Visibility.Collapsed;
            _detailDivider.Visibility = _detailRow.Visibility;
        }

        private static void FillEntities(Panel host, List<MinionRecord> items, double size, Func<Entity, UIElement> make)
        {
            host.Children.Clear();
            if (items == null) return;
            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.CardId)) continue;
                Add(host, make(EntityFor(it.CardId, it.Tags)), size, new Thickness(0, 0, 6, 0));
            }
        }

        private static void Add(Panel host, UIElement child, double size, Thickness margin)
        {
            var fe = child as FrameworkElement;
            if (fe != null)
            {
                fe.Width = size;
                fe.Height = size;
                fe.Margin = margin;
                fe.IsHitTestVisible = false;   // the panel as a whole handles input, never its parts
            }
            host.Children.Add(child);
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

        private static string MetaLine(FinalBoardRecord rec)
        {
            var bits = new List<string>();
            if (rec.Duos) bits.Add("Duos");
            if (rec.Turns > 0) bits.Add(rec.Turns + (rec.Turns == 1 ? " turn" : " turns"));
            var dur = Duration(rec);
            if (dur != null) bits.Add(dur);
            var started = rec.StartedAtUtc;
            if (started.HasValue) bits.Add(started.Value.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture));
            return string.Join("  ·  ", bits);
        }

        private static string Duration(FinalBoardRecord rec)
        {
            DateTime a, b;
            if (!DateTime.TryParse(rec.StartedAt, out a) || !DateTime.TryParse(rec.EndedAt, out b)) return null;
            var d = b - a;
            if (d <= TimeSpan.Zero || d > TimeSpan.FromHours(4)) return null;
            return ((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00");
        }

        private static string Ordinal(int n)
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

        private static readonly Brush Green = Freeze(Color.FromRgb(0x6D, 0xEB, 0x6C));
        private static readonly Brush Red = Freeze(Color.FromRgb(0xEC, 0x69, 0x69));

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static TextBlock Text(double size, FontWeight weight, Brush brush) => new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }
}
