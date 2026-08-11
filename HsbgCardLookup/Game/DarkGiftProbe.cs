// DEV-ONLY diagnostic logger for the Season-14 "Dark Gift" / Dark Discovery system. Debug builds
// only (#if DEBUG, like GameStateProbe) — never ships, never writes for end users.
//
// What we're hunting (one real match; the user hovers the Dark Discovery button locked/ready/across
// turns and ideally uses it once):
//   1. WHICH entity/tags represent the button + its locked→ready flip on turn 3 (unknown tag ints
//      are fine — we correlate by WHEN they change).
//   2. WHERE the eligible-tier window lives (a tag? or purely turn-derived — then we compute it from
//      the dev-insights table and only need the turn mapping).
//   3. HOW shopTurn / GetTurnNumber() / the raw TURN tag align with the dev table's "Turn N" (the
//      tooltip transitions the user observes are the calibration marks).
//   4. WHAT the discover choices + their attached Dark Gift enchantments look like when the button
//      is used (CardIds, tags, ATTACHED pairing).
//   5. WHETHER player-level counters exist for gift eligibility (battlecries/deathrattles triggered,
//      tavern spells cast this game — the Battle Scars / Death's Embrace / Spell Siphon gates).
//
// Pure read; writes darkgift.log next to spike.log; safe to delete wholesale.
#if DEBUG
#warning DarkGiftProbe diagnostic is ACTIVE in this DEBUG build (writes darkgift.log via OnUpdate).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hearthstone_Deck_Tracker;                       // Core
using Hearthstone_Deck_Tracker.Hearthstone.Entities;  // Entity
using HearthDb.Enums;                                  // GameTag, Zone, CardType
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Game
{
    internal sealed class DarkGiftProbe
    {
        private const int PollMs = 500;   // tight enough to catch phase edges; diffs keep the log change-only

        // CardId substrings marking an entity as dark-gift-related → WATCHED (full dump once, then
        // tag diffs every tick). The real naming is unknown (these cards are in neither our data nor
        // the old HearthDb we compile against), so cast a wide net; a false positive just adds one
        // extra watched entity.
        private static readonly string[] CardIdKeywords = { "dark", "gift", "discover", "xavius", "nightmare" };

        private readonly CardStore _store;
        private DateTime _lastPoll = DateTime.MinValue;
        private bool _inMatch;
        private bool _phaseKnown, _inCombat;
        private int _shopTurn;                        // # of recruit phases seen = the player-facing turn
        private string _lastTurnLine;

        // Watched entities (player/game/hero + keyword matches): full dump on first sight, then
        // per-tick tag diffs (a missing tag counts as 0, the HS convention).
        private readonly Dictionary<int, string> _watchLabel = new Dictionary<int, string>();
        private readonly Dictionary<int, Dictionary<GameTag, int>> _watchTags = new Dictionary<int, Dictionary<GameTag, int>>();
        // One-time dumps: per entity id (SETASIDE/HAND arrivals) and per CardId (unknown-card net).
        private readonly HashSet<int> _dumpedIds = new HashSet<int>();
        private readonly HashSet<string> _dumpedCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DarkGiftProbe(CardStore store)
        {
            _store = store;
            Write("=== DarkGiftProbe armed (plugin loaded) ===");
        }

        public void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < PollMs) return;
                _lastPoll = now;

                var g = Core.Game;
                bool isBg = false;
                try { isBg = g != null && g.IsBattlegroundsMatch; } catch { }
                if (!isBg)
                {
                    if (_inMatch) { Write("=== left BG match ==="); ResetMatch(); }
                    return;
                }
                if (!_inMatch)
                {
                    _inMatch = true;
                    bool solo = false, duos = false;
                    try { solo = g.IsBattlegroundsSoloMatch; duos = g.IsBattlegroundsDuosMatch; } catch { }
                    Write($"=== entered BG match === solo={solo} duos={duos}");
                }

                bool combat = false;
                try { combat = g.IsBattlegroundsCombatPhase; } catch { }
                if (!_phaseKnown)
                {
                    _phaseKnown = true;
                    _inCombat = combat;
                    if (!combat) _shopTurn = 1;       // match starts in (hero pick +) recruit 1
                }
                else if (combat != _inCombat)
                {
                    _inCombat = combat;
                    if (!combat) _shopTurn++;         // combat → recruit = the next shop turn
                    Write($"--- phase -> {(combat ? "COMBAT" : "RECRUIT")} (shopTurn={_shopTurn}) ---");
                }

                Entity pe = null, ge = null, hero = null;
                int playerId = -1;
                try { pe = g.PlayerEntity; } catch { }
                try { ge = g.GameEntity; } catch { }
                try { hero = g.Player?.Hero; playerId = g.Player?.Id ?? -1; } catch { }

                // Turn-calibration line (change-detected): every turn counter side by side + the
                // context the offering rules key on. The tooltip transitions the user observes map
                // the dev table's "Turn N" onto these numbers.
                int getTurn = -1; try { getTurn = g.GetTurnNumber(); } catch { }
                string turnLine = $"TURN shopTurn={_shopTurn} getTurn={getTurn} tagTurn={Tag(ge, GameTag.TURN)}"
                    + $" step={Tag(ge, GameTag.STEP)}/{Tag(ge, GameTag.NEXT_STEP)} phase={(combat ? "COMBAT" : "RECRUIT")}"
                    + $" tier={Tag(pe, GameTag.PLAYER_TECH_LEVEL)} gold={Tag(pe, GameTag.RESOURCES) - Tag(pe, GameTag.RESOURCES_USED) + Tag(pe, GameTag.TEMP_RESOURCES)}";
                if (turnLine != _lastTurnLine) { Write(turnLine); _lastTurnLine = turnLine; }

                // Core watched entities: the ready-flip / tier-window / eligibility-counter hunt.
                var all = Snapshot(g.Entities?.Values);
                WatchDiff(pe, "PLAYER", all);
                WatchDiff(ge, "GAME", all);
                WatchDiff(hero, "HERO", all);

                foreach (var e in all)
                {
                    if (e == null) continue;
                    string cid = null; try { cid = e.CardId; } catch { }
                    if (string.IsNullOrEmpty(cid)) continue;

                    // (1) Dark-gift-looking CardId → watched (full dump + ongoing diffs), any zone/phase.
                    if (MatchesKeyword(cid)) { WatchDiff(e, "E" + e.Id + " " + cid, all); continue; }

                    if (combat) continue;   // the captures below are recruit-phase phenomena

                    int ctrl = Tag(e, GameTag.CONTROLLER);
                    int zone = Tag(e, GameTag.ZONE);
                    bool isEnch = false; try { isEnch = e.IsEnchantment; } catch { }

                    // (2) Our SETASIDE/HAND arrivals, once per entity: discover choices land in
                    // SETASIDE; a picked minion rides into HAND with its gift attached. Attached
                    // enchantments are listed inline so the choice↔gift pairing is visible even for
                    // repeat gifts. Enchantments themselves are skipped here (they surface via their
                    // host's listing, and unknown ones via (3)) — otherwise the shop's Drag-To-Buy
                    // marker enchants would spam a dump per shop roll.
                    if (!isEnch && ctrl == playerId && (zone == (int)Zone.SETASIDE || zone == (int)Zone.HAND))
                    {
                        if (_dumpedIds.Add(e.Id))
                        {
                            Write("ZONE " + DescribeFull(e));
                            foreach (var en in AttachedTo(all, e.Id))
                                Write("  L attached: " + DescribeFull(en));
                        }
                        continue;
                    }

                    // (3) Safety net for naming we failed to guess: any card our store doesn't know,
                    // controlled by us (or global), dumped once per CardId — catches the button/gifts
                    // under ANY naming without per-shop-roll spam (repeat gift enchants re-identify
                    // via their host's ZONE dump).
                    if ((ctrl == playerId || ctrl == 0) && _store.Lookup(StripGold(cid)) == null)
                    {
                        if (_dumpedCardIds.Add(StripGold(cid))) Write("NEWCARD " + DescribeFull(e));
                    }
                }
            }
            catch (Exception ex) { Write("poll error: " + ex.Message); }
        }

        private void ResetMatch()
        {
            _inMatch = false;
            _phaseKnown = false; _inCombat = false;
            _shopTurn = 0;
            _lastTurnLine = null;
            _watchLabel.Clear(); _watchTags.Clear();
            _dumpedIds.Clear(); _dumpedCardIds.Clear();
        }

        // ── Watched entities: first sight = full dump; afterwards = tag-diff lines ────────────────
        private void WatchDiff(Entity e, string label, List<Entity> all)
        {
            if (e == null) return;
            int id; try { id = e.Id; } catch { return; }
            var tags = TagSnapshot(e);
            if (tags == null) return;   // enumeration raced HDT's mutation — retry next tick

            Dictionary<GameTag, int> prev;
            if (!_watchTags.TryGetValue(id, out prev))
            {
                _watchLabel[id] = label;
                _watchTags[id] = tags;
                Write($"WATCH {label} " + DescribeFull(e));
                foreach (var en in AttachedTo(all, id))
                    Write("  L attached: " + DescribeFull(en));
                return;
            }

            List<string> changes = null;
            var keys = new HashSet<GameTag>(tags.Keys);
            keys.UnionWith(prev.Keys);
            foreach (var k in keys.OrderBy(k2 => k2.ToString(), StringComparer.Ordinal))
            {
                int o, n;
                prev.TryGetValue(k, out o);
                tags.TryGetValue(k, out n);
                if (o == n) continue;
                if (changes == null) changes = new List<string>();
                changes.Add($"{k} {o}->{n}");
            }
            _watchTags[id] = tags;
            if (changes != null) Write($"D {_watchLabel[id]}: " + string.Join(", ", changes));
        }

        // ── Descriptors ───────────────────────────────────────────────────────────────────────────
        private string DescribeFull(Entity e)
        {
            if (e == null) return "(null)";
            int id = 0; try { id = e.Id; } catch { }
            string cid = ""; try { cid = e.CardId ?? ""; } catch { }
            string hdb = ""; try { hdb = e.Card?.Name ?? ""; } catch { }
            string text = ""; try { text = (e.Card?.Text ?? "").Replace("\r", " ").Replace("\n", " "); } catch { }
            var ours = _store.Lookup(StripGold(cid));
            int zone = Tag(e, GameTag.ZONE), ctrl = Tag(e, GameTag.CONTROLLER), ct = Tag(e, GameTag.CARDTYPE);
            string s = $"E{id} cid={cid} hdb='{hdb}' ours={(ours != null ? "'" + ours.Name + "'" : "MISS")}"
                + $" zone={(Zone)zone} ctrl={ctrl} type={(CardType)ct}";
            int attached = Tag(e, GameTag.ATTACHED);
            if (attached > 0) s += " attachedTo=" + attached;
            if (text.Length > 0) s += $" text=\"{(text.Length > 140 ? text.Substring(0, 137) + "..." : text)}\"";
            return s + " tags[" + AllTags(e) + "]";
        }

        // Every tag, sorted by name (unknown-to-our-enum tags print as their raw int — exactly what
        // we want for a system newer than the compiled GameTag enum).
        private static string AllTags(Entity e)
        {
            try
            {
                return string.Join(", ", e.Tags
                    .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                    .Select(kv => kv.Key + "=" + kv.Value));
            }
            catch (Exception ex) { return "(err " + ex.Message + ")"; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────
        private static bool MatchesKeyword(string cardId)
        {
            foreach (var k in CardIdKeywords)
                if (cardId.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static List<Entity> AttachedTo(List<Entity> all, int hostId)
        {
            var outp = new List<Entity>();
            if (hostId <= 0) return outp;
            foreach (var e in all)
            {
                try { if (e != null && e.IsEnchantment && e.GetTag(GameTag.ATTACHED) == hostId) outp.Add(e); }
                catch { }
            }
            return outp;
        }

        private static int Tag(Entity e, GameTag t)
        {
            if (e == null) return 0;
            try { return e.GetTag(t); } catch { return 0; }
        }

        private static Dictionary<GameTag, int> TagSnapshot(Entity e)
        {
            try { return e.Tags.ToDictionary(kv => kv.Key, kv => kv.Value); } catch { return null; }
        }

        private static List<Entity> Snapshot(IEnumerable<Entity> src)
        {
            try { return src?.ToList() ?? new List<Entity>(); }
            catch { return new List<Entity>(); }
        }

        // Tripled/golden ids carry a trailing _G with no record of their own → look up the base.
        private static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup", "darkgift.log");

        private static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { /* diagnostics must never throw */ }
        }
    }
}
#endif
