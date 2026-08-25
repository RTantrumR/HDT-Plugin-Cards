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
        public int FreeRollsUsed { get; set; }
        public int TavernUpgrades { get; set; }
        public int Freezes { get; set; }
        public int TriplesCreated { get; set; }
        public int MinionsBought { get; set; }
        public int SpellsBought { get; set; }
        public int MinionsSold { get; set; }
        public int MinionsPlayed { get; set; }
        public int SpellsPlayed { get; set; }

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
        /// Actions per minute across shop time only. Combat is excluded on purpose: nothing can be
        /// done during it, so counting it would just punish long fights and make a slow player with
        /// short combats look faster than a fast one with long ones.
        /// </summary>
        [JsonIgnore]
        public double ApmAverage
        {
            get
            {
                if (Turns == null) return 0;
                double seconds = Turns.Sum(t => t.ShopSeconds);
                if (seconds < 1) return 0;
                return ActionCount / (seconds / 60.0);
            }
        }

        /// <summary>Busiest single shop turn. Turns shorter than 5s are ignored — they divide into noise.</summary>
        [JsonIgnore]
        public double ApmPeak
        {
            get
            {
                if (Turns == null) return 0;
                double best = 0;
                foreach (var t in Turns)
                {
                    if (t.ShopSeconds < 5) continue;
                    double apm = t.Actions / (t.ShopSeconds / 60.0);
                    if (apm > best) best = apm;
                }
                return best;
            }
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
