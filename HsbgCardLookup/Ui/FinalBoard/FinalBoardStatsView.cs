using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HsbgCardLookup.Config;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// The half of a match Hearthstone never shows you: what you DID.
    ///
    /// This exists because the board, hero power and trinkets are already on the game's own end
    /// screens, so a panel that only redrew them would be a worse copy of something the player just
    /// looked at. Everything here is derived from <see cref="MatchStats"/> — the action stream we
    /// record ourselves — and none of it is available anywhere in game.
    ///
    /// The per-turn table is the point, not the headline tiles. A total can say "you left 9 gold
    /// unspent"; only the series can say WHICH turn, next to the tier you were on and the fight you
    /// then lost. Totals summarise, series explain.
    /// </summary>
    internal sealed class FinalBoardStatsView
    {
        private const double ContentW = 888;      // the panel's 920 reference width less its padding
        private const double RowH = 26;
        private const double TierIconH = 20;

        private readonly StackPanel _root = new StackPanel();

        public FrameworkElement Root => _root;

        /// <param name="biggestMinionName">
        /// Resolved by the caller, not here: card lookup lives in HDT's Database, and keeping this
        /// view free of it is what lets the whole thing be rendered to a PNG with no HDT running.
        /// </param>
        /// <param name="options">Which blocks to draw; null means all of them.</param>
        public void Show(MatchStats s, string biggestMinionName = null, FinalBoardOptions options = null)
        {
            var o = options ?? new FinalBoardOptions();
            _root.Children.Clear();
            if (s == null || s.Turns == null || s.Turns.Count == 0)
            {
                _root.Children.Add(Empty());
                return;
            }

            // Built as a list first so a divider only ever lands BETWEEN two blocks that are both
            // being drawn -- switching the middle block off must not leave two rules touching.
            var blocks = new List<UIElement>();
            if (o.HeadlineTiles) blocks.Add(Headline(s));
            if (o.Counters) blocks.Add(Counters(s, biggestMinionName));
            if (o.TurnTable) blocks.Add(TurnTable(s));
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i > 0) _root.Children.Add(Divider(12));
                _root.Children.Add(blocks[i]);
            }
        }

        // ── action categories ───────────────────────────────────────────────────────────────────
        /// <summary>
        /// A turn's actions, split by what they were. Each kind gets its OWN COLUMN in the table,
        /// always in this order, always in the same place, labelled by the header above it.
        ///
        /// That is a deliberate correction of a first attempt at a stacked bar, and the reason is
        /// measured rather than aesthetic. In a stack, a category that did not happen this turn
        /// collapses out, so segments that are far apart in the palette end up touching — and run
        /// against ALL pairs rather than adjacent ones, this set fails: Sell and Spell are
        /// separated by 1.6 (OKLab ΔE) for a deuteranope, and Other and Buy by 9.8 for normal
        /// vision, against a floor of 15. No seven-colour set clears that gate. Giving each kind a
        /// fixed labelled column makes POSITION the identity and colour merely reinforcement, which
        /// removes the problem instead of arguing with it — and it lets a column be read downward,
        /// so "when did I stop buying" is answerable at a glance, which a row of stacks never was.
        ///
        /// Upgrades, freezes and hero powers share "Other": a handful per match each, and the
        /// counters above already report all three by name.
        /// </summary>
        private static readonly ActionKind[] Kinds =
        {
            new ActionKind("Buy", 0x39, 0x87, 0xE5, "buy", "buyspell"),
            new ActionKind("Play", 0xD9, 0x59, 0x26, "play"),
            new ActionKind("Spell", 0x19, 0x9E, 0x70, "spell"),
            new ActionKind("Activate", 0xC9, 0x85, 0x00, "activate"),
            new ActionKind("Sell", 0xD5, 0x51, 0x81, "sell"),
            new ActionKind("Roll", 0x00, 0x83, 0x00, "roll"),
            new ActionKind("Other", 0x90, 0x85, 0xE9, "upgrade", "freeze", "heropower"),
        };

        private sealed class ActionKind
        {
            public readonly string Label;
            public readonly Brush Brush;
            private readonly string[] _kinds;

            public ActionKind(string label, byte r, byte g, byte b, params string[] kinds)
            {
                Label = label;
                Brush = Frozen(Color.FromRgb(r, g, b));
                _kinds = kinds;
            }

            public bool Matches(string kind) => _kinds.Contains(kind, StringComparer.Ordinal);
        }

        /// <summary>Anything the tracker learns to emit later still lands somewhere, in "Other".</summary>
        private static int IndexOf(string kind)
        {
            for (int i = 0; i < Kinds.Length; i++) if (Kinds[i].Matches(kind)) return i;
            return Kinds.Length - 1;
        }

        // ── the four numbers worth reading first ────────────────────────────────────────────────
        private static UIElement Headline(MatchStats s)
        {
            var g = new Grid();
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int peakTurn = s.ApaPeakTurnNumber;
            Put(g, 0, Tile(s.ActionCount.ToString(CultureInfo.InvariantCulture), "actions", null, UiKit.AccentBrush));
            Put(g, 1, Tile(Round1(s.ApaAverage), "average APA", ActiveOfShops(s), Kinds[0].Brush));
            Put(g, 2, Tile(Round0(s.ApaPeakTurn), "peak sustained APA", peakTurn > 0 ? "on turn " + peakTurn : null, Kinds[2].Brush));

            // Gold spent, not gold unspent: a big tile around a one-digit number was a frame with
            // no picture, and spending is the total that scales with how much got done. The
            // leftover is still the mistake, so it keeps its red — in the subline, where a small
            // number belongs.
            int wasted = s.GoldWasted;
            Put(g, 3, Tile(s.GoldSpent.ToString(CultureInfo.InvariantCulture), "gold spent",
                           wasted > 0 ? wasted + " left unspent" : "every coin spent",
                           Kinds[3].Brush, wasted > 0 ? Red : null));
            return g;
        }

        /// <summary>"active 9:41 of 24:29 in shops" — the denominator's story and the idle share in one line.</summary>
        private static string ActiveOfShops(MatchStats s)
        {
            string shops = Mins(s.ShopSeconds);
            string active = Mins(s.ActiveSeconds);
            if (shops.Length == 0) return null;
            if (active.Length == 0) return shops + " in shops";
            return "active " + active + " of " + shops + " in shops";
        }

        private static UIElement Tile(string value, string label, string sub, Brush brush, Brush subBrush = null)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            box.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = brush,
                IsHitTestVisible = false,
            });
            box.Children.Add(Line(label, 13, UiKit.TextSecondary));
            box.Children.Add(Line(sub ?? "", 11, subBrush ?? UiKit.TextMuted));
            return box;
        }

        // ── everything that was pressed, counted ────────────────────────────────────────────────
        private static UIElement Counters(MatchStats s, string biggestMinionName)
        {
            var g = new Grid();
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Grouped by what the player was doing, not by what was easy to count: the tavern, the
            // board, then the fight. A flat list of eleven numbers is a list; these are three answers.
            Put(g, 0, Stack(
                Pair("Bought", Join(Count(s.MinionsBought, "minion"), Count(s.SpellsBought, "spell"))),
                Pair("Sold", s.MinionsSold.ToString(CultureInfo.InvariantCulture)),
                Pair("Rolled", s.TavernRolls.ToString(CultureInfo.InvariantCulture)),
                Pair("Froze", s.Freezes.ToString(CultureInfo.InvariantCulture)),
                Pair("Gold unspent", s.GoldWasted.ToString(CultureInfo.InvariantCulture),
                     s.GoldWasted > 0 ? Red : null)));

            Put(g, 1, Stack(
                Pair("Played", Join(Count(s.MinionsPlayed, "minion"), Count(s.SpellsPlayed, "spell"))),
                Pair("Activated", s.MinionActivations.ToString(CultureInfo.InvariantCulture)),
                Pair("Hero power", s.HeroPowersUsed.ToString(CultureInfo.InvariantCulture)),
                Pair("Triples", s.TriplesCreated.ToString(CultureInfo.InvariantCulture))));

            Put(g, 2, Stack(
                Pair("Combats", CombatsText(s)),
                Pair("Damage dealt", Damage(s.HeroDamageDealt, s.MaxHeroDamageDealt)),
                Pair("Damage taken", Damage(s.HeroDamageTaken, s.MaxHeroDamageTaken)),
                Pair("Biggest minion", Biggest(s, biggestMinionName)),
                // Firestone's window, kept here rather than in the headline: see MatchStats.PeakBurstActions.
                Pair("Peak burst", s.PeakBurstActions + " actions in 4s")));
            return g;
        }

        /// <summary>Each half wears its verdict whole — "8W" in win-green, "8L" in loss-red — so the
        /// record reads at a glance even before the letters do.</summary>
        private static TextBlock CombatsText(MatchStats s)
        {
            var tb = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            tb.Inlines.Add(Ink(s.CombatWins + "W", Green));
            tb.Inlines.Add(Ink(" " + s.CombatLosses + "L", Red));
            if (s.CombatDraws > 0)
                tb.Inlines.Add(Ink(" " + s.CombatDraws + "D", UiKit.TextMuted));
            return tb;
        }

        private static string Biggest(MatchStats s, string name)
        {
            if (s.HighestMinionAttack <= 0 && s.HighestMinionHealth <= 0) return "—";
            string stats = s.HighestMinionAttack + "/" + s.HighestMinionHealth;
            if (string.IsNullOrEmpty(name)) return stats;
            return name + "  " + stats;
        }

        private static string Damage(int total, int max)
        {
            if (total <= 0) return "0";
            return total + (max > 0 ? "  (highest " + max + ")" : "");
        }

        private static string Count(int n, string noun)
        {
            if (n <= 0) return null;
            return n + " " + noun + (n == 1 ? "" : "s");
        }

        private static string Join(string a, string b)
        {
            if (a == null && b == null) return "0";
            if (a == null) return b;
            if (b == null) return a;
            return a + " · " + b;
        }

        private static UIElement Pair(string label, string value, Brush valueBrush = null)
        {
            var v = Line(value, 13, valueBrush ?? UiKit.TextPrimary);
            v.FontWeight = FontWeights.SemiBold;
            return Pair(label, v);
        }

        private static UIElement Pair(string label, TextBlock value)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 18, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Line(label, 13, UiKit.TextMuted));
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
            return g;
        }

        // ── the series ──────────────────────────────────────────────────────────────────────────
        private static UIElement TurnTable(MatchStats s)
        {
            var rows = new StackPanel();
            rows.Children.Add(HeaderRow());

            bool alt = false;
            foreach (var t in s.Turns)
            {
                rows.Children.Add(TurnRow(t, alt));
                alt = !alt;
            }
            return rows;
        }

        // turn | tier | health | combat | gold | active | actions | APA | APM, then one column per
        // action kind. The rates are two-digit numbers and need almost none of their width; the
        // text columns get what their longest line needs ("Dealt 12 damage", "16 spent  3 left").
        private static readonly double[] Cols = { 34, 48, 80, 110, 100, 52, 50, 40, 40, 42, 44, 46, 58, 40, 40, 46 };

        private static readonly string[] Heads = { "Turn", "Tier", "Health", "Combat", "Gold", "Active", "Actions", "APA", "APM" };

        // Hover text for the three columns whose label cannot carry their definition. Everything
        // else in the header says what it is.
        private static readonly string[] HeadTips =
        {
            null, null, null, null, null,
            "First action to last, with any pause longer than 10 s counted as 10 s.\nReading the shop in and sitting there afterwards do not count.",
            null,
            "Actions per active minute: this turn's actions over its active time.\nA short flurry reads high; the peak tile only considers turns with 15 s or more of active time.",
            "Actions per minute of the whole shop phase, idle time included.",
        };

        private static Grid Row()
        {
            var g = new Grid { Width = ContentW, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var w in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        /// <summary>The kind columns label themselves, so the table needs no separate legend.</summary>
        private static UIElement HeaderRow()
        {
            var g = Row();
            for (int i = 0; i < Heads.Length; i++) Put(g, i, Head(Heads[i], HeadTips[i]));
            for (int i = 0; i < Kinds.Length; i++) Put(g, Heads.Length + i, Head(Kinds[i].Label, null));
            return g;
        }

        /// <summary>A head with a tip must be hit-testable for the hover to reach it; the rest stay transparent to the panel's drag.</summary>
        private static TextBlock Head(string text, string tip)
        {
            var tb = Line(text, 12, UiKit.TextMuted);
            tb.Margin = new Thickness(0, 0, 0, 4);
            if (tip != null)
            {
                tb.ToolTip = tip;
                tb.IsHitTestVisible = true;
            }
            return tb;
        }

        private static UIElement TurnRow(TurnStat t, bool alt)
        {
            var g = Row();
            g.Height = RowH;

            Cell(g, 0, t.Turn.ToString(CultureInfo.InvariantCulture), UiKit.TextSecondary, FontWeights.SemiBold);
            Put(g, 1, Tier(t));
            Put(g, 2, Health(t));
            Put(g, 3, Combat(t));
            Put(g, 4, Gold(t));
            Cell(g, 5, t.ActiveSeconds >= 1 ? Mins(t.ActiveSeconds) : "—", UiKit.TextSecondary, FontWeights.Normal);
            Cell(g, 6, t.Actions.ToString(CultureInfo.InvariantCulture), UiKit.TextPrimary, FontWeights.SemiBold);
            Cell(g, 7, t.ActiveSeconds >= 1 ? Round0(t.Apa) : "—", UiKit.TextSecondary, FontWeights.Normal);
            Cell(g, 8, t.ShopSeconds >= 1 ? Round0(t.ShopApm) : "—", UiKit.TextSecondary, FontWeights.Normal);

            var counts = new int[Kinds.Length];
            if (t.ActionKinds != null) foreach (var k in t.ActionKinds) counts[IndexOf(k)]++;
            for (int i = 0; i < Kinds.Length; i++)
                if (counts[i] > 0) Put(g, Heads.Length + i, KindCell(Kinds[i], counts[i]));

            if (!alt) return g;
            return new Border { Background = UiKit.Br(Color.FromArgb(0x1A, 0x39, 0x47, 0x5E)), Child = g };
        }

        /// <summary>A zero renders as nothing at all — an empty cell says "none of these" faster than a nought does, and keeps the eye on the turns where something happened.</summary>
        private static UIElement KindCell(ActionKind kind, int count)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(2),
                Background = kind.Brush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            });
            var n = Line(count.ToString(CultureInfo.InvariantCulture), 12, UiKit.TextPrimary);
            n.FontWeight = FontWeights.SemiBold;
            cell.Children.Add(n);
            return cell;
        }

        /// <summary>
        /// The tier icon ships with the plugin already, so it costs nothing — but it is a shield
        /// carrying N stars, and at a table row's height nobody counts stars. It keeps the number
        /// beside it: the shield is what the eye finds, the digit is what it reads.
        /// </summary>
        private static UIElement Tier(TurnStat t)
        {
            if (t.TavernTier <= 0) return Line("—", 12, UiKit.TextMuted);
            var box = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = UiKit.TierIcon(t.TavernTier, TierIconH);
            if (icon != null)
            {
                icon.IsHitTestVisible = false;
                icon.Margin = new Thickness(0, 0, 5, 0);
                box.Children.Add(icon);
            }
            box.Children.Add(Line(t.TavernTier.ToString(CultureInfo.InvariantCulture), 12, UiKit.TextSecondary));
            return box;
        }

        private static TextBlock Health(TurnStat t)
        {
            var tb = Run12();
            if (t.HeroHpStart <= 0 && t.HeroHpEnd <= 0) { tb.Inlines.Add(Ink("—", UiKit.TextMuted)); return tb; }
            if (t.HeroHpEnd == t.HeroHpStart || t.HeroHpEnd <= 0)
            {
                tb.Inlines.Add(Ink(t.HeroHpStart.ToString(CultureInfo.InvariantCulture), UiKit.TextSecondary));
                return tb;
            }
            if (t.HeroHpStart <= 0)
            {
                // No reading at the top of the turn, so this is where the hero came in — 30 plus armour,
                // doubled armour in duos, 60 for Patchwerk off his hero power. "0 → 40" said the hero
                // was resurrected from nothing; it is a spawn, and a spawn is one number.
                tb.Inlines.Add(Ink(t.HeroHpEnd.ToString(CultureInfo.InvariantCulture), UiKit.TextSecondary));
                return tb;
            }
            // The arrow recedes and the landing number carries the colour: what matters is where the
            // health ended up, not the punctuation getting it there.
            bool down = t.HeroHpEnd < t.HeroHpStart;
            tb.Inlines.Add(Ink(t.HeroHpStart.ToString(CultureInfo.InvariantCulture), UiKit.TextMuted));
            tb.Inlines.Add(Ink(" → ", UiKit.TextMuted));
            var end = Ink(t.HeroHpEnd.ToString(CultureInfo.InvariantCulture), down ? Red : Green);
            end.FontWeight = FontWeights.SemiBold;
            tb.Inlines.Add(end);
            return tb;
        }

        /// <summary>A win reads by the damage it sent, a loss by the damage it cost — in those words, because "won 3 damage" is not a sentence.</summary>
        private static TextBlock Combat(TurnStat t)
        {
            var tb = Run12();
            if (t.CombatResult == null) { tb.Inlines.Add(Ink("—", UiKit.TextMuted)); return tb; }
            if (t.CombatResult == "win")
                tb.Inlines.Add(Ink(t.DamageDealt > 0 ? "Dealt " + t.DamageDealt + " damage" : "Won", Green));
            else if (t.CombatResult == "loss")
                tb.Inlines.Add(Ink(t.DamageTaken > 0 ? "Took " + t.DamageTaken + " damage" : "Lost", Red));
            else
                tb.Inlines.Add(Ink("Tied", UiKit.TextMuted));
            return tb;
        }

        /// <summary>Leftover gold is the mistake worth seeing, so it is the half that gets coloured.</summary>
        private static TextBlock Gold(TurnStat t)
        {
            var tb = Run12();
            tb.Inlines.Add(Ink(t.GoldSpent + " spent", UiKit.TextSecondary));
            if (t.GoldLeftover > 0) tb.Inlines.Add(Ink("  " + t.GoldLeftover + " left", Red));
            return tb;
        }

        private static UIElement Empty()
        {
            var box = new StackPanel { Margin = new Thickness(0, 24, 0, 24), HorizontalAlignment = HorizontalAlignment.Center };
            var a = Line("Nothing recorded for this match", 15, UiKit.TextSecondary);
            a.HorizontalAlignment = HorizontalAlignment.Center;
            var b = Line("Action stats exist only for matches played while recording was on.", 12, UiKit.TextMuted);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(a);
            box.Children.Add(b);
            return box;
        }

        // ── plumbing ────────────────────────────────────────────────────────────────────────────
        private static TextBlock Run12() => new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        private static Run Ink(string text, Brush brush) => new Run(text) { Foreground = brush };

        private static void Cell(Grid g, int col, string text, Brush brush, FontWeight weight)
        {
            var tb = Line(text, 12, brush);
            tb.FontWeight = weight;
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        private static void Put(Grid g, int col, UIElement child)
        {
            Grid.SetColumn(child, col);
            g.Children.Add(child);
        }

        private static StackPanel Stack(params UIElement[] children)
        {
            var p = new StackPanel();
            foreach (var c in children) p.Children.Add(c);
            return p;
        }

        private static TextBlock Line(string text, double size, Brush brush) => new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        private static Border Divider(double gap) => new Border
        {
            Height = 1,
            Margin = new Thickness(0, gap, 0, gap),
            Background = UiKit.Br(Color.FromArgb(0x55, 0x39, 0x47, 0x5E)),
        };

        private static string Round0(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        private static string Round1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Mins(double seconds)
        {
            if (seconds < 1) return "";
            var ts = TimeSpan.FromSeconds(seconds);
            return ((int)ts.TotalMinutes) + ":" + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private static readonly Brush Green = Frozen(Color.FromRgb(0x6D, 0xEB, 0x6C));
        private static readonly Brush Red = Frozen(Color.FromRgb(0xEC, 0x69, 0x69));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
