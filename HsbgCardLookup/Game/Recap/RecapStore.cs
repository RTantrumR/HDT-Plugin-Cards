using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HsbgCardLookup.Config;
using Newtonsoft.Json;

namespace HsbgCardLookup.Game.Recap
{
    /// <summary>
    /// The recap's own history: one JSON per solo match under <c>DataDir\recaps\</c>. Kept apart
    /// from HDT's store (which prunes after 7 days) and from the final-board branch's records
    /// (which carry no shop window and no season). Averages are computed here, never stored, so
    /// a rule change re-scores every match on disk.
    /// </summary>
    internal sealed class RecapStore
    {
        private readonly string _dir;
        private readonly Action<string> _log;
        private readonly List<RecapRecord> _all = new List<RecapRecord>();

        public RecapStore(Action<string> log)
        {
            _log = log;
            _dir = Path.Combine(PluginConfig.DataDir, "recaps");
        }

        public IReadOnlyList<RecapRecord> All => _all;

        /// <summary>By HDT's key (CurrentGameStats.StartTime "o"), which the session rows carry too.</summary>
        public RecapRecord Find(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            return _all.FirstOrDefault(r => r.GameId == gameId);
        }

        public void Load()
        {
            _all.Clear();
            try
            {
                if (!Directory.Exists(_dir)) return;
                foreach (var path in Directory.GetFiles(_dir, "*.json"))
                {
                    try
                    {
                        var rec = JsonConvert.DeserializeObject<RecapRecord>(File.ReadAllText(path));
                        if (rec != null && rec.Turns != null) _all.Add(rec);
                    }
                    catch (Exception ex) { Log("skipped " + Path.GetFileName(path) + ": " + ex.Message); }
                }
                _all.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
            }
            catch (Exception ex) { Log("Load failed: " + ex.Message); }
        }

        /// <summary>Atomic write: tmp file then replace, so a crash mid-write cannot leave half a JSON.</summary>
        public bool Save(RecapRecord rec)
        {
            if (rec == null) return false;
            try
            {
                Directory.CreateDirectory(_dir);
                string path = Path.Combine(_dir, rec.StartedAt.ToString("yyyy-MM-dd_HH-mm-ss") + ".json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(rec, Formatting.Indented));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);

                _all.RemoveAll(r => r.GameId == rec.GameId);
                _all.Add(rec);
                _all.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
                return true;
            }
            catch (Exception ex)
            {
                Log("Save failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The matches a new one is compared against: same season label, after the last manual
        /// reset, with at least one fought combat, and never the match being scored itself.
        /// An empty season label matches only records that are also unlabelled.
        /// </summary>
        public List<RecapRecord> Comparable(string season, DateTime resetAt, RecapRecord exclude)
        {
            string s = season ?? "";
            return _all.Where(r => (r.Season ?? "") == s
                                   && r.StartedAt > resetAt
                                   && r.CombatsFought > 0
                                   && (exclude == null || r.GameId != exclude.GameId))
                       .ToList();
        }

        private void Log(string msg)
        {
            try { _log?.Invoke("[Recap] store: " + msg); } catch { }
        }
    }
}
