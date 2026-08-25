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
        private const int PollMs = 500;
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

                if (_config.RecordMatchHistory) _tracker.Poll();

                if (_finished || !_endedAt.HasValue) return;
                if ((now - _endedAt.Value).TotalSeconds > CaptureWindowSeconds)
                {
                    _finished = true;
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

            var x = _pending ?? ReadExtras();
            rec.Source = "live";
            rec.HeroPowerCardId = x.HeroPowerCardId;
            rec.Trinkets = x.Trinkets;
            rec.AnomalyDbfId = x.AnomalyDbfId;
            rec.AnomalyCardId = x.AnomalyCardId;
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

        // ── reads ───────────────────────────────────────────────────────────────────────────────
        private sealed class Extras
        {
            public string HeroPowerCardId;
            public string HeroPowerVia = "none";
            public List<MinionRecord> Trinkets;
            public int AnomalyDbfId;
            public string AnomalyCardId;
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
            try
            {
                int id = 0;
                try { if (g.PlayerEntity != null) id = g.PlayerEntity.GetTag(GameTag.HERO_POWER_ENTITY); } catch { }
                if (id > 0)
                {
                    Entity e;
                    if (g.Entities != null && g.Entities.TryGetValue(id, out e) && e != null && !string.IsNullOrEmpty(e.CardId))
                    {
                        x.HeroPowerCardId = e.CardId;
                        x.HeroPowerVia = "HERO_POWER_ENTITY";
                        return;
                    }
                }

                int me = -1;
                try { if (g.Player != null) me = g.Player.Id; } catch { }
                if (me < 0 || g.Entities == null) return;

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
                if (best != null)
                {
                    x.HeroPowerCardId = best.CardId;
                    x.HeroPowerVia = InPlay(best) ? "fallback:play" : "fallback:any";
                }
            }
            catch { }
        }

        private static bool InPlay(Entity e)
        {
            try { return e.GetTag(GameTag.ZONE) == (int)Zone.PLAY; } catch { return false; }
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

        private static MinionRecord ToRecord(Entity e)
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
