using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Hearthstone_Deck_Tracker;                       // Core
using Hearthstone_Deck_Tracker.Hearthstone;           // GameV2, Player
using Hearthstone_Deck_Tracker.Hearthstone.Entities;  // Entity
using HearthDb.Enums;                                  // GameTag
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// Opt-in match recorder (numbers only — no screenshots). On the combat-phase boundaries it reads
    /// the player's whole board + context (hero/HP/tier/gold/trinkets/anomaly) and writes a row per minion
    /// to a per-match CSV — a sortable timeline.
    ///
    /// <b>Writes incrementally, the moment each snapshot is captured</b> (the file is created on the first
    /// capture and each snapshot is appended + flushed to disk immediately, the handle closed each time).
    /// So a crash, an error, or the player quitting early never loses the rows already recorded — there is
    /// no end-of-match buffer to lose.
    ///
    /// Two snapshots per round, both read during the stable RECRUIT phase (board reads are reliable there;
    /// HDT recreates entities during combat):
    ///   • <b>End of Turn</b>   — at recruit→combat: your final board going into the battle (round N).
    ///   • <b>Start of Turn</b> — one tick after combat→recruit: the survivors entering the next shop,
    ///     labelled round N+1 (the turn just beginning) so it pairs with that round's End of Turn.
    ///
    /// Driven by <see cref="IPlugin.OnUpdate"/> via <see cref="Poll"/> (throttled). Pure read — never
    /// mutates the game. All state access is wrapped in try/catch because HDT mutates these collections on
    /// its own threads; everything runs on the OnUpdate thread (the OnGameEnd event only sets a latch).
    ///
    /// Deliberately NOT implemented yet: the "player won = last opponent died" event (needs lobby
    /// standings) — the final End-of-Turn board + game-end already capture the endgame.
    /// </summary>
    public sealed class MatchRecorder
    {
        private const int PollMs = 300;          // phase transitions last seconds; 300ms catches them
        private const int PostCombatSettleMs = 600;  // let the recruit board re-populate before reading survivors

        private static readonly string[] Header =
        {
            "Match","Round","Phase","Time","Hero","HeroHP","TavernTier","Gold",
            "Trinkets","Anomaly","PlayerEnchantments","Slot","Minion","Golden","Attack","Health","Tier","Keywords","Enchantments"
        };

        // Live keyword GameTags we surface (value != 0 = present). Windfury is special-cased (mega first).
        private static readonly (GameTag Tag, string Label)[] KeywordTags =
        {
            (GameTag.TAUNT, "Taunt"),
            (GameTag.DIVINE_SHIELD, "Divine Shield"),
            (GameTag.POISONOUS, "Poisonous"),
            (GameTag.VENOMOUS, "Venomous"),
            (GameTag.REBORN, "Reborn"),
            (GameTag.STEALTH, "Stealth"),
        };

        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly Action<string> _log;

        // Match / phase state (all touched only on the OnUpdate thread).
        private DateTime _lastPoll = DateTime.MinValue;
        private bool _matchActive;
        private bool _wasBgMatch;
        private DateTime _matchStart;
        private string _matchLabel;              // the Match column value (match start, fixed for the match)
        private string _matchHero;               // resolved once we can read the hero
        private string _matchAnomaly;            // resolved once per match (doesn't change)

        private bool _phaseKnown;
        private bool _inCombat;
        private int _round;
        private bool _pendingPost;               // a combat just ended; capture survivors after a short settle
        private DateTime _combatEndedAt;

        private string _csvPath;                 // null until the first capture creates the file (+ header)
        private int _snapshotCount;

        // OnGameEnd latch (HDT keeps IsBattlegroundsMatch true through the placement screen, so the event
        // is the reliable end signal). ActionList has Add but no Remove → route via a static current.
        private volatile bool _endRequested;
        private static MatchRecorder _current;
        private static bool _hooked;

        public MatchRecorder(CardStore store, PluginConfig config, Action<string> log)
        {
            _store = store; _config = config; _log = log;
            HookGameEnd();
        }

        // ── Poll (OnUpdate thread) ───────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
                if (!_config.ExportMatchBoards)
                {
                    if (_matchActive) Reset();   // feature toggled off mid-match → stop cleanly
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < PollMs) return;
                _lastPoll = now;

                var g = Core.Game;
                bool isBg = false;
                try { isBg = g != null && g.IsBattlegroundsMatch; } catch { }

                // Match boundaries.
                if (isBg && !_wasBgMatch) StartMatch();
                _wasBgMatch = isBg;

                // End signal (event latch, or simply leaving the BG match). Rows are already on disk —
                // nothing to flush; just log + reset.
                if (_matchActive && (_endRequested || !isBg))
                {
                    if (_snapshotCount > 0) Log($"match complete: {_snapshotCount} snapshots → {_csvPath}");
                    else Log("match ended — nothing recorded");
                    Reset();
                    return;
                }

                if (!_matchActive || !isBg) return;

                bool combat;
                try { combat = g.IsBattlegroundsCombatPhase; } catch { return; }

                if (!_phaseKnown) { _inCombat = combat; _phaseKnown = true; return; }

                if (combat != _inCombat)
                {
                    if (combat)
                    {
                        // recruit → combat: the board is locked for battle = the END of this shop turn.
                        _round++;
                        Capture(g, "End of Turn", _round);
                        _pendingPost = false;   // any uncaptured prior survivors are now stale
                    }
                    else
                    {
                        // combat → recruit: defer the survivor read until the board re-settles — it's the
                        // START of the next turn's shop.
                        _pendingPost = true;
                        _combatEndedAt = now;
                    }
                    _inCombat = combat;
                }
                else if (!combat && _pendingPost && (now - _combatEndedAt).TotalMilliseconds >= PostCombatSettleMs)
                {
                    // Survivors entering the next shop → that's the start of round (_round + 1).
                    Capture(g, "Start of Turn", _round + 1);
                    _pendingPost = false;
                }
            }
            catch { /* OnUpdate must never throw */ }
        }

        // ── Match lifecycle ──────────────────────────────────────────────────────────────────────
        private void StartMatch()
        {
            _matchActive = true;
            _endRequested = false;
            _matchStart = DateTime.Now;
            _matchLabel = _matchStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            _matchHero = null;
            _matchAnomaly = null;
            _phaseKnown = false;
            _inCombat = false;
            _round = 0;
            _pendingPost = false;
            _csvPath = null;
            _snapshotCount = 0;
            Log("match recording started");
        }

        private void Reset()
        {
            _matchActive = false;
            _endRequested = false;
            _phaseKnown = false;
            _pendingPost = false;
            _csvPath = null;
            _snapshotCount = 0;
        }

        // ── Capture one snapshot (OnUpdate thread) → write it immediately ───────────────────────────
        private void Capture(GameV2 g, string phase, int round)
        {
            try
            {
                var snap = new BoardSnapshot { Round = round, Phase = phase, Time = DateTime.Now };

                // Pre-group noise-filtered enchantments by their host entity id (ATTACHED) — feeds both the
                // per-minion and the player/hero enchantment columns.
                var byId = g.Entities;
                var enchByHost = GroupEnchantments(Snapshot(byId?.Values));
                int heroId = -1;

                Player player = g.Player;
                if (player != null)
                {
                    // Hero + context.
                    Entity hero = null;
                    try { hero = player.Hero; } catch { }
                    if (hero != null)
                    {
                        try { heroId = hero.Id; } catch { }
                        snap.Hero = NameOf(hero) ?? "";
                        if (string.IsNullOrEmpty(_matchHero) && !string.IsNullOrEmpty(snap.Hero)) _matchHero = snap.Hero;
                        try { snap.HeroHp = hero.Health + hero.GetTag(GameTag.ARMOR); } catch { }
                    }

                    // Board minions.
                    foreach (var e in Snapshot(player.Minions))
                    {
                        if (e == null) continue;
                        var row = new MinionRow();
                        try { row.Slot = e.GetTag(GameTag.ZONE_POSITION); } catch { }
                        row.Golden = IsGolden(e.CardId);
                        var card = _store.Lookup(StripGold(e.CardId));
                        row.Name = card?.Name ?? NameOf(e) ?? e.CardId ?? "";
                        row.Tier = card?.Tier;
                        try { row.Attack = e.Attack; } catch { }
                        try { row.Health = e.Health; } catch { }
                        row.Keywords = Keywords(e);
                        try { row.Enchantments = RenderHostEnchants(enchByHost, e.Id, byId); } catch { }
                        snap.Minions.Add(row);
                    }
                    snap.Minions.Sort((a, b) => a.Slot.CompareTo(b.Slot));

                    // Trinkets (lesser/greater + any overflow), joined by name.
                    var trinkets = new List<string>();
                    foreach (var e in Snapshot(player.Trinkets))
                    {
                        var c = _store.Lookup(StripGold(e?.CardId));
                        if (c != null) trinkets.Add(c.Name);
                    }
                    snap.Trinkets = string.Join("; ", trinkets);
                }

                // Context tags off the player entity (best-effort; some only populate in shop phase).
                Entity pe = null;
                try { pe = g.PlayerEntity; } catch { }
                if (pe != null)
                {
                    try { snap.TavernTier = pe.GetTag(GameTag.PLAYER_TECH_LEVEL); } catch { }
                    try { snap.Gold = pe.GetTag(GameTag.RESOURCES) - pe.GetTag(GameTag.RESOURCES_USED) + pe.GetTag(GameTag.TEMP_RESOURCES); } catch { }
                }

                // Player/hero-level enchantments (gold buffs etc.) — the gold-source story, noise-filtered.
                try { snap.PlayerEnchantments = RenderHostEnchants(enchByHost, new[] { heroId, pe?.Id ?? -1 }, byId); } catch { }

                // Anomaly — retry each capture until found (it doesn't change, but the entity may not be
                // present/resolvable at the first snapshot; the old code latched a blank result forever).
                if (string.IsNullOrEmpty(_matchAnomaly))
                {
                    var a = ResolveAnomaly(g);
                    if (!string.IsNullOrEmpty(a)) _matchAnomaly = a;
                }
                snap.Anomaly = _matchAnomaly ?? "";

                Write(snap);
                _snapshotCount++;
                Log($"captured R{round} {phase}: {snap.Minions.Count} minions, hero={snap.Hero} hp={snap.HeroHp} tier={snap.TavernTier} gold={snap.Gold} pEnch=[{snap.PlayerEnchantments}]");
            }
            catch (Exception ex) { Log("capture error: " + ex.Message); }
        }

        private string ResolveAnomaly(GameV2 g)
        {
            try
            {
                foreach (var e in Snapshot(g.Entities?.Values))
                {
                    if (e == null) continue;
                    // An anomaly entity = our card record says "anomaly" (any pool) OR the live CARDTYPE tag
                    // is BATTLEGROUND_ANOMALY (catches anomalies missing/mismatched in our data).
                    var c = _store.Lookup(StripGold(e.CardId));
                    bool isAnom = c != null && string.Equals(c.CardType, "anomaly", StringComparison.OrdinalIgnoreCase);
                    if (!isAnom) { try { isAnom = e.GetTag(GameTag.CARDTYPE) == (int)CardType.BATTLEGROUND_ANOMALY; } catch { } }
                    if (isAnom) return c?.Name ?? TryCardName(e) ?? e.CardId;
                }
            }
            catch { }
            // NB: "all heroes are X" anomalies (BACON_ANOMALY_ALL_HEROES_ARE_THIS_DBID) may have NO anomaly
            // entity at all — only a GameEntity tag — so this returns null for them (handled separately TODO).
            return null;
        }

        // ── Incremental CSV write (OnUpdate thread) ────────────────────────────────────────────────
        // First call creates the file with the BOM + header; every call appends this snapshot's rows and
        // closes the handle (so the data is on disk before the next combat — crash/early-quit safe).
        private void Write(BoardSnapshot s)
        {
            try
            {
                if (_csvPath == null)
                {
                    var dir = Path.Combine(PluginConfig.DataDir, "match-exports");
                    Directory.CreateDirectory(dir);
                    _csvPath = Path.Combine(dir, $"match-{_matchStart:yyyyMMdd-HHmmss}-{Sanitize(_matchHero)}.csv");
                    // UTF-8 WITH BOM (so Excel reads Cyrillic) + a leading "sep=," hint so Excel splits on
                    // comma regardless of the user's locale — many locales default Excel's list separator to
                    // ';', which leaves a comma CSV unsplit in one column. Excel consumes this line; it stays
                    // a valid comma CSV for other tools.
                    File.WriteAllText(_csvPath,
                        "sep=," + Environment.NewLine + string.Join(",", Header) + Environment.NewLine,
                        new UTF8Encoding(true));
                }

                var sb = new StringBuilder();
                string ctx = string.Join(",", new[]
                {
                    Csv(_matchLabel), Csv(s.Round.ToString()), Csv(s.Phase),
                    Csv(s.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                    Csv(s.Hero), Csv(s.HeroHp.ToString()), Csv(s.TavernTier.ToString()), Csv(s.Gold.ToString()),
                    Csv(s.Trinkets), Csv(s.Anomaly), Csv(s.PlayerEnchantments)
                });

                if (s.Minions.Count == 0)
                {
                    // Empty board still gets one row so the moment is visible (8 blank minion columns:
                    // Slot, Minion, Golden, Attack, Health, Tier, Keywords, Enchantments).
                    sb.AppendLine(string.Join(",", new[] { ctx, "", "", "", "", "", "", "", "" }));
                }
                else
                {
                    foreach (var m in s.Minions)
                    {
                        sb.AppendLine(string.Join(",", new[]
                        {
                            ctx, Csv(m.Slot.ToString()), Csv(m.Name), Csv(m.Golden ? "Y" : ""),
                            Csv(m.Attack.ToString()), Csv(m.Health.ToString()),
                            Csv(m.Tier?.ToString() ?? ""), Csv(m.Keywords), Csv(m.Enchantments)
                        }));
                    }
                }

                // Append without a BOM (no preamble on append-mode no-BOM encoding).
                File.AppendAllText(_csvPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("write error: " + ex.Message); }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────
        private static string Keywords(Entity e)
        {
            var ks = new List<string>();
            try
            {
                if (e.GetTag(GameTag.MEGA_WINDFURY) != 0) ks.Add("Mega-Windfury");
                else if (e.GetTag(GameTag.WINDFURY) != 0) ks.Add("Windfury");
                foreach (var kt in KeywordTags)
                    if (e.GetTag(kt.Tag) != 0) ks.Add(kt.Label);
            }
            catch { }
            return string.Join(", ", ks);
        }

        // ── Enchantments (snapshot-level, ported from HSBot's enchantment_tracker domain logic) ──────

        // Group noise-filtered enchantment entities by the id they're ATTACHED to (host minion/hero).
        private static Dictionary<int, List<Entity>> GroupEnchantments(IEnumerable<Entity> all)
        {
            var map = new Dictionary<int, List<Entity>>();
            try
            {
                foreach (var e in all)
                {
                    if (e == null || !e.IsEnchantment || IsNoiseEnchant(e)) continue;
                    int host; try { host = e.GetTag(GameTag.ATTACHED); } catch { continue; }
                    if (host <= 0) continue;
                    if (!map.TryGetValue(host, out var lst)) { lst = new List<Entity>(); map[host] = lst; }
                    lst.Add(e);
                }
            }
            catch { }
            return map;
        }

        private string RenderHostEnchants(Dictionary<int, List<Entity>> byHost, int hostId, IDictionary<int, Entity> byId)
            => RenderHostEnchants(byHost, new[] { hostId }, byId);

        // "Source: effect text" for each enchantment on the given host(s), joined with " | ".
        private string RenderHostEnchants(Dictionary<int, List<Entity>> byHost, int[] hostIds, IDictionary<int, Entity> byId)
        {
            var parts = new List<string>();
            try
            {
                foreach (var hostId in hostIds)
                {
                    if (hostId <= 0 || !byHost.TryGetValue(hostId, out var lst)) continue;
                    foreach (var e in lst)
                    {
                        var r = RenderOne(e, byId);
                        if (!string.IsNullOrEmpty(r)) parts.Add(r);
                    }
                }
            }
            catch { }
            return string.Join(" | ", parts);
        }

        // One enchantment → "Source: effect". Numbers resolve from TAG_SCRIPT_DATA_NUM_1/2 substituted into
        // the card-text template ({0}/{1}); source is the CREATOR entity's card name.
        private string RenderOne(Entity e, IDictionary<int, Entity> byId)
        {
            try
            {
                string source = "";
                try
                {
                    int creatorId = e.GetTag(GameTag.CREATOR);
                    if (creatorId > 0 && byId != null && byId.TryGetValue(creatorId, out var ce))
                    {
                        var cc = _store.Lookup(StripGold(ce?.CardId));
                        source = cc?.Name ?? TryCardName(ce) ?? "";
                    }
                }
                catch { }

                string text = "";
                try { text = e.Card?.Text ?? ""; } catch { }
                text = CleanEffect(SubstituteScript(text, e));

                string body = !string.IsNullOrEmpty(text) ? text : (TryCardName(e) ?? e.CardId ?? "");
                // Drop shop/UI markers (purchasable/triple/cost state, not gameplay buffs): they all render
                // as "Costs (N)" / "Drag To Buy" — Drag To Buy, Triple Reward, Check Triples, Duplicating
                // Lens, Upbeat Duo, etc. (NB: English-text heuristic; a localized client would need a
                // card-id-based filter via another probe pass.)
                if (string.IsNullOrEmpty(body) || IsShopMarker(body)) return "";
                return string.IsNullOrEmpty(source) ? body : source + ": " + body;
            }
            catch { return ""; }
        }

        // Engine/internal enchantments to drop (mirrors HSBot's [DNT] + system-name filtering).
        private static bool IsNoiseEnchant(Entity e)
        {
            try
            {
                string name = TryCardName(e) ?? "";
                if (name.IndexOf("[DNT]", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("(DNT)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("PlayerEnchant", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string cid = e.CardId ?? "";
                if (cid.StartsWith("TB_BaconShop_", StringComparison.OrdinalIgnoreCase)) return true;
                if (cid.StartsWith("Bacon_", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return true; }
        }

        // Fill the card-text placeholders {0}/{1} from the enchantment's script-data tags (the live buff
        // amount), so "+{0}/+{1}." renders as e.g. "+8/+8.".
        private static string SubstituteScript(string text, Entity e)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                if (text.IndexOf("{0}", StringComparison.Ordinal) >= 0)
                    text = text.Replace("{0}", e.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_1).ToString());
                if (text.IndexOf("{1}", StringComparison.Ordinal) >= 0)
                    text = text.Replace("{1}", e.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_2).ToString());
            }
            catch { }
            return text;
        }

        // Port of HSBot's clean_effect_text: collapse to one line, strip trigger/lead-in prefixes, truncate.
        private static string CleanEffect(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = string.Join(" ", text.Split(new[] { '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
            foreach (var p in new[] { "Battlecry: ", "Deathrattle: ", "Start of Combat: ", "End of Turn: ", "Passive: ", "Choose One - " })
                if (t.StartsWith(p, StringComparison.Ordinal)) { t = t.Substring(p.Length); break; }
            foreach (var p in new[] { "Give a friendly minion ", "Give a minion ", "Give your minions ", "Give all ", "Give your " })
                if (t.StartsWith(p, StringComparison.Ordinal)) { t = t.Substring(p.Length); break; }
            t = t.Replace("______", "").Trim();
            if (t.Length > 80) t = t.Substring(0, 77) + "...";
            return t;
        }

        private static string TryCardName(Entity e) { try { return e?.Card?.Name; } catch { return null; } }

        // Shop/UI marker enchantments (purchasable/triple/cost state) — not gameplay buffs. They render as
        // "Costs (N)" or carry "Drag To Buy". Real buffs are stat/keyword effects, so this never drops them.
        private static readonly Regex CostMarker = new Regex(@"^Costs\s*\(\d+\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static bool IsShopMarker(string text) =>
            !string.IsNullOrEmpty(text) &&
            (CostMarker.IsMatch(text) || text.IndexOf("Drag To Buy", StringComparison.OrdinalIgnoreCase) >= 0);

        // Hero/minion display name: prefer our card record, fall back to HearthDb's name.
        private string NameOf(Entity e)
        {
            try
            {
                var c = _store.Lookup(StripGold(e?.CardId));
                if (c != null) return c.Name;
                return e?.Card?.Name;
            }
            catch { return null; }
        }

        private static bool IsGolden(string cardId) =>
            !string.IsNullOrEmpty(cardId) && cardId.EndsWith("_G", StringComparison.Ordinal);

        // Tripled/golden ids carry a trailing _G with no record of their own → look up the base.
        private static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);

        private static IEnumerable<Entity> Snapshot(IEnumerable<Entity> src)
        {
            try { return src == null ? new List<Entity>() : src.ToList(); }
            catch { return new List<Entity>(); }
        }

        // CSV field escape: quote when it contains comma/quote/newline; double embedded quotes.
        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch == ' ' ? '-' : ch);
            return sb.ToString();
        }

        private void HookGameEnd()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => { if (_current != null) _current._endRequested = true; })); }
            catch { /* event API absent → still finalize on leaving the match */ }
        }

        private void Log(string msg) { try { _log?.Invoke("[MatchRecorder] " + msg); } catch { } }

        // ── Row/snapshot models ────────────────────────────────────────────────────────────────────
        private sealed class BoardSnapshot
        {
            public int Round;
            public string Phase = "";
            public DateTime Time;
            public string Hero = "";
            public int HeroHp;
            public int TavernTier;
            public int Gold;
            public string Trinkets = "";
            public string Anomaly = "";
            public string PlayerEnchantments = "";
            public readonly List<MinionRow> Minions = new List<MinionRow>();
        }

        private sealed class MinionRow
        {
            public int Slot;
            public string Name = "";
            public bool Golden;
            public int Attack;
            public int Health;
            public int? Tier;
            public string Keywords = "";
            public string Enchantments = "";
        }
    }
}
