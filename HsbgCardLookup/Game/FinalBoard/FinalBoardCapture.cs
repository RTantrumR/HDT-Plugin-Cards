using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;                        // Core.Game
using Hearthstone_Deck_Tracker.Hearthstone;            // GameV2, Database
using Hearthstone_Deck_Tracker.Hearthstone.Entities;   // Entity
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;  // BattlegroundsLastGames
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// Captures the things only a running plugin can see when a Battlegrounds match ends — the hero
    /// power, the trinkets, the anomaly — and attaches them to the match HDT is about to record.
    ///
    /// It does NOT read the final board itself. HDT writes that into its own store ~3.3s after
    /// OnGameEnd, already correct, and re-deriving it here would mean two implementations that can
    /// disagree about the same match. So this waits for HDT's entry to appear, then merges our
    /// extras onto it. That also settles the identity problem for free: the record keeps HDT's own
    /// key (the match start time), so a later import can never produce a duplicate of the same game.
    ///
    /// Timing is built on what the live probe measured on 2026-08-25, not on guesswork:
    ///   • entities do NOT decay after OnGameEnd — hero, minions and trinkets were all still intact
    ///     30s later — so reading them a moment after the end is safe;
    ///   • CurrentGameStats' rating resolves ~400ms after the end (it is 0 at the event itself);
    ///   • HDT's store gains the game ~3.3s after the end.
    /// We snapshot our extras immediately (cheap, and it costs nothing to be early) and then wait up
    /// to <see cref="CaptureWindowSeconds"/> for HDT's entry.
    /// </summary>
    internal sealed class FinalBoardCapture
    {
        // 200 rather than 500: the tracker's B snapshot is frozen from its last rolling sample,
        // so its staleness equals this cadence — see MatchStatsTracker._rolling.
        private const int PollMs = 200;
        private const double CaptureWindowSeconds = 25;

        private readonly PluginConfig _config;
        private readonly FinalBoardStore _store;
        private readonly Action<string> _log;
        private readonly MatchStatsTracker _tracker;

        // GameEvents is an ActionList (Add only, no Remove), so handlers route through a static and
        // a plugin reload cannot end up with two live subscribers.
        private static FinalBoardCapture _current;
        private static bool _hooked;
        private volatile bool _startFlag, _endFlag;

        private DateTime _lastPoll = DateTime.MinValue;
        private DateTime? _endedAt;
        private bool _finished;
        private Extras _pending;
        private HashSet<string> _knownIds;

        // The record of the match in progress, written to disk every time the tracker's revision
        // moves — each closed turn and each resolved combat. A crash, a mid-match restart or an HDT
        // that never records the game can then cost at most the turn in progress, never the match.
        private FinalBoardRecord _live;
        private int _savedRevision;
        private bool _keyWarned;

        public FinalBoardCapture(PluginConfig config, FinalBoardStore store, Action<string> log)
        {
            _config = config;
            _store = store;
            _log = log;
            _tracker = new MatchStatsTracker(log);
            HookGameEvents();
        }

        public void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < PollMs) return;
                _lastPoll = now;

                if (_startFlag)
                {
                    _startFlag = false;
                    _tracker.Reset();
                    _endedAt = null;
                    _finished = false;
                    _pending = null;
                    _knownIds = null;
                    _live = null;
                    _savedRevision = 0;
                    _keyWarned = false;
                }
                if (_endFlag)
                {
                    _endFlag = false;
                    if (_config.RecordMatchHistory)
                    {
                        _endedAt = now;
                        _finished = false;
                        _knownIds = StoreIds();      // whatever HDT already had; ours is not in it yet
                        _pending = ReadExtras();     // entities are still alive at this point
                    }
                }

                if (_config.RecordMatchHistory)
                {
                    var g = Core.Game;
                    if (_tracker.Current == null) TryAdopt(g);
                    _tracker.Poll();
                    if (_tracker.Revision != _savedRevision) SaveLive(g, "turn");
                }

                if (_finished || !_endedAt.HasValue) return;
                if ((now - _endedAt.Value).TotalSeconds > CaptureWindowSeconds)
                {
                    _finished = true;
                    // HDT did not record the game. Ours is on disk already; close it out with what
                    // the end of the match can still tell us, so it renders as a finished match.
                    if (_live != null)
                    {
                        _live.EndedAt = DateTime.Now.ToString("o");
                        _live.Stats = _tracker.TakeSnapshot();
                        if (_live.Board == null || _live.Board.Count == 0) _live.Board = LastBoard(_live.Stats);
                        SaveLive(Core.Game, "match end without HDT's record");
                    }
                    Log("gave up waiting for HDT to record the match; tier-A data will still be imported on the next load");
                    return;
                }

                TryMerge();
            }
            catch (Exception ex) { Log("Poll error: " + ex.Message); }
        }

        /// <summary>Watch for the entry HDT adds for the match that just ended, then merge our extras onto it.</summary>
        private void TryMerge()
        {
            BattlegroundsLastGames.GameItem game = null;
            try
            {
                var inst = BattlegroundsLastGames.Instance;
                if (inst == null || inst.Games == null) return;
                foreach (var g in inst.Games.ToList())
                {
                    if (g == null || string.IsNullOrEmpty(g.StartTime)) continue;
                    if (_knownIds != null && _knownIds.Contains(g.StartTime)) continue;
                    // The newest of the unseen entries, in case more than one landed at once.
                    if (game == null || string.CompareOrdinal(g.StartTime, game.StartTime) > 0) game = g;
                }
            }
            catch (Exception ex) { Log("store read failed: " + ex.Message); return; }

            if (game == null) return;

            _finished = true;
            _store.Import(new[] { game });                 // tier A, HDT's own key and board

            var rec = _store.Find(game.StartTime);
            if (rec == null) { Log("merge failed: record missing right after import"); return; }

            // Normally HDT's key is the one the per-turn saves already used, so rec IS the live
            // record and the import has just filled in placement, rating and the final board. If
            // the keys differ, the live copy is a duplicate the moment its stats land on HDT's.
            if (_live != null && !ReferenceEquals(_live, rec))
            {
                Log("key mismatch: turns were saved under " + _live.GameId + ", HDT recorded " + game.StartTime + " — moving them");
                if (string.IsNullOrEmpty(rec.HeroPowerCardId)) rec.HeroPowerCardId = _live.HeroPowerCardId;
                if (rec.Trinkets == null) rec.Trinkets = _live.Trinkets;
                if (rec.AnomalyDbfId == 0) { rec.AnomalyDbfId = _live.AnomalyDbfId; rec.AnomalyCardId = _live.AnomalyCardId; }
                rec.DarkGiftLobby |= _live.DarkGiftLobby;
                _store.Delete(_live.GameId);
                _live = rec;
            }

            var x = _pending ?? ReadExtras();
            rec.Source = "live";
            rec.HeroPowerCardId = x.HeroPowerCardId;
            rec.Trinkets = x.Trinkets;
            rec.AnomalyDbfId = x.AnomalyDbfId;
            rec.AnomalyCardId = x.AnomalyCardId;
            rec.DarkGiftLobby = x.DarkGiftLobby;
            rec.PlayerName = x.PlayerName;
            rec.Turns = x.Turns;
            rec.Stats = _tracker.TakeSnapshot();
            if (string.IsNullOrEmpty(rec.HeroName)) rec.HeroName = HeroNameOf(rec.HeroCardId);

            _store.Save(rec);
            Log(string.Format("captured {0} | hero={1} place={2} mmr={3} | heroPower={4} ({5}) trinkets={6} anomaly={7} turns={8}",
                rec.GameId, rec.HeroName ?? rec.HeroCardId, rec.Placement,
                rec.MmrDelta.HasValue ? rec.MmrDelta.Value.ToString("+#;-#;0") : "?",
                rec.HeroPowerCardId ?? "-", x.HeroPowerVia,
                rec.Trinkets != null ? rec.Trinkets.Count : 0, rec.AnomalyCardId ?? "-", rec.Turns));
        }

        // ── the live record ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The key HDT will file this match under, computed the way HDT computes it at the end:
        /// <c>CurrentGameStats.StartTime.ToString("o")</c> (GameEventHandler.HandleGameEnd →
        /// BattlegroundsLastGames.AddGame, read from the decompiled 1.51.15). Using the same rule
        /// from turn one is what lets the end-of-match import land on the record the turns were
        /// already written to. One known exception: after a reconnect HDT restores the ORIGINAL
        /// start time at the very end, from an internal field this plugin cannot read — that case
        /// surfaces as a key mismatch in <see cref="TryMerge"/>, which moves the turns across.
        /// Null outside a match.
        /// </summary>
        private static string LiveKey(GameV2 g)
        {
            try
            {
                var stats = g != null ? g.CurrentGameStats : null;
                if (stats == null || stats.StartTime == DateTime.MinValue) return null;
                return stats.StartTime.ToString("o");
            }
            catch { return null; }
        }

        /// <summary>
        /// A match already on disk under this key — HDT or the plugin restarted mid-match — is
        /// continued, not replaced: the tracker adopts its stats before it would create its own.
        /// </summary>
        private void TryAdopt(GameV2 g)
        {
            try
            {
                if (g == null || !g.IsBattlegroundsMatch) return;
                var key = LiveKey(g);
                if (key == null) return;
                var rec = _store.Find(key);
                if (rec == null || rec.Stats == null || rec.Stats.Turns == null || rec.Stats.Turns.Count == 0) return;
                _tracker.Adopt(rec.Stats);
                _live = rec;
                _savedRevision = _tracker.Revision;
                Log("resuming " + key + " from disk: " + rec.Stats.Turns.Count + " turn(s) already recorded");
            }
            catch (Exception ex) { Log("adopt failed: " + ex.Message); }
        }

        /// <summary>Write the match in progress: whatever the tracker has, plus everything about the
        /// match that can be read now. Cheap enough to run once per turn (measured and logged).</summary>
        private void SaveLive(GameV2 g, string why)
        {
            try
            {
                var key = LiveKey(g);
                if (key == null)
                {
                    if (!_keyWarned) { Log("no game key yet — the turn stays in memory until there is one"); _keyWarned = true; }
                    return;
                }

                var rec = _live;
                if (rec != null && !string.Equals(rec.GameId, key, StringComparison.OrdinalIgnoreCase))
                {
                    // The key moved under us (a reconnect restoring the original start time). Follow
                    // it: same record, new name — unless that name is already taken, in which case
                    // both stay and the end-of-match merge sorts it out.
                    if (_store.Find(key) == null)
                    {
                        _store.Delete(rec.GameId);
                        rec.GameId = key;
                        rec.StartedAt = key;
                        Log("game key changed to " + key + "; the record follows it");
                    }
                    else rec = null;
                }
                if (rec == null) rec = _store.Find(key);
                if (rec == null) rec = new FinalBoardRecord { GameId = key, StartedAt = key };
                _live = rec;

                rec.Source = "live";
                FillLive(g, rec);
                if (rec.Stats == null || _tracker.Current != null) rec.Stats = _tracker.Current ?? rec.Stats;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = _store.Save(rec);
                _savedRevision = _tracker.Revision;
                int turns = rec.Stats != null && rec.Stats.Turns != null ? rec.Stats.Turns.Count : 0;
                Log(string.Format("{0}: {1} {2} | turns={3} ({4}ms)", why, ok ? "saved" : "SAVE FAILED", rec.GameId, turns, sw.ElapsedMilliseconds));
            }
            catch (Exception ex) { Log("SaveLive error: " + ex.Message); }
        }

        /// <summary>
        /// The facts about the match readable while it runs. The hero is resolved the way HDT
        /// resolves it for its own store (the leaderboard entity's card, skin folded to the base
        /// hero) so the live record and a later import agree on the id.
        /// </summary>
        private void FillLive(GameV2 g, FinalBoardRecord rec)
        {
            try
            {
                if (g == null) return;
                rec.Duos = MatchStatsTracker.IsDuos(g);
                try
                {
                    int me = g.Player != null ? g.Player.Id : -1;
                    Entity hero = null;
                    if (me >= 0 && g.Entities != null)
                        foreach (var e in g.Entities.Values.ToList())
                            if (e != null && e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) && e.IsControlledBy(me)) { hero = e; break; }
                    if (hero != null && !string.IsNullOrEmpty(hero.CardId))
                        rec.HeroCardId = BattlegroundsUtils.GetOriginalHeroId(hero.CardId) ?? hero.CardId;
                }
                catch { }
                if (string.IsNullOrEmpty(rec.HeroName)) rec.HeroName = HeroNameOf(rec.HeroCardId);
                try
                {
                    var s = g.CurrentGameStats;
                    if (s != null && rec.Rating == 0 && s.BattlegroundsRating > 0) rec.Rating = s.BattlegroundsRating;
                }
                catch { }

                var x = ReadExtras();
                if (!string.IsNullOrEmpty(x.PlayerName)) rec.PlayerName = x.PlayerName;
                if (x.Turns > rec.Turns) rec.Turns = x.Turns;
                if (!string.IsNullOrEmpty(x.HeroPowerCardId)) rec.HeroPowerCardId = x.HeroPowerCardId;
                if (x.Trinkets != null && x.Trinkets.Count > 0) rec.Trinkets = x.Trinkets;
                if (x.AnomalyDbfId > 0) { rec.AnomalyDbfId = x.AnomalyDbfId; rec.AnomalyCardId = x.AnomalyCardId; }
                rec.DarkGiftLobby |= x.DarkGiftLobby;
            }
            catch (Exception ex) { Log("FillLive error: " + ex.Message); }
        }

        /// <summary>The last board the turns saw — the pre-combat one of the final turn, which for a
        /// match HDT never recorded is the closest thing to a final board there is.</summary>
        private static List<MinionRecord> LastBoard(MatchStats s)
        {
            if (s == null || s.Turns == null) return null;
            for (int i = s.Turns.Count - 1; i >= 0; i--)
            {
                var t = s.Turns[i];
                var snap = t.SnapPreCombat ?? t.SnapEnd ?? t.SnapStart;
                if (snap != null && snap.Board != null && snap.Board.Count > 0) return snap.Board;
            }
            return null;
        }

        // ── reads ───────────────────────────────────────────────────────────────────────────────
        /// <summary>The Dark Gift button, probe-verified 2026-08-05 — the same id DarkGiftWatcher uses.</summary>
        private const string DarkGiftButtonId = "BG36_Button_DarkGift";

        private sealed class Extras
        {
            public string HeroPowerCardId;
            public string HeroPowerVia = "none";
            public List<MinionRecord> Trinkets;
            public int AnomalyDbfId;
            public string AnomalyCardId;
            public bool DarkGiftLobby;
            public string PlayerName;
            public int Turns;
        }

        private Extras ReadExtras()
        {
            var x = new Extras();
            try
            {
                var g = Core.Game;
                if (g == null) return x;

                try { if (g.Player != null) x.PlayerName = g.Player.Name; } catch { }
                try { x.Turns = g.CurrentGameStats != null ? g.CurrentGameStats.Turns : 0; } catch { }
                if (x.Turns <= 0) { try { x.Turns = g.GetTurnNumber(); } catch { } }

                x.Trinkets = new List<MinionRecord>();
                try
                {
                    var trinkets = g.Player != null && g.Player.Trinkets != null
                        ? g.Player.Trinkets.ToList() : new List<Entity>();
                    foreach (var t in trinkets.OrderBy(t => t != null ? t.Id : 0))
                        if (t != null) x.Trinkets.Add(ToRecord(t));
                }
                catch { }

                ReadHeroPower(g, x);
                ReadAnomaly(g, x);
                ReadDarkGiftLobby(g, x);
            }
            catch (Exception ex) { Log("ReadExtras error: " + ex.Message); }
            return x;
        }

        /// <summary>
        /// HERO_POWER_ENTITY on the PlayerEntity is the exact reference, but it read EMPTY in the
        /// live run on 2026-08-25 — which is the case Reign's plugin has a fallback for. So we take
        /// it when present and otherwise pick the player's own hero-power entity, preferring one
        /// that is actually in play. Which route was used is logged, so a wrong fallback shows up in
        /// the log rather than as a quietly wrong picture months later.
        /// </summary>
        private static void ReadHeroPower(GameV2 g, Extras x)
        {
            string via;
            var e = ResolveHeroPower(g, out via);
            if (e == null) return;
            x.HeroPowerCardId = e.CardId;
            x.HeroPowerVia = via;
        }

        /// <summary>The player's hero-power entity by the rule above, or null. Shared with the
        /// per-turn snapshots (<see cref="MatchStatsTracker"/>) so the two can never drift onto
        /// different hero powers; <paramref name="via"/> names the route for the log.</summary>
        internal static Entity ResolveHeroPower(GameV2 g, out string via)
        {
            via = "none";
            try
            {
                int id = 0;
                try { if (g.PlayerEntity != null) id = g.PlayerEntity.GetTag(GameTag.HERO_POWER_ENTITY); } catch { }
                if (id > 0)
                {
                    Entity e;
                    if (g.Entities != null && g.Entities.TryGetValue(id, out e) && e != null && !string.IsNullOrEmpty(e.CardId))
                    {
                        via = "HERO_POWER_ENTITY";
                        return e;
                    }
                }

                int me = -1;
                try { if (g.Player != null) me = g.Player.Id; } catch { }
                if (me < 0 || g.Entities == null) return null;

                Entity best = null;
                foreach (var e in g.Entities.Values.ToList())
                {
                    if (e == null || string.IsNullOrEmpty(e.CardId)) continue;
                    int type = 0, ctrl = 0;
                    try { type = e.GetTag(GameTag.CARDTYPE); } catch { }
                    if (type != (int)CardType.HERO_POWER) continue;
                    try { ctrl = e.GetTag(GameTag.CONTROLLER); } catch { }
                    if (ctrl != me) continue;
                    if (best == null) { best = e; continue; }
                    if (InPlay(e) && !InPlay(best)) best = e;
                }
                if (best == null) return null;
                via = InPlay(best) ? "fallback:play" : "fallback:any";
                return best;
            }
            catch { return null; }
        }

        private static bool InPlay(Entity e)
        {
            try { return e.GetTag(GameTag.ZONE) == (int)Zone.PLAY; } catch { return false; }
        }

        /// <summary>
        /// Dark Gifts announce themselves with a button entity and nothing else — no anomaly tag, no
        /// game-entity flag — so this looks for the same card id <c>DarkGiftWatcher</c> keys on. Read
        /// at match end while the entities are still up, because nothing afterwards remembers it.
        /// </summary>
        private static void ReadDarkGiftLobby(GameV2 g, Extras x)
        {
            try
            {
                var ents = g.Entities;
                if (ents == null) return;
                foreach (var e in ents.Values)
                {
                    if (e == null) continue;
                    if (string.Equals(e.CardId, DarkGiftButtonId, StringComparison.OrdinalIgnoreCase))
                    {
                        x.DarkGiftLobby = true;
                        return;
                    }
                }
            }
            catch { }
        }

        private static void ReadAnomaly(GameV2 g, Extras x)
        {
            try
            {
                int dbf = 0;
                try { if (g.GameEntity != null) dbf = g.GameEntity.GetTag(GameTag.BACON_GLOBAL_ANOMALY_DBID); } catch { }
                if (dbf <= 0)
                {
                    // HDT keeps it on the game's stats after it clears the live entities.
                    try
                    {
                        var d = g.CurrentGameStats != null ? g.CurrentGameStats.BattlegroundsDetails : null;
                        if (d != null && d.AnomalyDbfId.HasValue) dbf = d.AnomalyDbfId.Value;
                    }
                    catch { }
                }
                if (dbf <= 0) return;
                x.AnomalyDbfId = dbf;
                try
                {
                    var card = Database.GetCardFromDbfId(dbf, false);
                    if (card != null) x.AnomalyCardId = card.Id;
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// The displayed hero name, resolved the way HDT resolves it for its own session panel: a
        /// skin points at the parent card, and the name comes from that. Taking it from
        /// GameStats.PlayerHeroCardId would give the placeholder `TB_BaconShop_HERO_PH` instead.
        /// </summary>
        internal static string HeroNameOf(string heroCardId)
        {
            try
            {
                if (string.IsNullOrEmpty(heroCardId)) return null;
                var card = Database.GetCardFromId(heroCardId);
                if (card != null && card.BattlegroundsSkinParentId > 0)
                    card = Database.GetCardFromDbfId(card.BattlegroundsSkinParentId, false) ?? card;
                return card != null ? card.LocalizedName : null;
            }
            catch { return null; }
        }

        /// <summary>An entity as card id + its FULL tag dictionary. Shared with the per-turn
        /// snapshots for the hero power and the trinkets, where there are at most three per
        /// snapshot and the tags that matter (EXHAUSTED, script data) are outside the board
        /// whitelist.</summary>
        internal static MinionRecord ToRecord(Entity e)
        {
            var tags = new Dictionary<int, int>();
            try
            {
                if (e.Tags != null)
                    foreach (var kv in e.Tags.ToList()) tags[(int)kv.Key] = kv.Value;
            }
            catch { }
            return new MinionRecord { CardId = e.CardId, Tags = tags };
        }

        private HashSet<string> StoreIds()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var inst = BattlegroundsLastGames.Instance;
                if (inst != null && inst.Games != null)
                    foreach (var g in inst.Games.ToList())
                        if (g != null && !string.IsNullOrEmpty(g.StartTime)) set.Add(g.StartTime);
            }
            catch { }
            return set;
        }

        private void HookGameEvents()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => { if (_current != null) _current._startFlag = true; })); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => { if (_current != null) _current._endFlag = true; })); } catch { }
            try
            {
                Hearthstone_Deck_Tracker.API.GameEvents.OnEntityWillTakeDamage.Add(
                    new Action<Hearthstone_Deck_Tracker.API.PredamageInfo>(
                        info => { if (_current != null) _current._tracker.HandlePredamage(info); }));
            }
            catch { }
        }

        private void Log(string msg)
        {
            try { if (_log != null) _log("[FinalBoardCapture] " + msg); } catch { }
        }
    }
}
