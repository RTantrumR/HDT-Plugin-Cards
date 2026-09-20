using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// One shop turn, as three boards top to bottom: the shop as it opened (A), the shop as the
    /// player left it (B), and — only when the end-of-turn triggers changed anything — the board
    /// that went into combat (C). A sub-view of the stats table, reached by clicking a turn row;
    /// it has no tab of its own, only a way back, and arrows either side to step through the turns
    /// without going back to the table for each one.
    ///
    /// Like the stats view it draws no HDT control itself: the caller hands in the minion
    /// renderer, which keeps this file free of HDT's Database and lets a stored record render
    /// with no match running.
    /// </summary>
    internal sealed class FinalBoardTurnView
    {
        // The panel's 920 reference width less its padding, less the scroll bar the body shows on a
        // short screen — without that allowance the bar sat on the right arrow (seen live).
        private const double ContentW = 880;
        private const double ArrowW = 40;
        // Smaller than the final board's 134: three boards stack here, and seven at 134 would not
        // clear the arrows either side. Same −5 overlap as the final board — a wider gap was tried
        // and read worse (user, 2026-09-06). What the frames DO need room for is vertical: a taunt
        // frame, a divine shield and a golden frame all draw past the minion's box, top and
        // bottom, which is why the label above each board keeps its distance (HeadGap).
        private const double MinionSize = 110;
        private const double MinionGap = -5;
        // The hand shares the board's row. Positive gap, unlike the board's overlap: hand cards
        // carry no frame that draws past their box, so a negative one would just make them touch.
        private const double HandGap = 2;
        private const double DividerW = 24;
        // What the row has to fit into: the content width less both arrow columns, less a little
        // so a full row never sits flush against an arrow.
        private const double RowW = ContentW - 2 * ArrowW - 10;
        // Clearance, and it is not decoration. A taunt frame, a divine shield and a golden frame
        // all draw past a minion's box top and bottom, and the stat numbers sit on that overshoot,
        // so both gaps have to clear it or one row's numbers crowd the next row's cards. Raised
        // from 12/12 once the hand moved onto the board's own row and gave the height back.
        private const double HeadGap = 16;
        private const double BoardGap = 18;

        private readonly Grid _root = new Grid { Width = ContentW, HorizontalAlignment = HorizontalAlignment.Left };

        private MatchStats _stats;
        private int _index;
        private Func<ShopSnap, MinionRecord, UIElement> _minion;

        public FrameworkElement Root => _root;

        /// <summary>The back arrow was clicked. The view never hides itself — whoever hosts it owns that.</summary>
        public Action Back;

        /// <summary>Which turn is now on screen, raised on every render including an arrow step.
        /// The host re-points its header medallions at it.</summary>
        public Action<TurnStat> TurnShown;

        /// <summary>Draws one card held in hand, same contract as the minion renderer. Null leaves
        /// the hand out entirely.</summary>
        public Func<ShopSnap, MinionRecord, UIElement> Hand;

        /// <param name="index">Which of <see cref="MatchStats.Turns"/> to open on.</param>
        /// <param name="minion">Draws one stored minion — the caller's HDT control. It is handed the
        /// owning snapshot as well as the record, because what a minion is worth hovering for (its
        /// enchantments) lives on the snapshot, not on the minion.</param>
        public void Show(MatchStats s, int index, Func<ShopSnap, MinionRecord, UIElement> minion)
        {
            _stats = s;
            _index = index;
            _minion = minion;
            Render();
        }

        private void Render()
        {
            _root.Children.Clear();
            if (_stats == null || _stats.Turns == null || _index < 0 || _index >= _stats.Turns.Count) return;
            var t = _stats.Turns[_index];

            _fixed = Fixed.For(t);
            // What was POWERING this turn goes on the medallions the header already draws beside
            // the portrait, rather than on a strip of its own: three boards barely fit a 1080p
            // client, and a second row of trinkets was buying nothing the header could not carry.
            try { TurnShown?.Invoke(t); } catch { }

            var col = new StackPanel();
            col.Children.Add(TopBar(t));
            col.Children.Add(Body(t));
            _root.Children.Add(col);
        }

        /// <summary>
        /// Which of the turn's three numbers never moved, and therefore belong on the title line
        /// instead of on all three state headers.
        ///
        /// Height is the scarce thing in this view, and most of what a state header repeats is not
        /// news: health only changes in combat, so within a turn it is almost always one number
        /// written three times, and the tavern tier changes at most once. Saying it once at the top
        /// says the same thing and gives a board row back.
        /// </summary>
        private sealed class Fixed
        {
            public bool Health, Tier, Gold;   // true = constant across the turn, hoisted to the title
            public int HealthValue, TierValue, GoldValue;

            public static Fixed For(TurnStat t)
            {
                var f = new Fixed();
                var snaps = new List<ShopSnap>();
                foreach (var s in new[] { t.SnapStart, t.SnapEnd, t.SnapPreCombat }) if (s != null) snaps.Add(s);
                if (snaps.Count == 0) return f;

                f.HealthValue = snaps[0].HeroHp;
                f.TierValue = snaps[0].TavernTier;
                f.GoldValue = snaps[0].Gold;
                f.Health = f.Tier = f.Gold = true;
                foreach (var s in snaps)
                {
                    if (s.HeroHp != f.HealthValue) f.Health = false;
                    if (s.TavernTier != f.TierValue) f.Tier = false;
                    if (s.Gold != f.GoldValue) f.Gold = false;
                }
                return f;
            }
        }

        private Fixed _fixed = new Fixed();

        // ── the back arrow and the title ────────────────────────────────────────────────────────

        private UIElement TopBar(TurnStat t)
        {
            var g = ThreeColumns();
            g.Margin = new Thickness(0, 0, 0, 6);

            var back = FinalBoardPanel.GlyphButton(BackGlyph(), "Back to the stats", UiKit.AccentBrush, () => Back);
            back.HorizontalAlignment = HorizontalAlignment.Left;
            Put(g, 0, back);

            var title = new TextBlock
            {
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var name = Ink("Turn " + t.Turn.ToString(CultureInfo.InvariantCulture), UiKit.TextPrimary);
            name.FontWeight = FontWeights.SemiBold;
            title.Inlines.Add(name);
            title.Inlines.Add(Ink("  of " + _stats.Turns.Count.ToString(CultureInfo.InvariantCulture), UiKit.TextMuted));

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            row.Children.Add(title);
            // Whatever held still all turn is stated once, here, and left out of the state headers.
            if (_fixed.Gold) { row.Children.Add(Dot()); row.Children.Add(Num(_fixed.GoldValue, Gold)); row.Children.Add(Word("gold")); }
            if (_fixed.Health) { row.Children.Add(Dot()); row.Children.Add(Num(_fixed.HealthValue, Red)); row.Children.Add(Word("health")); }
            if (_fixed.Tier) { row.Children.Add(Dot()); AddTier(row, _fixed.TierValue); }
            Put(g, 1, row);
            return g;
        }

        // ── the three moments, with an arrow either side ────────────────────────────────────────

        private UIElement Body(TurnStat t)
        {
            var g = ThreeColumns();
            Put(g, 0, Arrow(ChevronGlyph(true), "Previous turn", -1));
            Put(g, 1, Snapshots(t));
            Put(g, 2, Arrow(ChevronGlyph(false), "Next turn", +1));
            return g;
        }

        /// <summary>An arrow with nowhere to go stays as a faint mark rather than disappearing: the
        /// layout does not shift between the first turn and the rest.</summary>
        private UIElement Arrow(Path glyph, string tip, int step)
        {
            int target = _index + step;
            bool can = target >= 0 && _stats.Turns != null && target < _stats.Turns.Count;
            if (!can)
            {
                glyph.Stroke = UiKit.TextMuted;
                glyph.Opacity = 0.3;
                return glyph;
            }
            var b = FinalBoardPanel.GlyphButton(glyph, tip, UiKit.AccentBrush,
                () => () => { _index = target; Render(); });
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Height = 40;
            return b;
        }

        /// <summary>
        /// When B and C read the same, the third block collapses to a line saying so: a third
        /// identical board would ask the reader to spot a difference that is not there. When they
        /// differ, the block's header names what moved, because a minion that grew from 413/423 to
        /// 538/537 is not something the eye finds between two boards on its own.
        /// </summary>
        private UIElement Snapshots(TurnStat t)
        {
            var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            box.Children.Add(Snapshot("Start of turn", t.SnapStart, opening: true));
            box.Children.Add(Snapshot("End of turn", t.SnapEnd));
            if (t.SnapEnd != null && t.SnapPreCombat != null)
            {
                var changes = Changes(t.SnapEnd, t.SnapPreCombat);
                if (changes.Count == 0) box.Children.Add(Head("After end-of-turn effects", "nothing changed"));
                else box.Children.Add(Snapshot("After end-of-turn effects", t.SnapPreCombat, string.Join(" · ", changes)));
            }
            else
                box.Children.Add(Snapshot("After end-of-turn effects", t.SnapPreCombat));
            return box;
        }

        /// <param name="opening">A — where gold is the bankroll the turn started with and is always
        /// worth a slot, as opposed to the leftover the later moments report.</param>
        private UIElement Snapshot(string label, ShopSnap s, string changes = null, bool opening = false)
        {
            if (s == null) return Head(label, "not captured");

            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            box.Children.Add(StateHead(label, s, changes, opening));

            box.Children.Add(CardRow(s));
            return box;
        }

        /// <summary>
        /// The board and the hand on ONE row, split by a marked divider — and the whole row scaled
        /// down until it fits rather than wrapping or growing.
        ///
        /// The hand used to sit on a line of its own under each board. That was honest and it cost
        /// a third of the view: with three moments per turn a 1080p client could see about 1.8 of
        /// them, before any of the breathing room the stat numbers still need. Here the hand costs
        /// no height at all, and what it costs instead is size — seven minions plus four cards come
        /// out around 79px rather than 110. That trade is the point: a minion the eye can still
        /// read, in a turn the eye can see all of.
        /// </summary>
        private UIElement CardRow(ShopSnap s)
        {
            var board = new List<UIElement>();
            foreach (var m in s.Board ?? new List<MinionRecord>())
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                var el = _minion != null ? _minion(s, m) : null;
                if (el != null) board.Add(el);
            }

            var hand = new List<UIElement>();
            double handAspect = 0;
            if (Hand != null && s.Hand != null)
                foreach (var m in s.Hand)
                {
                    if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                    var el = Hand(s, m);
                    if (el == null) continue;
                    hand.Add(el);
                    handAspect += Aspect(el);
                }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, BoardGap),
            };
            if (board.Count == 0)
            {
                // Said even when the hand is not empty: otherwise a row holding one card reads as
                // a board of one, and "you went into this with nothing on the field" is the more
                // important half of that moment.
                var empty = Line("Empty board", 12, UiKit.TextMuted);
                empty.Margin = new Thickness(0, 6, 0, 10);
                row.Children.Add(empty);
                if (hand.Count == 0) return row;
            }

            double divider = hand.Count > 0 ? DividerW : 0;
            double size = Size(board.Count, hand.Count, handAspect, divider);

            foreach (var el in board)
                // Hit-testing is the caller's business: it decides whether a card has anything to
                // say on hover and hands back something hoverable when it does. The press is never
                // handled here, so it still bubbles up to the panel's drag.
                row.Children.Add(new Viewbox
                {
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(MinionGap, 0, MinionGap, 0),
                    Child = el,
                });

            if (divider > 0) row.Children.Add(Divider(size));

            foreach (var el in hand)
                // Height only, no width: a spell's full card and a minion's square tile are
                // different shapes, and it is matching their HEIGHT that makes a mixed hand read
                // as one row rather than a ragged one.
                row.Children.Add(new Viewbox
                {
                    Height = size,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(HandGap, 0, HandGap, 0),
                    Child = el,
                });

            return row;
        }

        /// <summary>The largest card size at which everything on the row still fits the width
        /// between the two arrows, capped at the size a board alone is drawn at.</summary>
        private static double Size(int boardCount, int handCount, double handAspect, double divider)
        {
            double slots = boardCount + handAspect;
            if (slots <= 0) return MinionSize;
            double fixedPart = 2 * MinionGap * boardCount + 2 * HandGap * handCount + divider;
            double fit = (RowW - fixedPart) / slots;
            return fit < MinionSize ? fit : MinionSize;
        }

        /// <summary>
        /// A card's natural shape, so the row can budget width for it before it is in the tree. Read
        /// from an explicit Width/Height when the host set one, else measured. Anything that cannot
        /// answer counts as square, which is what a minion is.
        /// </summary>
        private static double Aspect(UIElement el)
        {
            try
            {
                var fe = el as FrameworkElement;
                if (fe != null && !double.IsNaN(fe.Width) && !double.IsNaN(fe.Height)
                    && fe.Width > 0 && fe.Height > 0)
                    return fe.Width / fe.Height;
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var d = el.DesiredSize;
                if (d.Width > 0 && d.Height > 0) return d.Width / d.Height;
            }
            catch { }
            return 1;
        }

        /// <summary>
        /// The mark between the board and the hand. It carries the word "hand" turned on its side
        /// because the one thing this view cannot spend is vertical space — a caption under the
        /// cards would cost a line on every one of the three moments, and this costs none.
        /// </summary>
        private static UIElement Divider(double size)
        {
            var line = new Border
            {
                Width = 1,
                Height = size * 0.55,
                Background = UiKit.StrokeBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = "hand",
                FontSize = 9.5,
                Foreground = UiKit.TextMuted,
                LayoutTransform = new RotateTransform(-90),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                IsHitTestVisible = false,
            };
            var g = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 3, 0),
                IsHitTestVisible = false,
            };
            g.Children.Add(line);
            g.Children.Add(label);
            return g;
        }

        /// <summary>A header with only words after the label: "not captured", "nothing changed".</summary>
        private static UIElement Head(string label, string detail)
        {
            var row = HeadRow();
            row.Children.Add(HeadLabel(label));
            row.Children.Add(Word(detail));
            return row;
        }

        /// <summary>
        /// The label, then only the numbers that this moment does not share with the rest of the
        /// turn — in the colours the game gives them, gold in gold, health in blood red, the tier
        /// as its own medallion.
        ///
        /// Two rules decide what survives. A number that never moved all turn was already said on
        /// the title line, so it is not said again. And gold LEFT is only news when there is some:
        /// the whole point of the figure is unspent money, so a zero at the end of a turn — the
        /// good case, and the common one — is silence rather than a slot.
        /// </summary>
        private UIElement StateHead(string label, ShopSnap s, string changes, bool opening)
        {
            var row = HeadRow();
            row.Children.Add(HeadLabel(label));
            if (!_fixed.Gold && (opening || s.Gold > 0))
            {
                row.Children.Add(Num(s.Gold, Gold));
                row.Children.Add(Word(opening ? "gold" : "gold left"));
            }
            if (!_fixed.Health)
            {
                Sep(row);
                row.Children.Add(Num(s.HeroHp, Red));
                row.Children.Add(Word("health"));
            }
            if (!_fixed.Tier)
            {
                Sep(row);
                AddTier(row, s.TavernTier);
            }
            if (changes != null)
            {
                var note = Word(row.Children.Count > 1 ? "—   " + changes : changes);
                note.Margin = new Thickness(14, 0, 0, 0);
                row.Children.Add(note);
            }
            return row;
        }

        private static StackPanel HeadRow() => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, HeadGap),
            IsHitTestVisible = false,
        };

        private static TextBlock HeadLabel(string label)
        {
            var tb = Line(label, 15, UiKit.TextPrimary);
            tb.FontWeight = FontWeights.SemiBold;
            tb.Margin = new Thickness(0, 0, 14, 0);
            return tb;
        }

        private static TextBlock Num(int value, Brush brush)
        {
            var tb = Line(value.ToString(CultureInfo.InvariantCulture), 15, brush);
            tb.FontWeight = FontWeights.SemiBold;
            tb.Margin = new Thickness(0, 0, 4, 0);
            return tb;
        }

        private static TextBlock Word(string text) => Line(text, 13, UiKit.TextMuted);

        /// <summary>A separator only between things that are actually there: with the fixed numbers
        /// gone a header can start at its second figure, and a leading "·" would read as a gap.</summary>
        private static void Sep(Panel row)
        {
            if (row.Children.Count > 1) row.Children.Add(Dot());
        }

        private static void AddTier(Panel row, int tier)
        {
            var icon = UiKit.TierIcon(tier, 18);
            if (icon != null)
            {
                icon.IsHitTestVisible = false;
                icon.VerticalAlignment = VerticalAlignment.Center;
                icon.Margin = new Thickness(0, 0, 4, 0);
                row.Children.Add(icon);
            }
            row.Children.Add(Num(tier, UiKit.TextSecondary));
            if (icon == null) row.Children.Add(Word("tier"));
        }

        private static TextBlock Dot()
        {
            var tb = Line("·", 13, UiKit.TextMuted);
            tb.Margin = new Thickness(8, 0, 8, 0);
            return tb;
        }

        // ── did the end-of-turn triggers change anything? ───────────────────────────────────────

        /// <summary>
        /// What the end-of-turn triggers changed between B and C, as short phrases; empty when
        /// nothing did. Compares the scalars, the board and the hand — the hand matters, a token
        /// that is a spell at B can be a 10/6 minion at C (seen live). Two things are left out on
        /// purpose: the tavern row, which C never has, and the enchantment lists, because the
        /// player entity's tavern bookkeeping (TagTransfer, Badsong) differs between B and C on
        /// turns where nothing else moved — measured over a 19-turn match, 2026-09-06 — and a
        /// change the reader could act on shows in a board or hand tag anyway.
        /// </summary>
        private static List<string> Changes(ShopSnap b, ShopSnap c)
        {
            var list = new List<string>();
            if (!SameMinions(b.Board, c.Board)) list.Add("board changed");
            if (!SameMinions(b.Hand, c.Hand)) list.Add("hand changed");
            if (b.Gold != c.Gold) list.Add("gold " + b.Gold + " → " + c.Gold);
            if (b.HeroHp != c.HeroHp) list.Add("health " + b.HeroHp + " → " + c.HeroHp);
            if (b.TavernTier != c.TavernTier) list.Add("tier " + b.TavernTier + " → " + c.TavernTier);
            return list;
        }

        private static bool SameMinions(List<MinionRecord> a, List<MinionRecord> b)
        {
            int na = a?.Count ?? 0, nb = b?.Count ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
            {
                var x = a[i]; var y = b[i];
                if (x == null || y == null) { if (x != y) return false; continue; }
                if (!string.Equals(x.CardId, y.CardId, StringComparison.Ordinal)) return false;
                int tx = x.Tags?.Count ?? 0, ty = y.Tags?.Count ?? 0;
                if (tx != ty) return false;
                if (tx == 0) continue;
                foreach (var kv in x.Tags)
                {
                    int v;
                    if (!y.Tags.TryGetValue(kv.Key, out v) || v != kv.Value) return false;
                }
            }
            return true;
        }

        // ── glyphs and plumbing ─────────────────────────────────────────────────────────────────

        private static Path BackGlyph() => Glyph("M9 2 L3 8 L9 14 M3 8 H15", 15, 13);

        private static Path ChevronGlyph(bool left) => Glyph(left ? "M9 2 L3 8 L9 14" : "M3 2 L9 8 L3 14", 11, 15);

        private static Path Glyph(string data, double w, double h) => new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = w,
            Height = h,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        private static Grid ThreeColumns()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowW) });
            return g;
        }

        private static void Put(Grid g, int col, UIElement child)
        {
            Grid.SetColumn(child, col);
            g.Children.Add(child);
        }

        private static Run Ink(string text, Brush brush) => new Run(text) { Foreground = brush };

        // The game's own colours for the two numbers: coin gold and blood red (the stats view's red).
        private static readonly Brush Gold = UiKit.AccentBrush;
        private static readonly Brush Red = Frozen(Color.FromRgb(0xEC, 0x69, 0x69));

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
