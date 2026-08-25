using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// What a player did during a match, as opposed to what they ended up with. This is the half
    /// Hearthstone does not show anywhere: the board, hero power and trinkets are all on the game's
    /// own end screens, but how much gold went unspent, how often the tavern was rolled, how fast
    /// the hands moved — none of that exists until something counts it.
    ///
    /// Counter semantics follow Reign-in-blood's HDT-FinalStatsPlugin (MIT) where they overlap; his
    /// AGENTS.md §11 documents both the rules and the ways each one goes wrong, which is worth more
    /// than the code. <see cref="MinionsSold"/> and the APM figures are ours — his plugin tracks
    /// neither.
    ///
    /// A missing value is left at zero and rendered as "—" rather than guessed. That is his rule and
    /// ours: a confident wrong number is worse than an absent one, because a player will act on it.
    /// </summary>
    internal sealed class MatchStats
    {
        // ── economy ─────────────────────────────────────────────────────────────────────────────
        public int GoldSpent { get; set; }
        public int TavernRolls { get; set; }
        public int TavernUpgrades { get; set; }
        public int Freezes { get; set; }
        public int TriplesCreated { get; set; }
        public int HeroPowersUsed { get; set; }
        public int MinionsBought { get; set; }
        public int SpellsBought { get; set; }
        public int MinionsSold { get; set; }
        public int MinionsPlayed { get; set; }
        public int SpellsPlayed { get; set; }

        /// <summary>
        /// Season 14's "Activate (N)" abilities used on minions already on the board. A separate
        /// action from playing the minion, costing its own gold, and one nothing else counts —
        /// seven of them in a single 8-turn match, so leaving them out understates a busy player.
        /// </summary>
        public int MinionActivations { get; set; }

        // ── board peaks ─────────────────────────────────────────────────────────────────────────
        public int HighestAttack { get; set; }
        public int HighestHealth { get; set; }
        /// <summary>Attack+health of the single biggest minion — never the max attack of one glued to the max health of another.</summary>
        public int HighestMinionAttack { get; set; }
        public int HighestMinionHealth { get; set; }
        public string HighestMinionCardId { get; set; }

        // ── combat ──────────────────────────────────────────────────────────────────────────────
        public int HeroDamageDealt { get; set; }
        public int MaxHeroDamageDealt { get; set; }
        public int HeroDamageTaken { get; set; }
        public int MaxHeroDamageTaken { get; set; }
        public int CombatWins { get; set; }
        public int CombatLosses { get; set; }
        public int CombatDraws { get; set; }

        /// <summary>One entry per shop turn — the series is what makes the numbers analysable rather than just true.</summary>
        public List<TurnStat> Turns { get; set; } = new List<TurnStat>();

        [JsonIgnore] public int HighestTurn => Turns != null && Turns.Count > 0 ? Turns.Max(t => t.Turn) : 0;

        /// <summary>Total gold left unspent across the match — the cheapest mistake to see and to fix.</summary>
        [JsonIgnore] public int GoldWasted => Turns != null ? Turns.Sum(t => Math.Max(0, t.GoldLeftover)) : 0;

        [JsonIgnore] public int ActionCount => Turns != null ? Turns.Sum(t => t.Actions) : 0;

        /// <summary>
        /// Actions per minute across the whole match's shop time. Combat is excluded on purpose:
        /// nothing can be done during it, so counting it would punish long fights and flatter a slow
        /// player whose combats happened to be short.
        /// </summary>
        [JsonIgnore] public double ApmAverage => ShopSeconds >= 1 ? ActionCount / (ShopSeconds / 60.0) : 0;

        /// <summary>Total time spent in shops. The denominator of <see cref="ApmAverage"/>.</summary>
        [JsonIgnore] public double ShopSeconds => Turns != null ? Turns.Sum(t => t.ShopSeconds) : 0;

        /// <summary>Best SUSTAINED turn — how fast a whole shop was played. Turns under 5s divide into noise.</summary>
        [JsonIgnore] public double ApmPeakTurn => PeakTurn().Value;

        /// <summary>Which turn <see cref="ApmPeakTurn"/> belongs to, or 0 if there is no usable turn.</summary>
        [JsonIgnore] public int ApmPeakTurnNumber => PeakTurn().Key;

        private KeyValuePair<int, double> PeakTurn()
        {
            int bestTurn = 0;
            double best = 0;
            if (Turns != null)
            {
                foreach (var t in Turns)
                {
                    if (t.ShopSeconds < 5) continue;
                    double apm = t.Actions / (t.ShopSeconds / 60.0);
                    if (apm > best) { best = apm; bestTurn = t.Turn; }
                }
            }
            return new KeyValuePair<int, double>(bestTurn, best);
        }

        /// <summary>
        /// Best BURST — the fastest four seconds of the match. Four seconds is Firestone's window,
        /// so this figure is comparable to the one their in-game widget shows; theirs is live-only
        /// and resets every turn, so a match-level version does not exist anywhere else.
        ///
        /// NOT the headline number, and that was measured. In a real match whose turn 8 carried 34
        /// of the player's 61 actions, the four-second peak picked turn FIVE instead — five ordinary
        /// drags that happened to land close together score 75, while the flurry the player actually
        /// remembers scores 31 sustained across a whole shop. A window that short measures how fast
        /// two hands can move once, not how fast a turn was played. <see cref="ApmPeakTurn"/> is the
        /// one that agrees with the player.
        /// </summary>
        [JsonIgnore]
        public double ApmPeakBurst => PeakBurst(4.0);

        public double PeakBurst(double windowSeconds)
        {
            if (Turns == null || windowSeconds <= 0) return 0;
            double best = 0;
            foreach (var t in Turns)
            {
                var times = t.ActionTimes;
                if (times == null || times.Count == 0) continue;
                // Every window that starts at an action: the busiest window always does.
                for (int i = 0; i < times.Count; i++)
                {
                    int count = 0;
                    double until = times[i] + windowSeconds * 1000.0;
                    for (int j = i; j < times.Count && times[j] <= until; j++) count++;
                    double apm = count / windowSeconds * 60.0;
                    if (apm > best) best = apm;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// One shop turn and the combat that followed it, finalised once. Keeping the per-turn series
    /// rather than only totals is what allows "you stopped spending after turn 8" to be visible at
    /// all — a total can never show when something went wrong.
    /// </summary>
    internal sealed class TurnStat
    {
        public int Turn { get; set; }
        public int TavernTier { get; set; }

        public int Actions { get; set; }
        public double ShopSeconds { get; set; }

        /// <summary>
        /// When each action happened, in milliseconds from the start of this shop turn.
        ///
        /// Storing the timeline rather than only the count is what keeps every APM definition open:
        /// a rolling-window peak, a sustained per-turn rate, or whatever a later comparison needs.
        /// A busy match is a few hundred small integers, so the cost is nothing next to being locked
        /// into whichever formula happened to be written first.
        /// </summary>
        public List<int> ActionTimes { get; set; } = new List<int>();

        /// <summary>Parallel to <see cref="ActionTimes"/>: what each action was, for per-turn breakdowns.</summary>
        public List<string> ActionKinds { get; set; } = new List<string>();

        public int GoldSpent { get; set; }
        /// <summary>Gold still in hand when the shop closed.</summary>
        public int GoldLeftover { get; set; }

        public int MinionsBought { get; set; }
        public int MinionsSold { get; set; }
        public int Rolls { get; set; }

        public int BoardSize { get; set; }

        public int HeroHpStart { get; set; }
        public int HeroHpEnd { get; set; }

        /// <summary>"win" / "loss" / "draw", or null while the combat has not resolved.</summary>
        public string CombatResult { get; set; }
        public int DamageDealt { get; set; }
        public int DamageTaken { get; set; }

        [JsonIgnore]
        public double Apm => ShopSeconds >= 1 ? Actions / (ShopSeconds / 60.0) : 0;
    }
}
