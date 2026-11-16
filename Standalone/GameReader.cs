using System;
using System.Collections.Generic;
using System.Diagnostics;
using MemSpike;

namespace Toolkit.Standalone
{
    /// <summary>One leaderboard tile, in the order the client draws them.</summary>
    internal sealed class Tile
    {
        public string HeroCardId;
        public int PlayerId, Place, Tier, Health;
        public string Name;                                    // display name from the lobby map
        public bool IsMe;
        public Dictionary<int, int> Races = new Dictionary<int, int>();
    }

    /// <summary>What one read of the client returned. Null fields = not available right now.</summary>
    internal sealed class Snapshot
    {
        public string Me;                                      // own display name
        public string Region;                                  // "US" / "EU" / "AP" / "CN", or null
        public List<Tile> Tiles = new List<Tile>();            // empty outside a BG match
        public List<int> LobbyTribes = new List<int>();        // TAG_RACE ids, empty outside a match
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

        public GameReader(Action<string> log) { _log = log; }

        public void Dispose() { Detach(); }

        private void Detach() { _mem?.Dispose(); _mem = null; _mono = null; _pid = 0; _statics = false; }

        /// <summary>Null when Hearthstone isn't running or can't be read yet.</summary>
        public Snapshot Read()
        {
            var procs = Process.GetProcessesByName("Hearthstone");
            if (procs.Length == 0) { if (_pid != 0) _log("Hearthstone exited"); Detach(); return null; }
            var p = procs[0];
            try
            {
                if (_pid != p.Id)
                {
                    Detach();
                    _mem = new Mem(p.Id);
                    _mono = new Mono(_mem);
                    _mono.Attach(p, "Assembly-CSharp");
                    _pid = p.Id;
                    _log("attached: " + _mono.Describe().Replace("\n", " | "));
                }
                if (!_statics)
                {
                    _statics = _mono.CalibrateStatics("BnetPresenceMgr", "s_instance");
                    if (!_statics) return null;
                    _log("statics calibrated");
                }
                var snap = Walk();
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

        private Snapshot Walk()
        {
            var m = _mono;
            var s = new Snapshot();

            long presence = m.StaticObj("BnetPresenceMgr", "s_instance");
            long myTag = m.Obj(m.Obj(m.Obj(presence, "m_myPlayer"), "m_account"), "m_battleTag");
            s.Me = m.Str(myTag, "m_name");
            s.Region = Region(m.Obj(m.Obj(presence, "m_myGameAccountId"), "<EntityId>k__BackingField"));

            long gs = m.StaticObj("GameState", "s_instance");
            if (gs == 0) return s;

            // Lobby: player id -> display name. Filled from Battle.net presence, so present in a real
            // match from the first read; empty for the opponents of a replay.
            var names = new Dictionary<int, string>();
            foreach (var kv in m.IntMap(m.Obj(gs, "m_playerInfoMap")))
                names[kv.Key] = m.Str(kv.Value, "m_name");

            // The tribes in this lobby (excluding the amalgam), a List<TAG_RACE>: 4-byte enums inline.
            long tribes = m.Obj(gs, "m_availableRacesInBattlegroundsExcludingAmalgam");
            long items = m.Obj(tribes, "_items");
            int count = Math.Min(m.Int(tribes, "_size"), m.ArrayLen(items));
            for (int i = 0; i < count && i < 32; i++)
            {
                int race = m.M.I32(items + 0x20 + i * 4L);
                if (race > 0) s.LobbyTribes.Add(race);
            }

            long mgr = m.StaticObj("PlayerLeaderboardManager", "s_instance");
            if (mgr == 0) return s;
            foreach (long team in m.List(m.Obj(mgr, "m_teams")))
                foreach (long card in m.List(m.Obj(team, "m_playerLeaderboardCards")))
                {
                    long hero = m.Obj(card, "m_playerHeroEntity");
                    var t = new Tile { HeroCardId = m.Str(hero, "m_cardIdInternal") };
                    int health = 0, damage = 0;
                    foreach (var tag in m.IntIntDict(m.Obj(m.Obj(hero, "m_tags"), "m_values")))
                    {
                        switch (tag.Key)
                        {
                            case 30: t.PlayerId = tag.Value; break;      // PLAYER_ID
                            case 1373: t.Place = tag.Value; break;       // PLAYER_LEADERBOARD_PLACE
                            case 1377: t.Tier = tag.Value; break;        // PLAYER_TECH_LEVEL
                            case 45: health = tag.Value; break;          // HEALTH
                            case 44: damage = tag.Value; break;          // DAMAGE
                        }
                    }
                    t.Health = health - damage;
                    foreach (var r in m.IntIntMap(m.Obj(m.Obj(card, "m_overlay"), "m_raceCounts"))) t.Races[r.Key] = r.Value;
                    string name;
                    if (names.TryGetValue(t.PlayerId, out name)) t.Name = name;
                    t.IsMe = t.Name != null && t.Name == s.Me;
                    s.Tiles.Add(t);
                }
            return s;
        }

        // Battle.net EntityId { high_, low_ }: the region is byte 4 of the high half
        // (1 = US, 2 = EU, 3 = Asia, 5 = China).
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
