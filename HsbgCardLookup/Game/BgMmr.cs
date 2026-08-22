using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using Hearthstone_Deck_Tracker;                 // Core
using Hearthstone_Deck_Tracker.Enums;           // Region
using HearthDb.Enums;                            // GameTag
using HsbgCardLookup.Config;
using HsbgCardLookup.Net;
using HsbgCardLookup.Ui;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// Opt-in in-match feature: opponents' Battlegrounds MMR as per-portrait labels on the BG
    /// leaderboard (plus tavern tier, dead dimming, last-opponent marker). Rendering lives in
    /// <see cref="LeaderboardOverlay"/> on HDT's own overlay canvas; ratings/deltas come from the
    /// per-region blob <c>hsbg.cards/bgmmr/{REGION}.json</c> (duos: <c>{REGION}-duo.json</c> — a
    /// separate leaderboard), fetched once per match, disk-cache fallback. Solo and duos layouts.
    /// Overlay geometry + opponent/team tracking adapted from HDT-BGMMRPlugin (MIT) — see NOTICE
    /// (repo root).
    /// </summary>
    public sealed class BgMmr
    {
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;

        private DateTime _lastPoll = DateTime.MinValue;
        private volatile string _lastSig;
        private LeaderboardOverlay _overlay;
        private MmrSidePanel _panel;
        private bool _editing;   // arrange mode owns the panel (canvas thread only)

        private volatile bool _ended;
        private volatile bool _startFlag;
        private bool _wasBgMatch;
        private static BgMmr _current;
        private static bool _hooked;

        // Per-match state (all read/written on the OnUpdate thread).
        private readonly HashSet<int> _dead = new HashSet<int>();              // PLAYER_IDs, latched
        private readonly Dictionary<int, int> _lastTier = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _teammate = new Dictionary<int, int>();  // duos: pid -> teammate pid, latched
        private int _trackedOpponentId;
        private int _trackedOpponentTeammateId;   // duos: the opposing team's second member
        private int _combatOpponentId;
        private int _lastOpponentId;
        private bool _wasCombat;

        // Per-match leaderboard fetch state.
        private volatile Dictionary<string, int> _players;   // name -> rating
        private volatile Dictionary<string, int> _deltas;    // name -> today's change (non-zero only)
        private volatile Dictionary<string, string> _ciName; // case-insensitive name -> canonical blob name
        private string _fetchKey;                            // region + optional "-duo" suffix
        private DateTime _lastBlobFail = DateTime.MinValue;
        private volatile bool _fetching;

        public BgMmr(PluginConfig config, Dispatcher ui, Action<string> log)
        {
            _config = config; _ui = ui; _log = log;
            HookGameEvents();
        }

        // ── Poll (OnUpdate thread) ──────────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
                if (!_config.ShowOpponentMmr) { HideIfShown(); return; }

                bool isBg = false, isDuos = false;
                try
                {
                    var gg = Core.Game;
                    isBg = gg != null && gg.IsBattlegroundsMatch;
                    isDuos = isBg && gg.IsBattlegroundsDuosMatch;
                }
                catch { }
                // A fast requeue can go match→match without IsBattlegroundsMatch dipping false, so the
                // flip-reset alone can leave _ended latched — OnGameStart covers that (same fix as
                // DarkGiftWatcher).
                if (_startFlag) { _startFlag = false; _ended = false; ResetMatch(); }
                if (isBg && !_wasBgMatch) { _ended = false; ResetMatch(); }
                _wasBgMatch = isBg;

                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < 1000) return;
                _lastPoll = now;

                List<LeaderboardOverlay.Row> rows = null;
                if (isBg && !_ended)
                {
                    string region = CurrentRegion();
                    if (region != null) EnsureBlob(region, isDuos);
                    UpdateOpponentTracking();
                    rows = isDuos ? ReadStandingsDuos() : ReadStandings();
                }

                bool show = rows != null && rows.Count > 0;
                // Every per-part toggle folds into the signature so a settings change re-renders
                // ("D" = duos layout, "~" = blob still pending — rows render "…" until it loads).
                string flags = (isDuos ? "D" : "") + (_players == null ? "~" : "")
                    + (_config.ShowMmrLabels ? "L" : "") + (_config.ShowMmrPanel ? "P" : "")
                    + (_config.ShowOpponentNames ? "n" : "") + (_config.ShowMmrRating ? "r" : "")
                    + (_config.ShowMmrDeltas ? "a" : "") + "t" + _config.TavernTierMode
                    + (_config.ShowLastOpponent ? "o" : "") + (_config.DimDeadPlayers ? "d" : "");
                string sig = show
                    ? flags + "|" + string.Join(",", rows.Select(r =>
                        r.Name + "=" + r.Rating + "/" + r.Delta + "/" + r.TavernTier +
                        (r.IsDead ? "d" : "") + (r.IsLastOpponent ? "l" : "") + (r.IsCurrentOpponent ? "c" : "")))
                    : "0";
                if (sig == _lastSig) return;
                _lastSig = sig;

                var rr = show ? rows : null;
                bool duo = isDuos;
                Marshal(() => ApplyUi(rr, duo));
            }
            catch { /* OnUpdate must never throw */ }
        }

        public void OnSettingsChanged()
        {
            _lastSig = null;   // any toggle change → rebuild both surfaces on the next poll
            bool master = _config.ShowOpponentMmr;
            bool portraitAny = master && (_config.ShowMmrLabels || TiersOnPortraits || _config.ShowLastOpponent);
            bool panel = master && _config.ShowMmrPanel;
            Marshal(() =>
            {
                if (!portraitAny) _overlay?.HideAll();
                if (!panel && !_editing) _panel?.Hide();
            });
        }

        /// <summary>Arrange mode (shared with the HUD's "Arrange…" button): show the standings panel
        /// with sample data so it can be placed/resized out of a match. Needs Hearthstone running.</summary>
        public void SetEditMode(bool on)
        {
            Marshal(() =>
            {
                _editing = on;
                bool enabled = _config.ShowOpponentMmr && _config.ShowMmrPanel;
                if (on && !enabled) return;              // panel surface off → nothing to arrange
                if (on)
                {
                    EnsurePanel();
                    _panel.IsDuos = false;   // sample standings are a solo board
                    SyncPanelFlags();
                    _panel.SetEditMode(true);
                }
                else if (_panel != null)
                {
                    _panel.SetEditMode(false);
                    _lastSig = null;                     // next poll restores live standings if any
                }
            });
        }

        // ── Live standings: PLAYER_LEADERBOARD_PLACE order → name/rating/tier/flags ─────────────────
        private List<LeaderboardOverlay.Row> ReadStandings()
        {
            var outp = new List<LeaderboardOverlay.Row>();
            try
            {
                var g = Core.Game;
                if (g?.Entities == null) return outp;

                // Hero card id (skin/gold-normalized) -> player battletag, from the lobby roster.
                var heroToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lobby = HearthMirror.Reflection.Client?.GetBattlegroundsLobbyInfo();
                    if (lobby?.Players != null)
                        foreach (var p in lobby.Players)
                            if (!string.IsNullOrEmpty(p?.HeroCardId) && !string.IsNullOrWhiteSpace(p?.Name))
                                heroToName[NormHero(p.HeroCardId)] = p.Name;
                }
                catch { }

                // Ghost hero copies can briefly duplicate a place — prefer the in-play entity.
                var byPlace = new Dictionary<int, KeyValuePair<bool, LeaderboardOverlay.Row>>();
                foreach (var e in Snapshot(g.Entities.Values))
                {
                    int place; try { place = e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE); } catch { continue; }
                    if (place <= 0) continue;
                    string cid = e?.CardId;
                    if (string.IsNullOrEmpty(cid)) continue;

                    int pid = 0;
                    try { if (e.HasTag(GameTag.PLAYER_ID)) pid = e.GetTag(GameTag.PLAYER_ID); } catch { }
                    bool inPlay = false;
                    try { inPlay = e.IsInPlay; } catch { }

                    if (pid > 0)
                    {
                        // Tier can transiently read 0 while HS swaps hero entities → latch last valid.
                        try
                        {
                            if (e.HasTag(GameTag.PLAYER_TECH_LEVEL))
                            {
                                int t = e.GetTag(GameTag.PLAYER_TECH_LEVEL);
                                if (t >= 1 && t <= 7) _lastTier[pid] = t;
                            }
                        }
                        catch { }
                        // Death is latched — a BG player can't resurrect, and a later ghost copy may
                        // read full health again.
                        try { if (e.HasTag(GameTag.HEALTH) && e.Health <= 0) _dead.Add(pid); } catch { }
                    }

                    string name = heroToName.TryGetValue(NormHero(cid), out var n) ? n : null;
                    int rating = 0, delta = 0; bool pending = _players == null;
                    if (name != null)
                    {
                        LookupRating(name, out rating, out delta, out pending);
                    }
                    else
                    {
                        // No battletag (bot / blank-name lobby slot) → fall back to the hero name; such
                        // players are never on the leaderboard (8000↓).
                        try { name = e.Card?.Name; } catch { }
                        if (string.IsNullOrEmpty(name)) name = "?";
                    }

                    var row = new LeaderboardOverlay.Row
                    {
                        Name = name,
                        Rating = rating,
                        RatingPending = pending,
                        Delta = delta,
                        TavernTier = pid > 0 && _lastTier.TryGetValue(pid, out var lt) ? lt : 0,
                        IsDead = pid > 0 && _dead.Contains(pid),
                        IsLastOpponent = pid > 0 && pid == _lastOpponentId,
                        IsCurrentOpponent = pid > 0 && pid == _trackedOpponentId
                    };
                    if (!byPlace.TryGetValue(place, out var existing) || (inPlay && !existing.Key))
                        byPlace[place] = new KeyValuePair<bool, LeaderboardOverlay.Row>(inPlay, row);
                }
                outp = byPlace.OrderBy(kv => kv.Key).Select(kv => kv.Value.Value).ToList();
            }
            catch { }
            return outp;
        }

        // Exact-case lookup first, then case-insensitive via the canonical-name index (other data
        // sources may change a name's capitalization). Pending = the blob hasn't loaded yet ("…").
        private void LookupRating(string name, out int rating, out int delta, out bool pending)
        {
            rating = 0; delta = 0;
            var players = _players; var deltas = _deltas; var ci = _ciName;
            pending = players == null;
            if (pending || string.IsNullOrEmpty(name)) return;
            string key = name;
            if (!players.ContainsKey(key) && ci != null && ci.TryGetValue(name, out var canon)) key = canon;
            if (players.TryGetValue(key, out var r)) rating = r;   // else 0 = 8000↓
            if (deltas != null && deltas.TryGetValue(key, out var d)) delta = d;
        }

        // ── Duos standings: per-player records → teams → team-ordered rows ──────────────────────────
        // Duos teammates can SHARE a PLAYER_LEADERBOARD_PLACE, so unlike the solo path (deduped by
        // place) this one keys on PLAYER_ID. Team pairing/order mirrors HDT-BGMMRPlugin: explicit
        // teammate-tag links first (latched — an abandoned team can later receive transient ranks),
        // then equal-place, then adjacency; teams by best place; the player who fights first next
        // combat is listed first within a team (that's how the game stacks the paired portraits).
        private sealed class DuoRec
        {
            public int Pid;
            public string CardId;
            public string HeroName;    // fallback display name (bot / blank-name lobby slot)
            public int Place;          // 0 = unknown
            public bool FightsFirst;
            public bool InPlay;
        }

        private List<LeaderboardOverlay.Row> ReadStandingsDuos()
        {
            var outp = new List<LeaderboardOverlay.Row>();
            try
            {
                var g = Core.Game;
                if (g?.Entities == null) return outp;

                // Hero card id (skin/gold-normalized) -> player battletag, from the lobby roster.
                var heroToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lobby = HearthMirror.Reflection.Client?.GetBattlegroundsLobbyInfo();
                    if (lobby?.Players != null)
                        foreach (var p in lobby.Players)
                            if (!string.IsNullOrEmpty(p?.HeroCardId) && !string.IsNullOrWhiteSpace(p?.Name))
                                heroToName[NormHero(p.HeroCardId)] = p.Name;
                }
                catch { }

                var recs = new Dictionary<int, DuoRec>();
                var ffPids = new HashSet<int>();
                foreach (var e in Snapshot(g.Entities.Values))
                {
                    int pid = 0;
                    try { if (e.HasTag(GameTag.PLAYER_ID)) pid = e.GetTag(GameTag.PLAYER_ID); } catch { }
                    if (pid <= 0) continue;

                    // Team-link + fights-first tags can sit on any of the player's entities (hero or
                    // player entity — the reference plugin checks the hero first, then any entity with
                    // that PLAYER_ID), so read them before the hero-row filter below.
                    try
                    {
                        if (e.HasTag(GameTag.BACON_DUO_TEAMMATE_PLAYER_ID))
                        {
                            int mate = e.GetTag(GameTag.BACON_DUO_TEAMMATE_PLAYER_ID);
                            if (mate > 0 && mate != pid) _teammate[pid] = mate;   // latched
                        }
                    }
                    catch { }
                    try
                    {
                        if (e.HasTag(GameTag.BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT)
                            && e.GetTag(GameTag.BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT) > 0)
                            ffPids.Add(pid);
                    }
                    catch { }

                    int place = 0;
                    try { place = e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE); } catch { }
                    string cid = e?.CardId;
                    if (place <= 0 || string.IsNullOrEmpty(cid)) continue;   // hero rows only below

                    // Tier/death latches — same rules as solo (tier can transiently read 0 while HS
                    // swaps hero entities; death is permanent, ghost copies may read full health).
                    try
                    {
                        if (e.HasTag(GameTag.PLAYER_TECH_LEVEL))
                        {
                            int t = e.GetTag(GameTag.PLAYER_TECH_LEVEL);
                            if (t >= 1 && t <= 7) _lastTier[pid] = t;
                        }
                    }
                    catch { }
                    try { if (e.HasTag(GameTag.HEALTH) && e.Health <= 0) _dead.Add(pid); } catch { }

                    bool inPlay = false;
                    try { inPlay = e.IsInPlay; } catch { }
                    string heroName = null;
                    try { heroName = e.Card?.Name; } catch { }

                    if (!recs.TryGetValue(pid, out var rec))
                        recs[pid] = new DuoRec { Pid = pid, CardId = cid, HeroName = heroName, Place = place, InPlay = inPlay };
                    else if (inPlay && !rec.InPlay)   // ghost hero copies — prefer the in-play entity
                    {
                        rec.CardId = cid; rec.HeroName = heroName; rec.Place = place; rec.InPlay = true;
                    }
                }
                foreach (var pid in ffPids)
                    if (recs.TryGetValue(pid, out var rec)) rec.FightsFirst = true;

                var players = recs.Values.OrderBy(p => p.Pid).ToList();
                var ordered = BuildDuosTeams(players, _teammate)
                    .OrderBy(t => t.Select(p => p.Place).Where(pl => pl >= 1 && pl <= 8).DefaultIfEmpty(9).Min())
                    .SelectMany(t => t.OrderByDescending(p => p.FightsFirst).ThenBy(p => p.Pid))
                    .Take(8);

                foreach (var p in ordered)
                {
                    string name = heroToName.TryGetValue(NormHero(p.CardId), out var n) ? n : null;
                    int rating = 0, delta = 0; bool pending = _players == null;
                    if (name != null) LookupRating(name, out rating, out delta, out pending);
                    else name = string.IsNullOrEmpty(p.HeroName) ? "?" : p.HeroName;

                    outp.Add(new LeaderboardOverlay.Row
                    {
                        Name = name,
                        Rating = rating,
                        RatingPending = pending,
                        Delta = delta,
                        TavernTier = _lastTier.TryGetValue(p.Pid, out var lt) ? lt : 0,
                        IsDead = _dead.Contains(p.Pid),
                        IsLastOpponent = p.Pid == _lastOpponentId,
                        // Both members of the opposing team lean out with their portraits.
                        IsCurrentOpponent = p.Pid == _trackedOpponentId
                            || (_trackedOpponentTeammateId > 0 && p.Pid == _trackedOpponentTeammateId)
                    });
                }
            }
            catch { }
            return outp;
        }

        private static List<List<DuoRec>> BuildDuosTeams(List<DuoRec> players, Dictionary<int, int> teammate)
        {
            bool Linked(int a, int b) =>
                (teammate.TryGetValue(a, out var x) && x == b) || (teammate.TryGetValue(b, out var y) && y == a);

            var remaining = new List<DuoRec>(players);
            var teams = new List<List<DuoRec>>();
            // Explicit links first, so a player can't be consumed by a temporary-place fallback
            // before their partner is considered.
            foreach (var p in players)
            {
                if (!remaining.Contains(p)) continue;
                var linked = remaining.FirstOrDefault(c => !ReferenceEquals(c, p) && Linked(p.Pid, c.Pid));
                if (linked == null) continue;
                teams.Add(new List<DuoRec> { p, linked });
                remaining.Remove(p);
                remaining.Remove(linked);
            }
            // Fallback for links not exposed yet at match start: same place, then adjacency.
            while (remaining.Count > 0)
            {
                var p = remaining[0];
                remaining.RemoveAt(0);
                var mate = remaining.FirstOrDefault(c => p.Place >= 1 && c.Place == p.Place);
                if (mate == null && remaining.Count > 0) mate = remaining[0];
                var team = new List<DuoRec> { p };
                if (mate != null) { team.Add(mate); remaining.Remove(mate); }
                teams.Add(team);
            }
            return teams;
        }

        // ── Opponent tracking: NEXT_OPPONENT_PLAYER_ID + combat-edge latch ──────────────────────────
        private void UpdateOpponentTracking()
        {
            try
            {
                var g = Core.Game;
                int next = 0, nextMate = 0;
                try
                {
                    var pe = g?.PlayerEntity;
                    if (pe != null && pe.HasTag(GameTag.NEXT_OPPONENT_PLAYER_ID))
                        next = pe.GetTag(GameTag.NEXT_OPPONENT_PLAYER_ID);
                    if (pe != null && pe.HasTag(GameTag.NEXT_OPPONENT_TEAMMATE_PLAYER_ID))
                        nextMate = pe.GetTag(GameTag.NEXT_OPPONENT_TEAMMATE_PLAYER_ID);
                }
                catch { }
                int selfId = 0;
                try { selfId = g?.Player?.Id ?? 0; } catch { }
                if (next > 0 && next != selfId)
                {
                    _trackedOpponentId = next;
                    // Duos: the opposing team's second member — only adopted alongside a main-tag
                    // update, and only when it's a distinct player. Always 0 in solo.
                    _trackedOpponentTeammateId = nextMate > 0 && nextMate != next ? nextMate : 0;
                }

                bool isCombat = false;
                try { isCombat = g != null && g.IsBattlegroundsCombatPhase; } catch { }
                if (isCombat)
                {
                    if (_trackedOpponentId <= 0)
                        try { var o = g?.Opponent; if (o != null && o.Id > 0) _trackedOpponentId = o.Id; } catch { }
                    if (_trackedOpponentId > 0) _combatOpponentId = _trackedOpponentId;
                }
                else if (_wasCombat && _combatOpponentId > 0)
                {
                    // Combat just ended — only now does this opponent become the "last" one.
                    _lastOpponentId = _combatOpponentId;
                    _combatOpponentId = 0;
                }
                _wasCombat = isCombat;
            }
            catch { }
        }

        // ── Overlay + panel (canvas thread) ─────────────────────────────────────────────────────────
        private void ApplyUi(List<LeaderboardOverlay.Row> rows, bool isDuos)
        {
            try
            {
                if (_overlay == null) _overlay = new LeaderboardOverlay();
                _overlay.IsDuos = isDuos;
                bool any = rows != null && rows.Count > 0;

                // Surface 1: the portrait-anchored parts — the MMR/name label box (gated by
                // ShowMmrLabels), the tavern-tier icons (their own location axis: TavernTierMode
                // Portraits/Panel/Both/Off) and the ⚔ marker are all INDEPENDENT, so e.g. tiers can
                // sit by the portraits with the label box entirely off ("tiers only") — or live only
                // in the panel with nothing at the portraits at all.
                bool portraitAny = _config.ShowMmrLabels || TiersOnPortraits || _config.ShowLastOpponent;
                if (any && portraitAny)
                {
                    _overlay.ShowNames = _config.ShowMmrLabels && _config.ShowOpponentNames;
                    _overlay.ShowRating = _config.ShowMmrLabels && _config.ShowMmrRating;
                    _overlay.ShowDeltas = _config.ShowMmrLabels && _config.ShowMmrDeltas;
                    _overlay.ShowTiers = TiersOnPortraits;
                    _overlay.ShowLastOpp = _config.ShowLastOpponent;
                    _overlay.DimDead = _config.DimDeadPlayers;
                    _overlay.SetStandings(rows);
                }
                else _overlay.HideAll();

                // Surface 2: the separate draggable standings panel (skipped while arrange mode owns it).
                if (_editing) return;
                if (any && _config.ShowMmrPanel)
                {
                    EnsurePanel();
                    _panel.IsDuos = isDuos;
                    SyncPanelFlags();
                    _panel.SetStandings(rows);
                }
                else _panel?.Hide();
            }
            catch { }
        }

        private void EnsurePanel()
        {
            if (_panel != null) return;
            _panel = new MmrSidePanel();
            var p = _config.MmrPanelHud;
            if (p.WF > 0) _panel.Place(p.XF, p.YF, p.WF);
            _panel.GeometryChanged = (xf, yf, wf) =>
            {
                var pl = _config.MmrPanelHud;
                pl.Set = true; pl.XF = xf; pl.YF = yf; pl.WF = wf;
                try { _config.Save(); } catch { }
            };
        }

        private void SyncPanelFlags()
        {
            _panel.ShowNames = _config.ShowOpponentNames;
            _panel.ShowRating = _config.ShowMmrRating;
            _panel.ShowDeltas = _config.ShowMmrDeltas;
            _panel.ShowTiers = TiersInPanel;
            _panel.ShowLastOpp = _config.ShowLastOpponent;
            _panel.DimDead = _config.DimDeadPlayers;
        }

        // TavernTierMode ("Off"/"Portraits"/"Panel"/"Both"; unknown/empty = Both) → per-surface bools.
        private bool TiersOnPortraits
        {
            get
            {
                var m = _config.TavernTierMode;
                return string.IsNullOrEmpty(m)
                    || string.Equals(m, "Both", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m, "Portraits", StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool TiersInPanel
        {
            get
            {
                var m = _config.TavernTierMode;
                return string.IsNullOrEmpty(m)
                    || string.Equals(m, "Both", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m, "Panel", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void HideIfShown()
        {
            if (_overlay == null && _panel == null) return;
            _lastSig = null;
            Marshal(() =>
            {
                _overlay?.HideAll();
                if (!_editing) _panel?.Hide();
            });
        }

        public void CloseAll()
        {
            try
            {
                var o = _overlay; var p = _panel;
                _overlay = null; _panel = null;
                if (o != null || p != null)
                    (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.Invoke(new Action(() =>
                    {
                        o?.Detach();
                        p?.Close();
                    }));
            }
            catch { }
        }

        private void Marshal(Action action)
        {
            try { (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.BeginInvoke(action); } catch { }
        }

        // ── Leaderboard blob: fetch once per match+mode, disk-cache fallback ──────────────────────────
        // Key = region + optional "-duo" suffix (duos has its own leaderboard). A failed fetch backs
        // off 60s instead of retrying every poll tick — e.g. while the duo blob doesn't exist yet.
        private void EnsureBlob(string region, bool duos)
        {
            string key = region + (duos ? "-duo" : "");
            if (_fetching) return;
            if (string.Equals(_fetchKey, key, StringComparison.Ordinal))
            {
                if (_players != null) return;
                if ((DateTime.UtcNow - _lastBlobFail).TotalSeconds < 60) return;
            }
            _fetchKey = key;
            _fetching = true;
            Task.Run(async () =>
            {
                try { await FetchBlob(key); }
                finally { _fetching = false; }
            });
        }

        private async Task FetchBlob(string key)
        {
            string url = AssetClient.SiteBase + "/bgmmr/" + key + ".json";
            var json = await AssetClient.GetStringAsync(url);
            if (!string.IsNullOrEmpty(json) && Adopt(json, key, "loaded")) return;
            try
            {
                var path = Path.Combine(CacheDir, key + ".json");
                if (File.Exists(path) && Adopt(File.ReadAllText(path), key, "cache")) return;
            }
            catch { }
            _lastBlobFail = DateTime.UtcNow;
            _log?.Invoke($"BgMmr: leaderboard {key} unavailable (ratings pending)");
        }

        // Parse the blob into the players + deltas maps; on a live fetch also refresh the disk cache.
        private bool Adopt(string json, string key, string source)
        {
            Blob blob;
            try { blob = JsonConvert.DeserializeObject<Blob>(json); } catch { return false; }
            if (blob?.Players == null) return false;
            var players = new Dictionary<string, int>(blob.Players, StringComparer.Ordinal);
            _players = players;
            _deltas = blob.Deltas != null
                ? new Dictionary<string, int>(blob.Deltas, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            // Case-insensitive fallback index: CI name -> the canonical (highest-rated) blob name.
            var ci = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in players)
                if (!ci.TryGetValue(kv.Key, out var cur) || kv.Value > players[cur]) ci[kv.Key] = kv.Key;
            _ciName = ci;
            if (source == "loaded")
                try { Directory.CreateDirectory(CacheDir); File.WriteAllText(Path.Combine(CacheDir, key + ".json"), json); } catch { }
            _log?.Invoke($"BgMmr: leaderboard {key} {source} ({players.Count} players, {_deltas.Count} deltas)");
            return true;
        }

        private static string CacheDir => Path.Combine(PluginConfig.DataDir, "bgmmr-cache");

        internal static string CurrentRegion()
        {
            try
            {
                switch (Core.Game.CurrentRegion)
                {
                    case Region.US: return "US";
                    case Region.EU: return "EU";
                    case Region.ASIA: return "AP";
                    case Region.CHINA: return "CN";
                    default: return null;
                }
            }
            catch { return null; }
        }

        // Strip skin (_SKIN_*) + golden (_G) suffixes so a leaderboard-entity hero id matches the lobby's
        // HeroCardId across cosmetic variants.
        private static string NormHero(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return cardId;
            int i = cardId.IndexOf("_SKIN", StringComparison.OrdinalIgnoreCase);
            if (i > 0) cardId = cardId.Substring(0, i);
            if (cardId.EndsWith("_G", StringComparison.Ordinal)) cardId = cardId.Substring(0, cardId.Length - 2);
            return cardId;
        }

        private void ResetMatch()
        {
            _players = null; _deltas = null; _ciName = null; _fetchKey = null; _lastSig = null;
            _lastBlobFail = DateTime.MinValue;
            _dead.Clear(); _lastTier.Clear(); _teammate.Clear();
            _trackedOpponentId = 0; _trackedOpponentTeammateId = 0;
            _combatOpponentId = 0; _lastOpponentId = 0; _wasCombat = false;
        }

        private static IEnumerable<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity> Snapshot(
            IEnumerable<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity> src)
        {
            try { return src == null ? Enumerable.Empty<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>() : src.ToList(); }
            catch { return Enumerable.Empty<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>(); }
        }

        private void HookGameEvents()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => _current?.MarkEnded())); }
            catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => { var c = _current; if (c != null) c._startFlag = true; })); }
            catch { }
        }

        private void MarkEnded() { _ended = true; _lastSig = null; }

        private sealed class Blob
        {
            [JsonProperty("players")] public Dictionary<string, int> Players { get; set; }
            [JsonProperty("deltas")] public Dictionary<string, int> Deltas { get; set; }
        }
    }
}
