using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private const double BarW = 90;

        private readonly StackPanel _root = new StackPanel();

        public FrameworkElement Root => _root;

        /// <param name="biggestMinionName">
        /// Resolved by the caller, not here: card lookup lives in HDT's Database, and keeping this
        /// view free of it is what lets the whole thing be rendered to a PNG with no HDT running.
        /// </param>
        public void Show(MatchStats s, string biggestMinionName = null)
        {
            _root.Children.Clear();
            if (s == null || s.Turns == null || s.Turns.Count == 0)
            {
                _root.Children.Add(Empty());
                return;
            }

            _root.Children.Add(Headline(s));
            _root.Children.Add(Divider(12));
            _root.Children.Add(Counters(s, biggestMinionName));
            _root.Children.Add(Divider(12));
            _root.Children.Add(TurnTable(s));
        }

        // ── the four numbers worth reading first ────────────────────────────────────────────────
        private static UIElement Headline(MatchStats s)
        {
            var g = new Grid();
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int peakTurn = s.ApmPeakTurnNumber;
            Put(g, 0, Tile(s.ActionCount.ToString(CultureInfo.InvariantCulture), "actions", null));
            Put(g, 1, Tile(Round1(s.ApmAverage), "average APM", Mins(s.ShopSeconds) + " in shops"));
            Put(g, 2, Tile(Round0(s.ApmPeakTurn), "fastest turn", peakTurn > 0 ? "turn " + peakTurn : null));

            // Unspent gold is the only tile that can report a MISTAKE, so it is the only one that
            // ever leaves the accent colour. Painting a wasteful number in the same gold as a good
            // APM would congratulate the player for it.
            int wasted = s.GoldWasted;
            Put(g, 3, Tile(wasted.ToString(CultureInfo.InvariantCulture), "gold unspent",
                           wasted > 0 ? "over " + s.Turns.Count(t => t.GoldLeftover > 0) + " turns" : "nothing left behind",
                           wasted > 0 ? Red : null));
            return g;
        }

        private static UIElement Tile(string value, string label, string sub, Brush valueBrush = null)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            box.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = valueBrush ?? UiKit.AccentBrush,
                IsHitTestVisible = false,
            });
            box.Children.Add(Line(label, 13, UiKit.TextSecondary));
            box.Children.Add(Line(sub ?? "", 11, UiKit.TextMuted));
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
                Pair("Gold spent", s.GoldSpent.ToString(CultureInfo.InvariantCulture))));

            Put(g, 1, Stack(
                Pair("Played", Join(Count(s.MinionsPlayed, "minion"), Count(s.SpellsPlayed, "spell"))),
                Pair("Activated", s.MinionActivations.ToString(CultureInfo.InvariantCulture)),
                Pair("Hero power", s.HeroPowersUsed.ToString(CultureInfo.InvariantCulture)),
                Pair("Tavern ups", s.TavernUpgrades.ToString(CultureInfo.InvariantCulture)),
                Pair("Triples", s.TriplesCreated.ToString(CultureInfo.InvariantCulture))));

            Put(g, 2, Stack(
                Pair("Combats", s.CombatWins + "W " + s.CombatLosses + "L" + (s.CombatDraws > 0 ? " " + s.CombatDraws + "D" : "")),
                Pair("Damage dealt", Damage(s.HeroDamageDealt, s.MaxHeroDamageDealt)),
                Pair("Damage taken", Damage(s.HeroDamageTaken, s.MaxHeroDamageTaken)),
                Pair("Biggest", Biggest(s, biggestMinionName)),
                // Firestone's window, kept here rather than in the headline: see MatchStats.ApmPeakBurst.
                Pair("Peak burst", Round0(s.ApmPeakBurst) + " APM in 4s")));
            return g;
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
            return total + (max > 0 ? "  (best " + max + ")" : "");
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

        private static UIElement Pair(string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 18, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Line(label, 13, UiKit.TextMuted);
            var v = Line(value, 13, UiKit.TextPrimary);
            v.FontWeight = FontWeights.SemiBold;
            g.Children.Add(l);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return g;
        }

        // ── the series ──────────────────────────────────────────────────────────────────────────
        private static UIElement TurnTable(MatchStats s)
        {
            var rows = new StackPanel();
            rows.Children.Add(HeaderRow());

            int maxActions = Math.Max(1, s.Turns.Max(t => t.Actions));
            bool alt = false;
            foreach (var t in s.Turns)
            {
                rows.Children.Add(TurnRow(t, maxActions, alt));
                alt = !alt;
            }
            return rows;
        }

        private static readonly double[] Cols = { 46, 46, 92, 118, 118, 40, BarW + 34, 52 };

        private static Grid Row()
        {
            var g = new Grid { Width = ContentW, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var w in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        private static UIElement HeaderRow()
        {
            var g = Row();
            string[] heads = { "turn", "tier", "health", "combat", "gold", "acts", "", "APM" };
            for (int i = 0; i < heads.Length; i++)
            {
                var tb = Line(heads[i], 11, UiKit.TextMuted);
                tb.Margin = new Thickness(0, 0, 0, 4);
                Grid.SetColumn(tb, i);
                g.Children.Add(tb);
            }
            return g;
        }

        private static UIElement TurnRow(TurnStat t, int maxActions, bool alt)
        {
            var g = Row();
            g.Height = 22;

            Cell(g, 0, "T" + t.Turn, UiKit.TextSecondary, FontWeights.SemiBold);
            Cell(g, 1, t.TavernTier > 0 ? t.TavernTier.ToString(CultureInfo.InvariantCulture) : "—", UiKit.TextMuted, FontWeights.Normal);
            Cell(g, 2, Health(t), UiKit.TextSecondary, FontWeights.Normal);

            var combat = Line(CombatText(t), 12, CombatBrush(t));
            Grid.SetColumn(combat, 3);
            g.Children.Add(combat);

            // Leftover gold is the mistake worth seeing, so it is the half that gets coloured.
            var gold = new TextBlock { FontSize = 12, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            gold.Inlines.Add(new System.Windows.Documents.Run(t.GoldSpent + " spent") { Foreground = UiKit.TextSecondary });
            if (t.GoldLeftover > 0)
                gold.Inlines.Add(new System.Windows.Documents.Run("  " + t.GoldLeftover + " left") { Foreground = Red });
            Grid.SetColumn(gold, 4);
            g.Children.Add(gold);

            Cell(g, 5, t.Actions.ToString(CultureInfo.InvariantCulture), UiKit.TextPrimary, FontWeights.SemiBold);

            var bar = new Border
            {
                Width = Math.Max(2, BarW * t.Actions / (double)maxActions),
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = UiKit.AccentBrush,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(bar, 6);
            g.Children.Add(bar);

            Cell(g, 7, t.ShopSeconds >= 1 ? Round0(t.Apm) : "—", UiKit.TextSecondary, FontWeights.Normal);

            if (!alt) return g;
            return new Border { Background = UiKit.Br(Color.FromArgb(0x1A, 0x39, 0x47, 0x5E)), Child = g };
        }

        private static string Health(TurnStat t)
        {
            if (t.HeroHpStart <= 0 && t.HeroHpEnd <= 0) return "—";
            if (t.HeroHpEnd == t.HeroHpStart) return t.HeroHpStart.ToString(CultureInfo.InvariantCulture);
            return t.HeroHpStart + " → " + t.HeroHpEnd;
        }

        /// <summary>
        /// A win reads by the damage it sent, a loss by the damage it cost. No sign on the number:
        /// a minus in front of a win's damage reads as something the player lost, which is the exact
        /// opposite of what happened. The row's colour already says which side took it.
        /// </summary>
        private static string CombatText(TurnStat t)
        {
            if (t.CombatResult == null) return "—";
            if (t.CombatResult == "win") return t.DamageDealt > 0 ? "won  " + t.DamageDealt + " dmg" : "won";
            if (t.CombatResult == "loss") return t.DamageTaken > 0 ? "lost  " + t.DamageTaken + " dmg" : "lost";
            return "tied";
        }

        private static Brush CombatBrush(TurnStat t)
        {
            if (t.CombatResult == "win") return Green;
            if (t.CombatResult == "loss") return Red;
            return UiKit.TextMuted;
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
