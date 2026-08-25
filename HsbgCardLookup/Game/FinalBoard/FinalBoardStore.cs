using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HsbgCardLookup.Config;
using Newtonsoft.Json;
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;   // BattlegroundsLastGames

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// Our own on-disk history of finished Battlegrounds matches: one JSON file per game under
    /// <c>%APPDATA%\HearthstoneDeckTracker\HsbgCardLookup\matches\</c>.
    ///
    /// WHY THIS EXISTS AT ALL, given HDT already records final boards: **HDT prunes every game older
    /// than 7 days** (<c>BattlegroundsSessionViewModel.DeleteOldGames</c>), and its session list only
    /// shows the current session. Importing on every load is what turns a rolling 7-day window into
    /// a history. Verified against the live file: HDT's store had 4 games at rest and gained the
    /// finished match ~3.3s after OnGameEnd, rating already resolved.
    ///
    /// The store is deliberately dumb — no indexes, no caching layers. A record is a few KB and a
    /// heavy player accumulates a few hundred a season, so reading the folder once at startup costs
    /// less than the bookkeeping an index would need to stay correct.
    /// </summary>
    internal sealed class FinalBoardStore
    {
        private readonly Action<string> _log;
        private readonly string _dir;
        private readonly Dictionary<string, FinalBoardRecord> _byId =
            new Dictionary<string, FinalBoardRecord>(StringComparer.OrdinalIgnoreCase);
        // GameId → the file it lives in, so a save overwrites rather than accumulating duplicates.
        private readonly Dictionary<string, string> _paths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public FinalBoardStore(Action<string> log)
        {
            _log = log;
            _dir = Path.Combine(PluginConfig.DataDir, "matches");
        }

        public string Directory_ => _dir;

        /// <summary>Every record, newest first. Ordering is by start time, falling back to the id's text order.</summary>
        public IReadOnlyList<FinalBoardRecord> All
        {
            get
            {
                var list = _byId.Values.ToList();
                list.Sort((a, b) =>
                {
                    var ta = a.StartedAtUtc;
                    var tb = b.StartedAtUtc;
                    if (ta.HasValue && tb.HasValue) return tb.Value.CompareTo(ta.Value);
                    return string.Compare(b.GameId ?? "", a.GameId ?? "", StringComparison.Ordinal);
                });
                return list;
            }
        }

        public FinalBoardRecord Find(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            FinalBoardRecord r;
            return _byId.TryGetValue(gameId, out r) ? r : null;
        }

        // ── disk ────────────────────────────────────────────────────────────────────────────────
        public void Load()
        {
            _byId.Clear();
            _paths.Clear();
            try
            {
                if (!System.IO.Directory.Exists(_dir)) return;
                foreach (var file in System.IO.Directory.GetFiles(_dir, "*.json"))
                {
                    try
                    {
                        var rec = JsonConvert.DeserializeObject<FinalBoardRecord>(File.ReadAllText(file));
                        if (rec == null || string.IsNullOrEmpty(rec.GameId)) continue;
                        _byId[rec.GameId] = rec;
                        _paths[rec.GameId] = file;
                    }
                    catch (Exception ex) { Log("skipped " + Path.GetFileName(file) + ": " + ex.Message); }
                }
                Log("loaded " + _byId.Count + " record(s) from " + _dir);
            }
            catch (Exception ex) { Log("Load failed: " + ex.Message); }
        }

        /// <summary>
        /// Write one record. Atomic: a temp file is written and flushed first, then swapped in, so a
        /// crash mid-write cannot leave a half-JSON file that <see cref="Load"/> would then skip.
        /// </summary>
        public bool Save(FinalBoardRecord rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.GameId)) return false;
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                string path;
                if (!_paths.TryGetValue(rec.GameId, out path)) path = ResolvePath(rec);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(rec, Formatting.Indented));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);

                _byId[rec.GameId] = rec;
                _paths[rec.GameId] = path;
                return true;
            }
            catch (Exception ex)
            {
                Log("Save failed for " + rec.GameId + ": " + ex.Message);
                return false;   // history must never take the overlay down with it
            }
        }

        // A readable, sortable filename from the start time. Two games cannot start in the same
        // second, but a corrupted or hand-edited id could still collide, so a differing GameId takes
        // the next free suffix rather than overwriting someone else's match.
        private string ResolvePath(FinalBoardRecord rec)
        {
            var t = rec.StartedAtUtc;
            string stem = t.HasValue
                ? t.Value.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
                : "game-" + Math.Abs(rec.GameId.GetHashCode());
            if (rec.Duos) stem += "_duos";

            for (int i = 0; i < 50; i++)
            {
                string candidate = Path.Combine(_dir, i == 0 ? stem + ".json" : stem + "-" + (i + 1) + ".json");
                if (!File.Exists(candidate)) return candidate;
                try
                {
                    var existing = JsonConvert.DeserializeObject<FinalBoardRecord>(File.ReadAllText(candidate));
                    if (existing != null && string.Equals(existing.GameId, rec.GameId, StringComparison.OrdinalIgnoreCase))
                        return candidate;   // same match — overwrite it
                }
                catch { return candidate; }  // unreadable: reuse the slot rather than growing the folder
            }
            return Path.Combine(_dir, stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
        }

        // ── import from HDT ─────────────────────────────────────────────────────────────────────
        /// <summary>Copy anything HDT knows and we don't. Returns how many files were written.</summary>
        public int ImportFromHdt()
        {
            try
            {
                var inst = BattlegroundsLastGames.Instance;
                if (inst == null || inst.Games == null) return 0;
                return Import(inst.Games);
            }
            catch (Exception ex)
            {
                Log("ImportFromHdt failed: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Import path, taking the games explicitly so it can be driven from a test harness as well
        /// as from HDT's live singleton.
        ///
        /// A live record always wins: an import may only FILL fields the live capture left empty
        /// (the rating lands asynchronously, so a match captured at OnGameEnd genuinely can be
        /// missing it), never overwrite them and never downgrade Source back to "hdt".
        /// </summary>
        public int Import(IEnumerable<BattlegroundsLastGames.GameItem> games)
        {
            int written = 0;
            foreach (var g in games ?? Enumerable.Empty<BattlegroundsLastGames.GameItem>())
            {
                if (g == null || string.IsNullOrEmpty(g.StartTime)) continue;
                try
                {
                    var existing = Find(g.StartTime);
                    if (existing == null)
                    {
                        if (Save(FromHdt(g))) written++;
                    }
                    else if (Complete(existing, g))
                    {
                        if (Save(existing)) written++;
                    }
                }
                catch (Exception ex) { Log("import of " + g.StartTime + " failed: " + ex.Message); }
            }
            if (written > 0) Log("imported/updated " + written + " record(s) from HDT");
            return written;
        }

        private static FinalBoardRecord FromHdt(BattlegroundsLastGames.GameItem g)
        {
            return new FinalBoardRecord
            {
                GameId = g.StartTime,
                Source = "hdt",
                StartedAt = g.StartTime,
                EndedAt = g.EndTime,
                Duos = g.Duos,
                FriendlyGame = g.FriendlyGame,
                HeroCardId = g.Hero,
                Placement = g.Placement,
                Rating = g.Rating,
                RatingAfter = g.RatingAfter,
                SeasonReset = IsReset(g),
                Board = BoardOf(g),
            };
        }

        /// <summary>Fill blanks on an existing record from HDT's copy. Returns true if anything changed.</summary>
        private static bool Complete(FinalBoardRecord rec, BattlegroundsLastGames.GameItem g)
        {
            bool changed = false;
            if (!rec.RatingAfter.HasValue && g.RatingAfter.HasValue)
            {
                rec.RatingAfter = g.RatingAfter;
                rec.SeasonReset = IsReset(g);
                changed = true;
            }
            if (rec.Rating == 0 && g.Rating != 0) { rec.Rating = g.Rating; changed = true; }
            if (rec.Placement == 0 && g.Placement != 0) { rec.Placement = g.Placement; changed = true; }
            if (string.IsNullOrEmpty(rec.EndedAt) && !string.IsNullOrEmpty(g.EndTime)) { rec.EndedAt = g.EndTime; changed = true; }
            if (string.IsNullOrEmpty(rec.HeroCardId) && !string.IsNullOrEmpty(g.Hero)) { rec.HeroCardId = g.Hero; changed = true; }
            if ((rec.Board == null || rec.Board.Count == 0))
            {
                var board = BoardOf(g);
                if (board != null && board.Count > 0) { rec.Board = board; changed = true; }
            }
            return changed;
        }

        private static bool IsReset(BattlegroundsLastGames.GameItem g)
        {
            try { return BattlegroundsLastGames.IsRatingReset(g.Rating, g.RatingAfterOrCarriedForward); }
            catch { return false; }
        }

        private static List<MinionRecord> BoardOf(BattlegroundsLastGames.GameItem g)
        {
            var result = new List<MinionRecord>();
            if (g.FinalBoard == null || g.FinalBoard.FinalBoard == null) return result;
            foreach (var m in g.FinalBoard.FinalBoard)
            {
                if (m == null) continue;
                var tags = new Dictionary<int, int>();
                if (m.Tags != null)
                    foreach (var t in m.Tags)
                        if (t != null) tags[t.Tag] = t.Value;
                result.Add(new MinionRecord { CardId = m.CardId, Tags = tags });
            }
            return result;
        }

        private void Log(string msg)
        {
            try { if (_log != null) _log("[FinalBoardStore] " + msg); } catch { }
        }
    }
}
