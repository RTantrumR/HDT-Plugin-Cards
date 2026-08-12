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
    /// per-region blob <c>hsbg.cards/bgmmr/{REGION}.json</c> (fetched once per match, disk-cache
    /// fallback). Solo only. Overlay geometry + opponent tracking adapted from HDT-BGMMRPlugin
    /// (MIT) — see NOTICE (repo root).
    /// </summary>
    public sealed class BgMmr
    {
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;

        private DateTime _lastPoll = DateTime.MinValue;
        private volatile string _lastSig;
        private LeaderboardOverlay _overlay;

        private volatile bool _ended;
        private volatile bool _startFlag;
        private bool _wasBgMatch;
        private static BgMmr _current;
        private static bool _hooked;

        // Per-match state (all read/written on the OnUpdate thread).
        private readonly HashSet<int> _dead = new HashSet<int>();              // PLAYER_IDs, latched
        private readonly Dictionary<int, int> _lastTier = new Dictionary<int, int>();
        private int _trackedOpponentId;
        private int _combatOpponentId;
        private int _lastOpponentId;
        private bool _wasCombat;

        // Per-match leaderboard fetch state.
        private volatile Dictionary<string, int> _players;   // name -> rating
        private volatile Dictionary<string, int> _deltas;    // name -> today's change (non-zero only)
        private string _fetchRegion;
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
                if (isBg && !_ended && !isDuos)
                {
                    string region = CurrentRegion();
                    if (region != null) EnsureBlob(region);
                    UpdateOpponentTracking();
                    rows = ReadStandings();
                }

                bool show = rows != null && rows.Count > 0;
                bool names = _config.ShowOpponentNames;
                string sig = show
                    ? (names ? "n|" : "|") + string.Join(",", rows.Select(r =>
                        r.Name + "=" + r.Rating + "/" + r.Delta + "/" + r.TavernTier +
                        (r.IsDead ? "d" : "") + (r.IsLastOpponent ? "l" : "") + (r.IsCurrentOpponent ? "c" : "")))
                    : "0";
                if (sig == _lastSig) return;
                _lastSig = sig;

                var rr = show ? rows : null;
                Marshal(() => ApplyUi(rr, names));
            }
            catch { /* OnUpdate must never throw */ }
        }

        public void OnSettingsChanged()
        {
            _lastSig = null;   // name toggle / re-enable → rebuild the labels on the next poll
            if (!_config.ShowOpponentMmr) Marshal(() => _overlay?.HideAll());
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

                var players = _players; var deltas = _deltas;
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
                    int rating = 0, delta = 0;
                    if (name != null)
                    {
                        if (players != null && players.TryGetValue(name, out var r)) rating = r;   // else 0 = 8000↓
                        if (deltas != null && deltas.TryGetValue(name, out var d)) delta = d;
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

        // ── Opponent tracking: NEXT_OPPONENT_PLAYER_ID + combat-edge latch ──────────────────────────
        private void UpdateOpponentTracking()
        {
            try
            {
                var g = Core.Game;
                int next = 0;
                try
                {
                    var pe = g?.PlayerEntity;
                    if (pe != null && pe.HasTag(GameTag.NEXT_OPPONENT_PLAYER_ID))
                        next = pe.GetTag(GameTag.NEXT_OPPONENT_PLAYER_ID);
                }
                catch { }
                int selfId = 0;
                try { selfId = g?.Player?.Id ?? 0; } catch { }
                if (next > 0 && next != selfId) _trackedOpponentId = next;

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

        // ── Overlay (canvas/UI thread) ──────────────────────────────────────────────────────────────
        private void ApplyUi(List<LeaderboardOverlay.Row> rows, bool showNames)
        {
            try
            {
                if (_overlay == null) _overlay = new LeaderboardOverlay();
                if (rows == null || rows.Count == 0) { _overlay.HideAll(); return; }
                _overlay.ShowNames = showNames;
                _overlay.SetStandings(rows);
            }
            catch { }
        }

        private void HideIfShown()
        {
            if (_overlay != null) { _lastSig = null; Marshal(() => _overlay?.HideAll()); }
        }

        public void CloseAll()
        {
            try
            {
                var o = _overlay;
                _overlay = null;
                if (o != null) (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.Invoke(new Action(() => o.Detach()));
            }
            catch { }
        }

        private void Marshal(Action action)
        {
            try { (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.BeginInvoke(action); } catch { }
        }

        // ── Leaderboard blob: fetch once per match, disk-cache fallback ───────────────────────────────
        private void EnsureBlob(string region)
        {
            if (_fetching) return;
            if (string.Equals(_fetchRegion, region, StringComparison.Ordinal) && _players != null) return;
            _fetchRegion = region;
            _fetching = true;
            Task.Run(async () =>
            {
                try { await FetchBlob(region); }
                finally { _fetching = false; }
            });
        }

        private async Task FetchBlob(string region)
        {
            string url = AssetClient.SiteBase + "/bgmmr/" + region + ".json";
            var json = await AssetClient.GetStringAsync(url);
            if (!string.IsNullOrEmpty(json) && Adopt(json, region, "loaded")) return;
            try
            {
                var path = Path.Combine(CacheDir, region + ".json");
                if (File.Exists(path) && Adopt(File.ReadAllText(path), region, "cache")) return;
            }
            catch { }
            _log?.Invoke($"BgMmr: leaderboard {region} unavailable (all show 8000↓)");
        }

        // Parse the blob into the players + deltas maps; on a live fetch also refresh the disk cache.
        private bool Adopt(string json, string region, string source)
        {
            Blob blob;
            try { blob = JsonConvert.DeserializeObject<Blob>(json); } catch { return false; }
            if (blob?.Players == null) return false;
            _players = new Dictionary<string, int>(blob.Players, StringComparer.Ordinal);
            _deltas = blob.Deltas != null
                ? new Dictionary<string, int>(blob.Deltas, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            if (source == "loaded")
                try { Directory.CreateDirectory(CacheDir); File.WriteAllText(Path.Combine(CacheDir, region + ".json"), json); } catch { }
            _log?.Invoke($"BgMmr: leaderboard {region} {source} ({_players.Count} players, {_deltas.Count} deltas)");
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
            _players = null; _deltas = null; _fetchRegion = null; _lastSig = null;
            _dead.Clear(); _lastTier.Clear();
            _trackedOpponentId = 0; _combatOpponentId = 0; _lastOpponentId = 0; _wasCombat = false;
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
