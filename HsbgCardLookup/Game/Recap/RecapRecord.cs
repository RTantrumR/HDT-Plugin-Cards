using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace HsbgCardLookup.Game.Recap
{
    /// <summary>
    /// One solo Battlegrounds match as the recap sees it: a shop-turn series of action counts,
    /// shop windows and combat damage. Nothing here is derived from another record; the averages
    /// live in <see cref="RecapStore"/> so that every stored match recomputes under the current rule.
    /// </summary>
    internal sealed class RecapRecord
    {
        /// <summary>HDT's own key, <c>CurrentGameStats.StartTime.ToString("o")</c>, or a local fallback.</summary>
        public string GameId { get; set; }
        public DateTime StartedAt { get; set; }
        /// <summary>The site's season label at capture time ("Season 14"); empty when unknown.</summary>
        public string Season { get; set; }
        public string HeroCardId { get; set; }
        /// <summary>English hero name from HearthDb at capture time; null when the id was unknown.</summary>
        public string HeroName { get; set; }
        /// <summary>1..8, or 0 when it could not be read.</summary>
        public int Placement { get; set; }
        public List<RecapTurn> Turns { get; set; } = new List<RecapTurn>();

        [JsonIgnore] public int ActionCount => Turns.Sum(t => t.Actions);
        [JsonIgnore] public double WindowSeconds => Turns.Sum(t => t.WindowSeconds);
        [JsonIgnore] public double ActiveSeconds => Turns.Sum(t => t.ActiveSeconds);

        /// <summary>
        /// Actions per ACTIVE minute: first press to last, each silent gap counted for at most
        /// <see cref="IdleCapSeconds"/>. The literal actions-per-shop-minute (<see cref="ShopApm"/>)
        /// came out at 8 on a match the player felt busy in — the shop timer's reading, waiting and
        /// five-second turns dilute it — while the same match was 22 per minute of actual play.
        /// </summary>
        [JsonIgnore] public double MatchApm => ActiveSeconds >= 1 ? ActionCount / (ActiveSeconds / 60.0) : 0;

        /// <summary>Actions per minute of the whole shop time, idle included.</summary>
        [JsonIgnore] public double ShopApm => WindowSeconds >= 1 ? ActionCount / (WindowSeconds / 60.0) : 0;

        /// <summary>The most a pause between two presses counts for: reading a discover, weighing a roll.</summary>
        public const double IdleCapSeconds = 10;

        /// <summary>
        /// Floor for the best-turn figure: fewer active seconds than this cannot win it, so four
        /// presses inside five seconds do not outscore a whole turn played fast.
        /// </summary>
        public const double BestTurnMinSeconds = 15;

        [JsonIgnore]
        public RecapTurn BestTurn => Turns
            .Where(t => t.ActiveSeconds >= BestTurnMinSeconds)
            .OrderByDescending(t => t.Apm)
            .FirstOrDefault();

        /// <summary>The window the peak burst is measured over.</summary>
        public const double PeakWindowSeconds = 10;

        /// <summary>The busiest <see cref="PeakWindowSeconds"/> of the match: how many presses, and on which turn.</summary>
        [JsonIgnore]
        public KeyValuePair<int, int> PeakBurst
        {
            get
            {
                int best = 0, turn = 0;
                foreach (var t in Turns)
                {
                    int n = t.BurstActions(PeakWindowSeconds);
                    if (n > best) { best = n; turn = t.Turn; }
                }
                return new KeyValuePair<int, int>(best, turn);
            }
        }

        [JsonIgnore] public int TotalDamage => Turns.Sum(t => t.DamageDealt);
        [JsonIgnore] public int CombatsFought => Turns.Count(t => t.Fought);
        [JsonIgnore] public double DamagePerCombat => CombatsFought > 0 ? (double)TotalDamage / CombatsFought : 0;
    }

    internal sealed class RecapTurn
    {
        /// <summary>Shop round, 1-based (the game's own TURN tag counts combats too).</summary>
        public int Turn { get; set; }
        public int Actions { get; set; }
        /// <summary>MAIN_ACTION to MAIN_END on the game entity, from the real-time log's own timestamps.</summary>
        public double WindowSeconds { get; set; }
        /// <summary>"log" when both edges carried a parsable timestamp, "clock" when the poll clock had to stand in.</summary>
        public string WindowSource { get; set; }
        /// <summary>Milliseconds from the window's start, one per action.</summary>
        public List<int> ActionTimes { get; set; } = new List<int>();
        /// <summary>Largest hit on the opponent's hero in the combat after this shop.</summary>
        public int DamageDealt { get; set; }
        /// <summary>False for the shop the match ended in (conceded, or the combat never resolved).</summary>
        public bool Fought { get; set; }

        /// <summary>In-play time: first press to last, each gap capped at <see cref="RecapRecord.IdleCapSeconds"/>. Zero under two presses.</summary>
        [JsonIgnore]
        public double ActiveSeconds
        {
            get
            {
                var times = ActionTimes;
                if (times == null || times.Count < 2) return 0;
                double ms = 0;
                for (int i = 1; i < times.Count; i++)
                    ms += Math.Min(RecapRecord.IdleCapSeconds * 1000.0, Math.Max(0, times[i] - times[i - 1]));
                return ms / 1000.0;
            }
        }

        /// <summary>Actions per ACTIVE minute of this turn.</summary>
        [JsonIgnore] public double Apm => ActiveSeconds >= 1 ? Actions / (ActiveSeconds / 60.0) : 0;

        /// <summary>Most presses inside any window of the given length (timestamps are sorted).</summary>
        public int BurstActions(double windowSeconds)
        {
            var times = ActionTimes;
            if (times == null || times.Count == 0) return 0;
            int best = 0, j = 0;
            double w = windowSeconds * 1000.0;
            for (int i = 0; i < times.Count; i++)
            {
                while (j < times.Count && times[j] - times[i] <= w) j++;
                if (j - i > best) best = j - i;
            }
            return best;
        }
    }
}
