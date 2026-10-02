using System;
using System.Collections.Generic;
using System.Linq;

namespace HsbgCardLookup.Game.Recap
{
    /// <summary>What the recap panel shows, computed once per match from the record and its peers.
    /// The averages are null until enough history exists, and the panel then leaves them out
    /// rather than printing a dash that reads like an error.</summary>
    internal sealed class RecapText
    {
        /// <summary>Fewer comparable matches than this and there are no averages and no sentence.</summary>
        public const int MinHistory = 5;

        public string Hero;           // "Cenarius", or null
        public string Place;          // "1st", or null when unknown
        public int Turns;

        public double Apm;
        public double? AvgApm;
        public int BestTurn;          // 0 = none qualified
        public double BestTurnApm;
        public int PeakCount;         // most presses inside PeakWindow seconds
        public int PeakTurn;
        public double PeakWindow = RecapRecord.PeakWindowSeconds;

        public int Damage;
        public double PerCombat;
        public double? AvgPerCombat;

        public int HistoryCount;
        /// <summary>Each sentence with whether it is praise (true) or a nudge (false).</summary>
        public List<KeyValuePair<string, bool>> Sentences = new List<KeyValuePair<string, bool>>();

        public static RecapText Build(RecapRecord rec, IReadOnlyList<RecapRecord> history)
        {
            var t = new RecapText();
            int n = history != null ? history.Count : 0;
            bool enough = n >= MinHistory;

            t.Hero = rec.HeroName;
            t.Place = rec.Placement > 0 ? Ordinal(rec.Placement) : null;
            t.Turns = rec.LastTurn;
            t.Apm = rec.MatchApm;
            var best = rec.BestTurn;
            if (best != null) { t.BestTurn = best.Turn; t.BestTurnApm = best.Apm; }
            var peak = rec.PeakBurst;
            t.PeakCount = peak.Key;
            t.PeakTurn = peak.Value;
            t.Damage = rec.TotalDamage;
            t.PerCombat = rec.DamagePerCombat;
            t.HistoryCount = n;

            if (enough)
            {
                double avgApm = history.Average(r => r.MatchApm);
                double avgPer = history.Average(r => r.DamagePerCombat);
                t.AvgApm = avgApm;
                t.AvgPerCombat = avgPer;

                if (rec.MatchApm > avgApm)
                    t.Sentences.Add(new KeyValuePair<string, bool>(string.Format("APM above your average over your last {0} matches, keep it up!", n), true));
                else if (rec.MatchApm < avgApm)
                    t.Sentences.Add(new KeyValuePair<string, bool>(string.Format("APM below your average over your last {0} matches.", n), false));

                if (rec.CombatsFought > 0)
                {
                    if (rec.DamagePerCombat > avgPer)
                        t.Sentences.Add(new KeyValuePair<string, bool>("More damage per combat than you usually deal, keep it up!", true));
                    else if (rec.DamagePerCombat < avgPer)
                        t.Sentences.Add(new KeyValuePair<string, bool>("Less damage per combat than you usually deal.", false));
                }
            }
            return t;
        }

        private static string Ordinal(int n)
        {
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }
    }
}
