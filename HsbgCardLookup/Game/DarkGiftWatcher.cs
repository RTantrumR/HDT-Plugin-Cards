using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker;                 // Core
using HearthDb.Enums;                            // GameTag
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;
using HsbgCardLookup.Ui;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// Opt-in in-match feature: the Dark Gift list panel, opened by CLICKING the "?" marker that sits
    /// just above the in-game Dark Discovery button (<see cref="DarkGiftMarker"/>). It used to summon
    /// itself on hovering that button, off HearthMirror's <c>GetBigCardState()</c>; the panel is big,
    /// so hovering the button to read its own tooltip kept throwing it across the board. Now nothing
    /// appears unless the player asks for it, and it lands out of the way (pinned top-right) when it
    /// does. (Nightmare Lord Xavius' hero power follows the same offering rules, but is deliberately
    /// NOT a trigger — user direction.)
    ///
    /// The marker is on screen only while the panel would have something to say: in a Battlegrounds
    /// match, with at least one gift still offerable and at least one of the three uses left. Clicking
    /// it toggles the panel, which lists every Dark Gift still obtainable this game (offerable-now
    /// glowing / future dimmed / expired omitted) and floats guaranteed-tribe-relevant gifts to the
    /// top in green (from turn 6 one offer is the player's most common minion type). It closes on the
    /// marker again, on the panel's ✕, on a click anywhere outside it, and by itself the moment the
    /// player presses the real button. The header carries only NON-duplicated info (the button's own
    /// tooltip already states tier/uses/cost).
    ///
    /// Live state read off the button entity (BG36_Button_DarkGift, probe-verified 2026-08-05):
    /// TAG_SCRIPT_DATA_NUM_2 = uses left, NUM_3/NUM_4 = current min/max offered tier, LOCK_VISUAL =
    /// pre-turn-3 lock, EXHAUSTED = pressed this turn. Driven by <c>IPlugin.OnUpdate</c> (entity reads
    /// throttled). Pure read — never mutates the game.
    /// </summary>
    public sealed class DarkGiftWatcher
    {
        private const string ButtonCardId = "BG36_Button_DarkGift";
        private const int StateMs = 500;     // entity re-read throttle

        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;

        private DateTime _lastStateRead = DateTime.MinValue;
        private volatile string _lastSig;
        private volatile bool _open;          // the player has the panel open (toggled by the marker)
        private bool _markerShown;            // last value pushed to the marker (avoids marshalling every tick)
        private volatile bool _lastHadPool;   // a pool was rendered on the last content build (mode cycling)
        private readonly bool _preview;       // a detached instance driving a settings preview
        private DarkGiftPanel _panel;
        private DarkGiftMarker _marker;       // created and touched on the canvas thread only

        // Live state (refreshed on the throttle; read on the OnUpdate thread only).
        private int _buttonId = -1;
        private bool _buttonFound;
        private bool _locked;
        private bool _exhausted;              // button pressed this turn (clears next turn)
        private int _uses = -1, _tierMin, _tierMax;
        private int _turn;
        private readonly List<string> _topTribes = new List<string>();   // most common board type(s)
        private IReadOnlyList<DarkGift> _gifts;   // effective gift list (site data or static fallback), resolved per match
        private bool _duos;                       // mode filter for the pool (Duos-only vs Solos-only cards)
        private readonly HashSet<string> _lobbyTribes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // this lobby's tribes (empty until the mirror read succeeds)

        private volatile bool _ended;
        private volatile bool _startFlag;   // HDT's OnGameStart fired → reset for the new match
        private bool _wasBgMatch;
        private static DarkGiftWatcher _current;
        private static bool _hooked;

        private readonly int _ownPid;
        private uint _lastFgPid;
        private bool _lastFgIsHs;

        public DarkGiftWatcher(CardStore store, PluginConfig config, Dispatcher ui, Action<string> log)
            : this(store, config, ui, log, preview: false) { }

        /// <param name="preview">A detached instance that only ever renders into a caller's own panel
        /// (see <see cref="RenderInto"/>). It must NOT hook HDT's game events: those route through a
        /// static "current" pointer, so a second hooked instance would steal them from the live
        /// watcher and kill the in-match panel.</param>
        internal DarkGiftWatcher(CardStore store, PluginConfig config, Dispatcher ui, Action<string> log, bool preview)
        {
            _store = store; _config = config; _ui = ui; _log = log; _preview = preview;
            try { _ownPid = Process.GetCurrentProcess().Id; } catch { _ownPid = -1; }
            if (!preview) HookGameEvents();
        }

        // ── Poll (OnUpdate thread, ~100ms; entity reads throttled to StateMs) ───────────────────────
        public void Poll()
        {
            try
            {
                if (_preview) return;   // a preview instance renders on demand, never on the poll
                if (!_config.ShowDarkGifts || !ArrangeSession.AllowsDarkGifts) { HideIfShown(); return; }

                // New-match reset. Primary signal: HDT's OnGameStart event (a fast requeue can go
                // match→match without IsBattlegroundsMatch ever dipping false, which left the _ended
                // latch stuck and the panel dead — live-reported). The false→true flip stays as a
                // backup for reloads mid-match.
                if (_startFlag) { _startFlag = false; _ended = false; ResetMatch(); }

                bool isBg = false;
                try { var gg = Core.Game; isBg = gg != null && gg.IsBattlegroundsMatch; } catch { }
                if (isBg && !_wasBgMatch) { _ended = false; ResetMatch(); }
                _wasBgMatch = isBg;

                if (!isBg || _ended || !IsForeground()) { HideIfShown(); return; }

                // Plain throttle, no "read again immediately while the button is missing" clause: the
                // read sweeps every entity, and a lobby where Dark Gifts are simply not active would
                // pay for that sweep on every 100ms tick of the whole match.
                var now = DateTime.UtcNow;
                if ((now - _lastStateRead).TotalMilliseconds >= StateMs)
                {
                    _lastStateRead = now;
                    ReadLiveState();
                }

                // The rules only need the turn — the button entity just enriches the header.
                int targetTurn = _turn;

                // Is there anything to say at all? The marker requires the Dark Discovery BUTTON ENTITY
                // to exist — that, not the calendar, is what says this lobby runs Dark Gifts (the old
                // hover trigger got this for free, since only a real button can be hovered). Then:
                // nothing offerable yet (every gift's min turn is 3, which is also the button's
                // unlock), or all 3 uses spent (NUM_2 hits 0, CANT_READY flips) → no marker, no panel.
                bool anyNow = false;
                foreach (var g2 in _gifts ?? DarkGifts.All)
                    if (PossibleInLobby(g2) && g2.IsCurrent(targetTurn)) { anyNow = true; break; }
                bool available = _buttonFound && _uses != 0 && anyNow;

                if (!available) _open = false;
                bool show = _open;

                string mode = NormMode(_config.DarkGiftMode);
                string sig = available
                    ? $"{show}|{targetTurn}|{_locked}|{string.Join(",", _topTribes)}|{_tierMin}|{_tierMax}|{_lobbyTribes.Count}|{mode}"
                    : "hidden";
                if (sig == _lastSig && _markerShown == available) return;
                _lastSig = sig;
                _markerShown = available;

                List<DarkGiftPanel.Row> rows = null;
                List<DarkGiftPanel.MinionArt> minions = null;
                int poolTotal = 0;
                string header = null, poolCaption = null;
                if (show)
                {
                    var content = BuildContent(targetTurn, mode);
                    rows = content.Rows;
                    header = content.Header;
                    poolCaption = content.PoolCaption;
                    minions = content.Minions;
                    poolTotal = content.PoolTotal;
                }

#if DEBUG
                _log?.Invoke("[DarkGifts] marker " + (available ? "on" : "off") + ", panel "
                    + (rows == null ? "hide" : $"show ({rows.Count} rows, \"{header}\")"));
#endif
                bool markerVisible = available;
                Marshal(() => ApplyUi(markerVisible, rows, header, poolCaption, minions, poolTotal));
            }
            catch { /* OnUpdate must never throw */ }
        }

        public void OnSettingsChanged()
        {
            if (!_config.ShowDarkGifts || !ArrangeSession.AllowsDarkGifts) { HideIfShown(); return; }
            _lastSig = null;   // the display mode may have changed → re-render on the next poll
        }

        private static string NormMode(string m)
        {
            if (string.Equals(m, "Gifts", StringComparison.OrdinalIgnoreCase)) return "gifts";
            if (string.Equals(m, "Minions", StringComparison.OrdinalIgnoreCase)) return "minions";
            return "both";
        }

        // Right-click on the panel: cycle Both → Gifts → Minions → Both. Minions-only is skipped when
        // no pool is currently rendered — entering it would hide the panel mid-click, leaving no way
        // to right-click onward (settings can still select it directly).
        private void CycleMode()
        {
            string m = NormMode(_config.DarkGiftMode);
            _config.DarkGiftMode = m == "both" ? "Gifts" : m == "gifts" && _lastHadPool ? "Minions" : "Both";
            try { _config.Save(); } catch { }
            _lastSig = null;   // next poll (~100ms) re-renders in the new mode
        }

        // ── Live reads (OnUpdate thread): button tags, HP countdown, most common board type ─────────
        private void ReadLiveState()
        {
            try
            {
                var g = Core.Game;
                if (g == null) { _buttonFound = false; return; }
                try { _turn = g.GetTurnNumber(); } catch { }   // == the player-facing shop turn (verified)
                try { _duos = g.IsBattlegroundsDuosMatch; } catch { }

                if (_gifts == null) _gifts = DarkGifts.Resolve(_store);   // site data, static fallback
                if (_lobbyTribes.Count == 0) ReadLobbyTribes();           // may be empty early — keep retrying

                Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity btn = null;
                var ents = g.Entities;
                if (ents != null)
                {
                    if (_buttonId > 0)
                        try { ents.TryGetValue(_buttonId, out btn); } catch { btn = null; }
                    if (btn != null && !string.Equals(btn.CardId, ButtonCardId, StringComparison.OrdinalIgnoreCase))
                        btn = null;
                    if (btn == null)
                    {
                        List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity> all;
                        try { all = new List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>(ents.Values); }
                        catch { all = new List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>(); }
                        foreach (var e in all)
                        {
                            string cid = null; try { cid = e?.CardId; } catch { }
                            if (string.Equals(cid, ButtonCardId, StringComparison.OrdinalIgnoreCase)) { btn = e; break; }
                        }
                    }
                }

                if (btn != null)
                {
                    _buttonFound = true;
                    _buttonId = btn.Id;
                    int uses = Tag(btn, GameTag.TAG_SCRIPT_DATA_NUM_2);
                    bool exhausted = Tag(btn, GameTag.EXHAUSTED) != 0;
                    // The player just pressed the real button — the three choices are coming up, so
                    // the panel gets out of the way (it would be covering them). EXHAUSTED flips on
                    // the press and clears next turn, so only its RISING edge counts; the uses
                    // countdown is the backup signal in case the two tags land in different frames.
                    if ((exhausted && !_exhausted) || (_uses > 0 && uses >= 0 && uses < _uses)) _open = false;
                    _exhausted = exhausted;
                    _uses = uses;
                    _tierMin = Tag(btn, GameTag.TAG_SCRIPT_DATA_NUM_3);
                    _tierMax = Tag(btn, GameTag.TAG_SCRIPT_DATA_NUM_4);
                    _locked = Tag(btn, GameTag.LOCK_VISUAL) != 0;
                }
                else { _buttonFound = false; _buttonId = -1; }

                ReadTopTribes(g);
            }
            catch { _buttonFound = false; }
        }

        // Most common minion type on the player's board — drives the turn-6+ guaranteed-offer tip and
        // the green emphasis. Multi-type minions count once per type; Amalgam-style "All" is skipped
        // (it isn't a specific type). Ties keep every top type.
        private void ReadTopTribes(Hearthstone_Deck_Tracker.Hearthstone.GameV2 g)
        {
            try
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity> minions;
                try { minions = g.Player?.Minions?.ToList() ?? new List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>(); }
                catch { minions = new List<Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity>(); }
                foreach (var e in minions)
                {
                    string cid = null; try { cid = e?.CardId; } catch { }
                    var c = _store.Lookup(StripGold(cid));
                    if (c?.MinionTypes == null) continue;
                    foreach (var t in c.MinionTypes)
                        if (!string.IsNullOrEmpty(t) && !string.Equals(t, "All", StringComparison.OrdinalIgnoreCase))
                            counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;
                }

                _topTribes.Clear();
                int max = 0;
                foreach (var kv in counts) if (kv.Value > max) max = kv.Value;
                if (max > 0)
                    foreach (var kv in counts) if (kv.Value == max) _topTribes.Add(kv.Key);
                _topTribes.Sort(StringComparer.Ordinal);
            }
            catch { }
        }

        private static int Tag(Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity e, GameTag t)
        {
            try { return e.GetTag(t); } catch { return 0; }
        }

        private static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);

        // ── Content (OnUpdate thread; pure functions of the read state) ─────────────────────────────
        /// <summary>Everything the panel renders for one (turn, mode). Split out of Poll so the
        /// settings preview and arrange mode run the PRODUCTION computation instead of a copy of it —
        /// a change to what the panel shows belongs here, where every caller sees it.</summary>
        private sealed class PanelContent
        {
            public List<DarkGiftPanel.Row> Rows;
            public List<DarkGiftPanel.MinionArt> Minions;
            public string Header, PoolCaption;
            public int PoolTotal;
            public bool Suppress;   // minions-only with no pool: the panel shows nothing at all
        }

        private PanelContent BuildContent(int targetTurn, string mode)
        {
            var c = new PanelContent();

            // Effective offered-tier window: the button's live tags when available (anomaly-proof),
            // else the published table for the current turn.
            int wmin, wmax;
            if (_buttonFound && _tierMin > 0) { wmin = _tierMin; wmax = Math.Max(_tierMin, _tierMax); }
            else TierWindow(targetTurn, out wmin, out wmax);

            // Guaranteed-tribe pool: only when the turn-6+ rule is live AND the top type is
            // unambiguous (a tie means we can't know which type the game guarantees).
            PoolAnalysis pa = null;
            string tribe = targetTurn >= 6 && _topTribes.Count == 1 ? _topTribes[0] : null;
            if (tribe != null) pa = AnalyzePool(BuildPool(tribe, wmin, wmax));
            _lastHadPool = pa != null && pa.Pool.Count > 0;

            if (mode != "minions")
            {
                c.Rows = BuildRows(targetTurn, pa);
                c.Header = BuildHeader(targetTurn);
            }

            // Pool renders (left column): the caption whenever a pool exists at all, then as many
            // card arts as the user asked for, sole-enablers first; the rest become a "+N more" note.
            // At 0 the caption is the whole showing — which tribe is guaranteed, and how big its pool
            // is, without the wall of renders.
            if (mode != "gifts" && pa != null && pa.Pool.Count > 0)
            {
                c.PoolTotal = pa.Pool.Count;
                c.PoolCaption = $"Guaranteed {tribe} — Tier {(wmax > wmin ? wmin + "–" + wmax : wmin.ToString())} ({c.PoolTotal})";
                int show = _config.DarkGiftPoolCount;   // the config property clamps to 0..Max
                if (show > 0)
                    c.Minions = pa.Pool
                        .OrderByDescending(m => pa.Sole.Contains(m))
                        .ThenBy(m => m.Tier ?? 9).ThenBy(m => m.Name, StringComparer.Ordinal)
                        .Take(show)
                        .Select(m => new DarkGiftPanel.MinionArt { Card = m, Emph = pa.Sole.Contains(m) ? 2 : 1 })
                        .ToList();
            }

            if (mode == "minions")
            {
                // Minions-only: the art column alone, so no pool at all means no panel (user-chosen
                // over falling back to the list). Keyed on the CAPTION, not the renders — with the
                // count at 0 there is still a pool, and naming its tribe is exactly what 0 asks for.
                if (c.PoolCaption == null) { c.Rows = null; c.Suppress = true; }
                else c.Rows = new List<DarkGiftPanel.Row>();
            }
            return c;
        }

        /// <summary>Render a given turn into a caller-owned panel, off game. Used by the settings
        /// preview, which needs the real thing on screen with no match to read state from. Everything obtainable outside a match stays REAL — the gift list, the tier
        /// windows, the pool and its sole-enabler analysis all come from live card data; only the
        /// per-match facts that cannot exist (turn, the player's top tribe, the lobby's tribe set,
        /// Duos) are supplied by the caller.</summary>
        internal void RenderInto(DarkGiftPanel panel, int turn, string topTribe, bool duos, string mode)
        {
            if (panel == null) return;
            try
            {
                if (_gifts == null) _gifts = DarkGifts.Resolve(_store);
                _turn = turn;
                _duos = duos;
                _buttonFound = false;        // no button entity off game → use the published tier table
                _topTribes.Clear();
                if (!string.IsNullOrEmpty(topTribe)) _topTribes.Add(topTribe);
                _lobbyTribes.Clear();        // empty = "lobby unknown", which suppresses no gift

                var c = BuildContent(turn, NormMode(mode));
                if (c.Rows == null) { panel.Hide(); return; }
                panel.SetContent(c.Header, c.Rows, c.PoolCaption, c.Minions, c.PoolTotal);
                panel.Show();
            }
            catch (Exception ex) { try { _log?.Invoke("[DarkGifts] RenderInto error: " + ex.Message); } catch { } }
        }

        /// <summary>How many minions the guaranteed-tribe pool holds at a given turn — the tier window
        /// alone, no rendering and no live game state. The settings preview uses it to pick a turn whose
        /// pool can actually hold the count being previewed. Preview instances only: it writes
        /// <c>_duos</c>, which the live watcher owns.</summary>
        internal int PoolSizeFor(int turn, string tribe, bool duos)
        {
            if (!_preview) return 0;
            try
            {
                if (_gifts == null) _gifts = DarkGifts.Resolve(_store);
                _duos = duos;
                TierWindow(turn, out int wmin, out int wmax);
                return BuildPool(tribe, wmin, wmax).Count;
            }
            catch { return 0; }
        }

        private List<DarkGiftPanel.Row> BuildRows(int targetTurn, PoolAnalysis pa)
        {
            bool tribeRule = targetTurn >= 6 && _topTribes.Count > 0;   // a guaranteed top-type offer exists
            var current = new List<DarkGiftPanel.Row>();
            var future = new List<KeyValuePair<int, DarkGiftPanel.Row>>();
            foreach (var gft in _gifts ?? DarkGifts.All)
            {
                if (!PossibleInLobby(gft)) continue;    // can't exist in this lobby → not shown at all
                if (gft.IsGone(targetTurn)) continue;   // window closed → not shown at all
                bool cur = gft.IsCurrent(targetTurn);
                int emph = 0;
                if (cur && tribeRule)
                {
                    // Green: gifts the guaranteed top-type offer can carry — tribe-specific gifts
                    // matching that type, and typed-minions-only gifts (the guaranteed offer IS typed).
                    if (gft.TypedOnly || (gft.TribeOnly != null && _topTribes.Contains(gft.TribeOnly)))
                        emph = 1;
                    // Requirement gifts judged against the actual pool: exactly ONE minion satisfies
                    // it → PURPLE unique-enabler pairing (that minion's chip goes purple too);
                    // several → green; none → the guaranteed offer can't carry it (stays normal).
                    if (gft.Requires != null && pa != null)
                    {
                        int n; pa.ReqCounts.TryGetValue(gft.Requires, out n);
                        emph = n == 1 ? 2 : (n >= 2 ? Math.Max(emph, 1) : emph);
                    }
                }
                var row = new DarkGiftPanel.Row { Name = gft.Name, Text = gft.Text, Note = gft.Note, Current = cur, Emph = emph };
                if (cur) current.Add(row);
                else future.Add(new KeyValuePair<int, DarkGiftPanel.Row>(gft.MinTurn, row));
            }
            // Emphasized gifts float to the top of the current block (purple above green); both sorts
            // are stable (LINQ), so the list order survives within each group. Future gifts sort by
            // unlock turn.
            var result = current.OrderByDescending(r => r.Emph).ToList();
            result.AddRange(future.OrderBy(kv => kv.Key).Select(kv => kv.Value));
            return result;
        }

        // Gifts impossible in THIS lobby are dropped entirely (user direction): tribe-only gifts whose
        // tribe isn't among the lobby's, and gifts the devs remove from lobbies containing certain
        // tribes (Toughened Shield ↔ Quilboar/Naga). No filtering while the tribe set is unknown —
        // better a note-annotated extra row than a wrongly hidden one.
        private bool PossibleInLobby(DarkGift g)
        {
            if (_lobbyTribes.Count == 0) return true;
            if (g.TribeOnly != null && !_lobbyTribes.Contains(g.TribeOnly)) return false;
            if (g.NotWithTribes != null)
                foreach (var t in g.NotWithTribes)
                    if (_lobbyTribes.Contains(t)) return false;
            return true;
        }

        // This lobby's tribes via HearthMirror (returns HearthDb Race ints; race-condition-prone at
        // match start per hdt-game-state.md, so retried until non-empty).
        private void ReadLobbyTribes()
        {
            try
            {
                var races = HearthMirror.Reflection.Client?.GetAvailableBattlegroundsRaces();
                if (races == null) return;
                foreach (var r in races)
                {
                    var t = TribeName(r);
                    if (t != null) _lobbyTribes.Add(t);
                }
            }
            catch { }
        }

        private static string TribeName(int race)
        {
            switch ((Race)race)
            {
                case Race.BEAST: return "Beast";
                case Race.MECHANICAL: return "Mech";
                case Race.MURLOC: return "Murloc";
                case Race.NAGA: return "Naga";
                case Race.QUILBOAR: return "Quilboar";
                case Race.UNDEAD: return "Undead";
                case Race.DEMON: return "Demon";
                case Race.DRAGON: return "Dragon";
                case Race.ELEMENTAL: return "Elemental";
                case Race.PIRATE: return "Pirate";
                default: return null;
            }
        }

        // The published turn → offered-tier window (dev post; live-verified against the button's tags
        // T3–T10, frozen at 5–6 afterwards). Used when the button entity isn't resolved yet.
        private static void TierWindow(int turn, out int min, out int max)
        {
            if (turn <= 3) { min = 2; max = 2; }
            else if (turn == 4) { min = 2; max = 3; }
            else if (turn == 5) { min = 3; max = 3; }
            else if (turn == 6) { min = 3; max = 4; }
            else if (turn == 7) { min = 4; max = 4; }
            else if (turn == 8) { min = 4; max = 5; }
            else if (turn == 9) { min = 4; max = 6; }
            else { min = 5; max = 6; }
        }

        // ── Guaranteed-tribe pool (the turn-6+ "one offer is your most common type" rule) ───────────
        private sealed class PoolAnalysis
        {
            public List<BgCard> Pool;                                   // window-filtered tavern minions of the tribe
            public readonly Dictionary<string, int> ReqCounts = new Dictionary<string, int>();   // requirement → # satisfying
            public readonly HashSet<BgCard> Sole = new HashSet<BgCard>();                        // sole enablers (purple chips)
        }

        // Tavern-pool minions of the tribe within the offered-tier window. "Tavern" per the dev post:
        // no tokens / hero-power minions / buddies; plus the published offer exclusions we can read
        // from data (no Magnetic; named exceptions). Mode-mismatched cards are dropped.
        private List<BgCard> BuildPool(string tribe, int tierMin, int tierMax)
        {
            var pool = new List<BgCard>();
            try
            {
                foreach (var c in _store.All)
                {
                    if (c == null || !c.Pool) continue;
                    if (!string.Equals(c.CardType, "minion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!c.Tier.HasValue || c.Tier.Value < tierMin || c.Tier.Value > tierMax) continue;
                    if (_duos ? c.IsSolosOnly : c.IsDuosOnly) continue;
                    // Timewarped variants live only in their anomaly lobby (detecting that lobby is a
                    // later refinement — normal pools must exclude them or every count doubles). The
                    // CATEGORY is authoritative; the isTimewarped flag is missing on some records (live
                    // data 2026-08-13: Timewarped Leapfrogger leaked into Beast pools with flag=false).
                    if (c.IsTimewarped || HasCategory(c, "timewarped")) continue;

                    bool tribeMatch = false, derived = false, magnetic = false;
                    if (c.MinionTypes != null)
                        foreach (var t in c.MinionTypes)
                            if (string.Equals(t, tribe, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(t, "All", StringComparison.OrdinalIgnoreCase)) { tribeMatch = true; break; }
                    if (!tribeMatch) continue;

                    if (c.Categories != null)
                        foreach (var cat in c.Categories)
                            if (string.Equals(cat, "token", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(cat, "heroPower", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(cat, "buddy", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(cat, "darkgift", StringComparison.OrdinalIgnoreCase)) { derived = true; break; }
                    if (derived) continue;

                    if (c.Keywords != null)
                        foreach (var k in c.Keywords)
                            if (string.Equals(k, "Magnetic", StringComparison.OrdinalIgnoreCase)) { magnetic = true; break; }
                    if (magnetic) continue;

                    // Named gameplay/balance exceptions from the dev post ("such as" — non-exhaustive).
                    if (string.Equals(c.Name, "Leeroy Jenkins", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Name, "Deadly Spore", StringComparison.OrdinalIgnoreCase)) continue;

                    pool.Add(c);
                }
            }
            catch { }
            return pool;
        }

        private static PoolAnalysis AnalyzePool(List<BgCard> pool)
        {
            var pa = new PoolAnalysis { Pool = pool };
            try
            {
                foreach (var req in new[] { "Spellcraft", "Deathrattle", "EndOfTurn", "Avenge", "DivineShield", "Battlecry" })
                {
                    int n = 0; BgCard last = null;
                    foreach (var m in pool)
                        if (HasReq(m, req)) { n++; last = m; }
                    pa.ReqCounts[req] = n;
                    if (n == 1) pa.Sole.Add(last);
                }
            }
            catch { }
            return pa;
        }

        // Does the minion satisfy a gift's positive requirement? Keywords first, card text (raw, may
        // contain HTML tags — plain word search is tag-proof) as fallback. Two verified subtleties:
        // Sunken Persistence only matters for TEMPORARY spellcrafts ("until …" — e.g. Glowscale;
        // Tranquil Meditative's is already permanent), and Divine Shield must match keywords ONLY
        // (Glowscale's text GRANTS Divine Shield without having it).
        private static bool HasReq(BgCard m, string req)
        {
            string text = m.Text ?? "";
            switch (req)
            {
                case "Spellcraft": return (HasKw(m, "Spellcraft") || Contains(text, "Spellcraft")) && Contains(text, "until");
                case "Deathrattle": return HasKw(m, "Deathrattle") || Contains(text, "Deathrattle:");
                case "EndOfTurn": return Contains(text, "end of your turn") || Contains(text, "end of turn") || Contains(text, "end of every");
                case "Avenge": return HasKw(m, "Avenge") || Contains(text, "Avenge (");
                case "DivineShield": return HasKw(m, "Divine Shield");
                case "Battlecry": return HasKw(m, "Battlecry") || Contains(text, "Battlecry:");
                default: return false;
            }
        }

        private static bool HasKw(BgCard m, string kw)
        {
            if (m.Keywords == null) return false;
            foreach (var k in m.Keywords)
                if (string.Equals(k, kw, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool HasCategory(BgCard m, string cat)
        {
            if (m.Categories == null) return false;
            foreach (var c in m.Categories)
                if (string.Equals(c, cat, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Contains(string hay, string needle) =>
            hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // Only NON-duplicated info — the button's own tooltip already shows tier window, uses and cost.
        private string BuildHeader(int targetTurn)
        {
            var parts = new List<string>();
            if (targetTurn >= 6)
                parts.Add(_topTribes.Count > 0
                    ? $"one offer guaranteed: {string.Join("/", _topTribes)} (your top type)"
                    : "one offer will be your most common type");
            else
                parts.Add("from turn 6, one offer is your most common type");

            return string.Join(" • ", parts);
        }

        // ── Marker + panel (canvas thread) ──────────────────────────────────────────────────────────
        private void ApplyUi(bool markerVisible, List<DarkGiftPanel.Row> rows, string header,
            string poolCaption, List<DarkGiftPanel.MinionArt> minions, int poolTotal)
        {
            try
            {
                EnsureMarker();
                _marker.SetVisible(markerVisible);
                _marker.SetOpen(rows != null);

                if (rows == null) { _panel?.Hide(); return; }
                EnsurePanel();
                _panel.SetContent(header, rows, poolCaption, minions, poolTotal);   // sets the panel width…
                _panel.PlaceTopRight();   // …which the right-edge pin uses, so this runs on every build
                _panel.Show();
            }
            catch (Exception ex) { try { _log?.Invoke("[DarkGifts] ApplyUi error: " + ex.Message); } catch { } }
        }

        private void EnsurePanel()
        {
            if (_panel != null) return;
            _panel = new DarkGiftPanel();
            _panel.ModeCycleRequested += CycleMode;
            _panel.CloseRequested += ClosePanel;
        }

        private void EnsureMarker()
        {
            if (_marker != null) return;
            _marker = new DarkGiftMarker(_log);
            _marker.Clicked += ToggleOpen;
        }

        // Canvas thread (the marker's click). The next poll (~100ms) builds or drops the content.
        private void ToggleOpen()
        {
            _open = !_open;
            _lastSig = null;
        }

        // Canvas thread: the panel's ✕, and the mouse hook's click-off. A click ON the marker is not a
        // click-off — the marker's own toggle handles it, and closing here too would just reopen it.
        private void ClosePanel()
        {
            if (_marker != null && _marker.IsUnderMouse) return;
            _open = false;
            _lastSig = null;
        }

        private void HideIfShown()
        {
            _lastSig = null;
            _open = false;
            _markerShown = false;
            var p = _panel; var m = _marker;
            if (p != null || m != null)
                Marshal(() => { try { p?.Hide(); m?.SetVisible(false); } catch { } });
        }

        public void CloseAll()
        {
            var p = _panel; var m = _marker;
            _panel = null; _marker = null;
            if (p == null && m == null) return;
            try { (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.Invoke(new Action(() => { p?.Close(); m?.Close(); })); }
            catch { }
        }

        // The panel lives on HDT's overlay canvas, so its work belongs on that canvas' dispatcher.
        private void Marshal(Action action)
        {
            try { (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.BeginInvoke(action); } catch { }
        }

        private void ResetMatch()
        {
            _buttonId = -1; _buttonFound = false; _uses = -1; _locked = false; _exhausted = false;
            _topTribes.Clear();
            _lobbyTribes.Clear();
            _gifts = null;   // re-resolve next match (picks up background data refreshes)
            _lastSig = null; _open = false; _markerShown = false;
        }

        // ── Foreground gating (mirrors BgMmr/BgHud) ─────────────────────────────────────────────────
        private bool IsForeground()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == 0) return false;
                if (pid == _ownPid) return true;
                if (pid == _lastFgPid) return _lastFgIsHs;
                _lastFgPid = pid;
                try { using (var p = Process.GetProcessById((int)pid)) _lastFgIsHs = string.Equals(p.ProcessName, "Hearthstone", StringComparison.OrdinalIgnoreCase); }
                catch { _lastFgIsHs = false; }
                return _lastFgIsHs;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private void HookGameEvents()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => _current?.MarkEnded())); }
            catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => _current?.MarkStarted())); }
            catch { }
        }

        private void MarkEnded() { _ended = true; _lastSig = null; }
        private void MarkStarted() { _startFlag = true; }
    }
}
