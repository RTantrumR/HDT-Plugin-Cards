using System.Collections.Generic;

namespace HsbgCardLookup.Data
{
    /// <summary>"Most common minion type" from the game's own per-player race counts (what the
    /// leaderboard tile shows as "4 Beasts"), using the CLIENT'S exact rule — a port of
    /// <c>PlayerLeaderboardRecentCombatsPanel.SetRaces</c> (HS client, decompiled 2026-09-05):
    /// "All"-type minions count toward EVERY tribe, the top tribe wins with its total, and a tie for
    /// the top (or nothing but All-types) shows a type without a count.</summary>
    public static class TribeTally
    {
        /// <summary>Marker for the client's no-count case: 2+ tribes tied for the top, or only
        /// All-type minions. Rendered with the "All" art.</summary>
        public const string Mixed = "All";
        /// <summary>Marker for a board with no typed minions at all (neutral-only, or empty).</summary>
        public const string None = "None";

        private const int RaceAll = 26;   // TAG_RACE.ALL

        // TAG_RACE id → our tribe string (the bundled icon file names). Same numbering as HearthDb.Race.
        private static readonly Dictionary<int, string> RaceNames = new Dictionary<int, string>
        {
            { 11, "Undead" }, { 14, "Murloc" }, { 15, "Demon" }, { 17, "Mech" }, { 18, "Elemental" },
            { 20, "Beast" }, { 23, "Pirate" }, { 24, "Dragon" }, { 43, "Quilboar" }, { 92, "Naga" },
            { 126, "Aberration" },
        };

        /// <summary>The tribe to show and its count. <paramref name="raceCounts"/> is race id → count
        /// as the client holds it. Returns <see cref="None"/> (count 0) for no typed minions,
        /// <see cref="Mixed"/> (count 0) for the client's no-count case, else the tribe name with the
        /// client's displayed number (that tribe's count plus the All-types).</summary>
        public static string Dominant(IDictionary<int, int> raceCounts, out int count)
        {
            count = 0;
            if (raceCounts == null) return None;
            int all = raceCounts.TryGetValue(RaceAll, out var a) ? a : 0;

            int bestRace = RaceAll, best = all, second = 0;
            foreach (var kv in raceCounts)
            {
                if (kv.Key == RaceAll) continue;
                int total = kv.Value + all;
                if (total >= best && total > 0) { second = best; best = total; bestRace = kv.Key; }
                else if (total >= second && total > 0) second = total;
            }

            if (bestRace == RaceAll || best == second) return best == 0 ? None : Mixed;
            count = best;
            return RaceNames.TryGetValue(bestRace, out var name) ? name : Mixed;
        }
    }
}
