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
        // The hand sits under its board and reads as subordinate to it. Positive gap, unlike the
        // board's overlap: hand cards carry no frame that draws past their box, so a negative one
        // would just make them touch.
        private const double HandSize = 74;
        private const double HandGap = 2;
        private const double HeadGap = 12;
        private const double BoardGap = 12;

        private readonly Grid _root = new Grid { Width = ContentW, HorizontalAlignment = HorizontalAlignment.Left };

        private MatchStats _stats;
        private int _index;
        private Func<ShopSnap, MinionRecord, UIElement> _minion;

        public FrameworkElement Root => _root;

        /// <summary>The back arrow was clicked. The view never hides itself — whoever hosts it owns that.</summary>
        public Action Back;

        /// <summary>A strip of the turn's hero power and trinkets, built by the host because it owns
        /// the HDT controls. Null draws nothing and costs no height.</summary>
        public Func<TurnStat, UIElement> Detail;

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

            var col = new StackPanel();
            col.Children.Add(TopBar(t));
            // What was POWERING the boards below, once for the turn rather than once per snapshot:
            // three boards already strain a 1080p client's height, and a hero power that changed
            // between A and C says so in its own tooltip.
            UIElement detail = null;
            try { detail = Detail?.Invoke(t); } catch { }
            if (detail != null) col.Children.Add(detail);
            col.Children.Add(Body(t));
            _root.Children.Add(col);
        }

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
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var name = Ink("Turn " + t.Turn.ToString(CultureInfo.InvariantCulture), UiKit.TextPrimary);
            name.FontWeight = FontWeights.SemiBold;
            title.Inlines.Add(name);
            title.Inlines.Add(Ink("  of " + _stats.Turns.Count.ToString(CultureInfo.InvariantCulture), UiKit.TextMuted));
            Put(g, 1, title);
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
            box.Children.Add(Snapshot("Start of turn", t.SnapStart));
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

        private UIElement Snapshot(string label, ShopSnap s, string changes = null)
        {
            if (s == null) return Head(label, "not captured");

            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            box.Children.Add(StateHead(label, s, changes));

            // A taunt frame draws past its minion's box, so the row keeps clearance below.
            var board = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, BoardGap),
            };
            foreach (var m in s.Board ?? new List<MinionRecord>())
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                var el = _minion != null ? _minion(s, m) : null;
                if (el == null) continue;
                // Hit-testing is the caller's business now: it decides whether this minion has
                // anything to say on hover, and hands back something hoverable when it does. The
                // press itself is never handled here, so it still bubbles to the panel's drag.
                board.Children.Add(new Viewbox
                {
                    Width = MinionSize,
                    Height = MinionSize,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(MinionGap, 0, MinionGap, 0),
                    Child = el,
                });
            }
            if (board.Children.Count == 0)
            {
                var empty = Line("Empty board", 12, UiKit.TextMuted);
                empty.Margin = new Thickness(0, 6, 0, 10);
                board.Children.Add(empty);
            }
            box.Children.Add(board);

            var hand = HandRow(s);
            if (hand != null) box.Children.Add(hand);
            return box;
        }

        /// <summary>
        /// What was still in hand at this moment, under the board and smaller than it — a card the
        /// player held is context for the board, not part of it.
        ///
        /// Nothing is drawn for a hand that was EMPTY; the row appears only when there is something
        /// in it. The distinction the record makes between "empty" and "not captured" is deliberate
        /// (a failed read used to be indistinguishable from an empty hand), but it belongs in the
        /// record — a line saying the hand was empty on every early turn would be noise here.
        /// </summary>
        private UIElement HandRow(ShopSnap s)
        {
            if (Hand == null || s?.Hand == null || s.Hand.Count == 0) return null;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -6, 0, BoardGap),
            };
            foreach (var m in s.Hand)
            {
                if (m == null || string.IsNullOrEmpty(m.CardId)) continue;
                var el = Hand(s, m);
                if (el == null) continue;
                // Height only: a spell's full card and a minion's square tile have different
                // shapes, and matching their HEIGHT is what makes a mixed hand read as one row.
                row.Children.Add(new Viewbox
                {
                    Height = HandSize,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(HandGap, 0, HandGap, 0),
                    Child = el,
                });
            }
            if (row.Children.Count == 0) return null;

            var label = Line("in hand", 11, UiKit.TextMuted);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 0, 0, 2);
            var box = new StackPanel();
            box.Children.Add(label);
            box.Children.Add(row);
            return box;
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
        /// The label, then the three numbers in the colours the game gives them — gold in gold,
        /// health in blood red, the tier as its own medallion — so the row reads at a glance from
        /// the board below rather than as a line of grey text above it.
        /// </summary>
        private static UIElement StateHead(string label, ShopSnap s, string changes)
        {
            var row = HeadRow();
            row.Children.Add(HeadLabel(label));
            row.Children.Add(Num(s.Gold, Gold));
            row.Children.Add(Word("gold"));
            row.Children.Add(Dot());
            row.Children.Add(Num(s.HeroHp, Red));
            row.Children.Add(Word("health"));
            row.Children.Add(Dot());
            var tier = UiKit.TierIcon(s.TavernTier, 18);
            if (tier != null)
            {
                tier.IsHitTestVisible = false;
                tier.VerticalAlignment = VerticalAlignment.Center;
                tier.Margin = new Thickness(0, 0, 4, 0);
                row.Children.Add(tier);
            }
            row.Children.Add(Num(s.TavernTier, UiKit.TextSecondary));
            if (tier == null) row.Children.Add(Word("tier"));
            if (changes != null)
            {
                var note = Word("—   " + changes);
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
