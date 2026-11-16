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
        public long Reads;                                       // ReadProcessMemory calls this walk took

        /// <summary>The features' view of <see cref="Entities"/>, built once per snapshot (UI thread only).</summary>
        public IList<HsbgCardLookup.Host.IGameEntity> EntityList;

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
        // No string cache by address: the client's GC never moves a string but does reuse a freed
        // address, so a late-game card could inherit a removed card's id. Card ids are read when an
        // entity's pointer changes, which is rare enough to read them fresh.
        private long _gameFor;

        // The entity walk. A late Battlegrounds game holds 500-800 live entities, most of them buff
        // enchantments, combat copies and face-down placeholders that no feature reads. Each is
        // classified once and remembered for the life of the game object: enchantments and move
        // helpers are never read again, placeholders only until their card id shows. Field offsets
        // are resolved once per attach, so a needed entity costs a handful of reads instead of ~20.
        private const int TagCardType = 202, CardTypeGame = 1, CardTypePlayer = 2, CardTypeEnchantment = 6, CardTypeHoverTarget = 22;
        private sealed class EntityCache
        {
            public long Obj;              // the Entity object this id had when classified
            public long CardStr = -1;     // its m_cardIdInternal string pointer at the last read
            public string CardId;
            public bool Skip;             // an enchantment or move helper: never read again
            public bool Hidden;           // a face-down placeholder: only its card id is watched
        }
        private readonly Dictionary<int, EntityCache> _entities = new Dictionary<int, EntityCache>();
        private int _offCardId = -1, _offTags, _offValues, _offEntries, _offCount;

        public GameReader(Action<string> log) { _log = log; }

        public void Dispose() { Detach(); }

        private void Detach() { _mem?.Dispose(); _mem = null; _mono = null; _pid = 0; _statics = false; _entities.Clear(); _offCardId = -1; }

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
                long reads0 = _mem.Reads;
                var snap = Walk();
                snap.ReadMs = sw.ElapsedMilliseconds;
                snap.Reads = _mem.Reads - reads0;
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
            if (_gameFor != gs) { _entities.Clear(); _gameFor = gs; }

            // Lobby: display name + hero per player, from Battle.net presence.
            foreach (var kv in m.IntMap(m.Obj(gs, "m_playerInfoMap")))
            {
                long hero = m.Obj(kv.Value, "m_playerHero");
                s.Lobby.Add(new KeyValuePair<string, string>(m.Str(kv.Value, "m_name"), m.Str(hero, "m_cardIdInternal")));
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

            // The entities a feature can need: id -> card id + tags (see EntityCache above).
            var live = new HashSet<int>();
            foreach (var kv in m.IntMap(m.Obj(gs, "m_entityMap")))
            {
                if (kv.Value == 0) continue;
                live.Add(kv.Key);
                var e = ReadEntity(kv.Key, kv.Value);
                if (e != null) s.Entities[kv.Key] = e;
            }
            if (_entities.Count > live.Count + 256)                 // the client drops removed entities: so do we
                foreach (var id in new List<int>(_entities.Keys)) if (!live.Contains(id)) _entities.Remove(id);
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

        /// <summary>One entity for the snapshot, or null when it is one the features never read.</summary>
        private EntitySnap ReadEntity(int id, long obj)
        {
            if (_offCardId < 0 && !ResolveEntityOffsets(obj)) return null;
            EntityCache c;
            if (!_entities.TryGetValue(id, out c) || c.Obj != obj) _entities[id] = c = new EntityCache { Obj = obj };
            if (c.Skip) return null;

            long str = _mono.M.Ptr(obj + _offCardId);
            if (str != c.CardStr) { c.CardStr = str; c.CardId = _mono.Str(str); c.Hidden = false; }
            if (c.Hidden) return null;

            var tags = FastTags(obj);
            int type;
            tags.TryGetValue(TagCardType, out type);
            if (type == CardTypeEnchantment || type == CardTypeHoverTarget) { c.Skip = true; return null; }
            // No card id and not the game or a player: a face-down placeholder (other players' things).
            if (string.IsNullOrEmpty(c.CardId) && type != CardTypeGame && type != CardTypePlayer) { c.Hidden = true; return null; }
            return new EntitySnap { Id = id, CardId = c.CardId, Tags = tags };
        }

        // Offsets of Entity.m_cardIdInternal, Entity.m_tags, EntityTags.m_values and Dictionary's
        // _entries/_count, from the first entity seen. Player and GameEntity derive from Entity, and a
        // derived class keeps its base fields at the same offsets.
        private bool ResolveEntityOffsets(long entity)
        {
            var m = _mono;
            long card = m.FieldAddr(entity, "m_cardIdInternal"), tagsField = m.FieldAddr(entity, "m_tags");
            long tags = tagsField == 0 ? 0 : m.M.Ptr(tagsField);
            long valuesField = m.FieldAddr(tags, "m_values");
            long dict = valuesField == 0 ? 0 : m.M.Ptr(valuesField);
            long entries = m.FieldAddr(dict, "_entries"), count = m.FieldAddr(dict, "_count");
            if (card == 0 || tagsField == 0 || valuesField == 0 || entries == 0 || count == 0) return false;
            _offCardId = (int)(card - entity); _offTags = (int)(tagsField - entity); _offValues = (int)(valuesField - tags);
            _offEntries = (int)(entries - dict); _offCount = (int)(count - dict);
            _log("entity offsets: cardId 0x" + _offCardId.ToString("X") + " tags 0x" + _offTags.ToString("X") + " values 0x" + _offValues.ToString("X")
                + " entries 0x" + _offEntries.ToString("X") + " count 0x" + _offCount.ToString("X"));
            return true;
        }

        /// <summary>An entity's tags through the resolved offsets: tags object, dictionary, its header, its entries.</summary>
        private Dictionary<int, int> FastTags(long entity)
        {
            var mem = _mono.M;
            var d = new Dictionary<int, int>();
            long dict = mem.Ptr(mem.Ptr(entity + _offTags) + _offValues);
            if (dict == 0) return d;
            int lo = Math.Min(_offEntries, _offCount), hi = Math.Max(_offEntries + 8, _offCount + 4);
            var head = mem.Bytes(dict + lo, hi - lo);
            if (head == null) return d;
            long arr = BitConverter.ToInt64(head, _offEntries - lo);
            int n = BitConverter.ToInt32(head, _offCount - lo);
            if (arr == 0 || n <= 0 || n > 4096) return d;
            var e = mem.Bytes(arr + 0x20, n * 16);                  // Entry { hashCode, next, key, value }; _count <= length
            if (e == null) return d;
            for (int i = 0; i < n; i++)
                if (BitConverter.ToInt32(e, i * 16) >= 0) d[BitConverter.ToInt32(e, i * 16 + 8)] = BitConverter.ToInt32(e, i * 16 + 12);
            return d;
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
