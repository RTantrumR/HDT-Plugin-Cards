using System;
using System.Collections.Generic;
using System.Diagnostics;
using MemSpike;

namespace Toolkit.Standalone
{
    /// <summary>One entity of the client's own entity map, frozen at read time.</summary>
    internal sealed class EntitySnap
    {
        public int Id;
        public string CardId;
        public Dictionary<int, int> Tags;
    }

    /// <summary>One leaderboard tile, in the order the client draws them.</summary>
    internal sealed class TileSnap
    {
        public int HeroEntityId;
        public Dictionary<int, int> Races = new Dictionary<int, int>();
    }

    /// <summary>What one read of the client returned. Immutable once published.</summary>
    internal sealed class Snapshot
    {
        public string Region;                                    // "US" / "EU" / "AP" / "CN", or null
        public long GameStateAddr;                               // 0 = no game; a new address = a new game object
        public Dictionary<int, EntitySnap> Entities = new Dictionary<int, EntitySnap>();
        public int GameEntityId, LocalPlayerEntityId, OpponentPlayerEntityId;
        public List<TileSnap> Tiles = new List<TileSnap>();      // screen order
        public int TeamsWithTwo;                                 // duos: teams holding two tiles
        public List<KeyValuePair<string, string>> Lobby = new List<KeyValuePair<string, string>>();   // name, hero card id
        public List<int> LobbyTribes = new List<int>();
        public long ReadMs;

        /// <summary>A Battlegrounds game: it has lobby minion types from its creation, tiles later.</summary>
        public bool IsBattlegrounds => GameStateAddr != 0 && (LobbyTribes.Count > 0 || Tiles.Count > 0);
    }

    /// <summary>
    /// Reads the Hearthstone client with the spike's Mono walker. Keeps one attachment for as long as
    /// the process lives and re-attaches when it changes or a read throws (the game loads its code
    /// for ~20 s after launch, and classes have no static block until first used).
    /// </summary>
    internal sealed class GameReader : IDisposable
    {
        private readonly Action<string> _log;
        private Mem _mem;
        private Mono _mono;
        private int _pid;
        private bool _statics;
        private string _lastError;
        // Strings in the client never move (Boehm GC, non-moving) and card ids repeat endlessly:
        // remember them by address for the life of one game object.
        private readonly Dictionary<long, string> _strings = new Dictionary<long, string>();
        private long _stringsFor;

        public GameReader(Action<string> log) { _log = log; }

        public void Dispose() { Detach(); }

        private void Detach() { _mem?.Dispose(); _mem = null; _mono = null; _pid = 0; _statics = false; _strings.Clear(); }

        /// <summary>Null when Hearthstone isn't running or can't be read yet.</summary>
        public Snapshot Read()
        {
            int pid;
            HsWindow.Find(out pid);      // never a process list: see HsWindow
            if (pid == 0) { if (_pid != 0) _log("Hearthstone exited"); Detach(); return null; }
            try
            {
                if (_pid != pid)
                {
                    Detach();
                    _mem = new Mem(pid);
                    _mono = new Mono(_mem);
                    using (var p = Process.GetProcessById(pid)) _mono.Attach(p, "Assembly-CSharp");
                    _pid = pid;
                    _log("attached: " + _mono.Describe().Replace("\n", " | "));
                }
                if (!_statics)
                {
                    _statics = _mono.CalibrateStatics("BnetPresenceMgr", "s_instance");
                    if (!_statics) return null;
                    _log("statics calibrated");
                }
                var sw = Stopwatch.StartNew();
                var snap = Walk();
                snap.ReadMs = sw.ElapsedMilliseconds;
                _lastError = null;
                return snap;
            }
            catch (Exception ex)
            {
                string msg = ex.GetType().Name + ": " + ex.Message;
                if (msg != _lastError) { _log("read failed: " + msg); _lastError = msg; }
                if (_mono == null || _mono.Image == 0) Detach();      // attach itself failed: start over next time
                return null;
            }
        }

        private string CachedStr(long addr)
        {
            if (addr == 0) return null;
            string s;
            if (_strings.TryGetValue(addr, out s)) return s;
            s = _mono.Str(addr);
            if (s != null) _strings[addr] = s;
            return s;
        }

        private Dictionary<int, int> TagsOf(long entity)
        {
            var d = new Dictionary<int, int>();
            foreach (var t in _mono.IntIntDict(_mono.Obj(_mono.Obj(entity, "m_tags"), "m_values"))) d[t.Key] = t.Value;
            return d;
        }

        private Snapshot Walk()
        {
            var m = _mono;
            var s = new Snapshot();

            long presence = m.StaticObj("BnetPresenceMgr", "s_instance");
            s.Region = Region(m.Obj(m.Obj(presence, "m_myGameAccountId"), "<EntityId>k__BackingField"));

            long gs = m.StaticObj("GameState", "s_instance");
            s.GameStateAddr = gs;
            if (gs == 0) return s;
            if (_stringsFor != gs) { _strings.Clear(); _stringsFor = gs; }

            // Lobby: display name + hero per player, from Battle.net presence.
            foreach (var kv in m.IntMap(m.Obj(gs, "m_playerInfoMap")))
            {
                long hero = m.Obj(kv.Value, "m_playerHero");
                s.Lobby.Add(new KeyValuePair<string, string>(m.Str(kv.Value, "m_name"), CachedStr(m.Obj(hero, "m_cardIdInternal"))));
            }

            foreach (int race in m.IntList(m.Obj(gs, "m_availableRacesInBattlegroundsExcludingAmalgam")))
                if (race > 0) s.LobbyTribes.Add(race);

            long mgr = m.StaticObj("PlayerLeaderboardManager", "s_instance");
            if (mgr != 0)
                foreach (long team in m.List(m.Obj(mgr, "m_teams")))
                {
                    int cards = 0;
                    foreach (long card in m.List(m.Obj(team, "m_playerLeaderboardCards")))
                    {
                        cards++;
                        var t = new TileSnap();
                        int id;
                        if (TagsOf(m.Obj(card, "m_playerHeroEntity")).TryGetValue(53, out id)) t.HeroEntityId = id;
                        foreach (var r in m.IntIntMap(m.Obj(m.Obj(card, "m_overlay"), "m_raceCounts"))) t.Races[r.Key] = r.Value;
                        s.Tiles.Add(t);
                    }
                    if (cards >= 2) s.TeamsWithTwo++;
                }

            // Standard, Arena, Tavern Brawl...: nothing reads their entities, so don't walk them. Only a
            // Battlegrounds game has lobby minion types (from its creation) or leaderboard tiles.
            if (!s.IsBattlegrounds) return s;

            // Every entity the client holds: id -> card id + tags.
            foreach (var kv in m.IntMap(m.Obj(gs, "m_entityMap")))
            {
                if (kv.Value == 0) continue;
                s.Entities[kv.Key] = new EntitySnap
                {
                    Id = kv.Key,
                    CardId = CachedStr(m.Obj(kv.Value, "m_cardIdInternal")),
                    Tags = TagsOf(kv.Value)
                };
            }
            long gameEntity = m.Obj(gs, "m_gameEntity");
            if (gameEntity != 0)
            {
                int id;
                if (TagsOf(gameEntity).TryGetValue(53, out id)) s.GameEntityId = id;      // ENTITY_ID
            }

            // The two Player objects: the local one, and whoever it faces now.
            foreach (var kv in m.IntMap(m.Obj(gs, "m_playerMap")))
            {
                long local = m.FieldAddr(kv.Value, "m_local");
                bool isLocal = local != 0 && (m.M.I32(local) & 0xFF) != 0;
                if (isLocal) s.LocalPlayerEntityId = kv.Key; else s.OpponentPlayerEntityId = kv.Key;
            }
            return s;
        }

        // Battle.net EntityId { high_, low_ }: the region is byte 4 of the high half of the game
        // account logged in now (1 = US, 2 = EU, 3 = Asia, 5 = China).
        private string Region(long entityId)
        {
            if (entityId == 0) return null;
            long hiAddr = _mono.FieldAddr(entityId, "high_");
            if (hiAddr == 0) return null;
            ulong hi = (ulong)_mono.M.Ptr(hiAddr);
            switch ((hi >> 32) & 0xFF)
            {
                case 1: return "US";
                case 2: return "EU";
                case 3: return "AP";
                case 5: return "CN";
                default: return null;
            }
        }
    }
}
