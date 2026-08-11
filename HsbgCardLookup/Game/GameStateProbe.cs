// DEV-ONLY diagnostic logger. Compiled into Debug builds only (#if DEBUG) — Release/distribution
// builds exclude it entirely (and the Plugin.cs _probe wiring is likewise #if DEBUG'd), so it never
// ships and never writes gamestate.log for end users.
#if DEBUG
#warning GameStateProbe diagnostic is ACTIVE in this DEBUG build (writes gamestate.log via OnUpdate).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Hearthstone_Deck_Tracker;                       // Core
using Hearthstone_Deck_Tracker.Hearthstone.Entities;  // Entity
using HearthDb.Enums;                                  // GameTag
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// READ-ONLY diagnostics. On a throttled poll (driven by IPlugin.OnUpdate) it snapshots live
    /// Battlegrounds state to gamestate.log so we can learn what HDT actually exposes in a real
    /// match. It changes NOTHING in the game or the overlay and never filters the pool — pure
    /// research instrumentation, safe to delete wholesale. Everything is wrapped in try/catch
    /// because HDT mutates game-state collections on its own threads.
    /// </summary>
    internal sealed class GameStateProbe
    {
        // Tag names we care about for BG awareness (substring, case-insensitive). The one-time full
        // dump shows everything; this keeps the per-change lines readable.
        private static readonly string[] TagKeywords =
            { "BACON", "TECH", "TAVERN", "TRIPLE", "ANOMAL", "SUBSET", "RACE", "POOL", "TURN" };

        private readonly CardStore _store;
        private DateTime _lastPoll = DateTime.MinValue;
        private string _last;
        private bool _inMatch;
        private bool _dumpedAllTags;
        private string _lastLobby;   // spike: BG lobby roster (opponent battletags via HearthMirror)
        private int _lastHoverId = int.MinValue;   // spike: leaderboard hover detection
        private string _lastWinRect;               // spike: HS window geometry (slot-position math)
        private string _lastPlaces;                // spike: live placement order (PLAYER_LEADERBOARD_PLACE)

        public GameStateProbe(CardStore store) { _store = store; }

        public void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < 1500) return;   // throttle
                _lastPoll = now;

                var g = Core.Game;
                if (g == null || !g.IsBattlegroundsMatch)
                {
                    if (_inMatch) { Write("=== left BG match ==="); _inMatch = false; _last = null; _dumpedAllTags = false; _lastLobby = null; _lastHoverId = int.MinValue; _lastWinRect = null; _lastPlaces = null; }
                    return;
                }
                if (!_inMatch) { Write("=== entered BG match ==="); _inMatch = true; }

                if (!_dumpedAllTags)
                {
                    // Reveal every tag once, so we can discover the real names (e.g. lobby tribes)
                    // without guessing enum members at compile time.
                    Write("FULL TAG DUMP (one-time):");
                    Write("  playerEntity: " + AllTags(g.PlayerEntity));
                    Write("  gameEntity:   " + AllTags(g.GameEntity));
                    _dumpedAllTags = true;
                }

                // SPIKE (go/no-go): read the BG lobby roster + my battletag/rating via HearthMirror's
                // already-connected client (HDT starts the IPC client). If the players' Names populate
                // at match start without hovering portraits, the whole plugin HUD is unblocked.
                DumpLobby();

                // SPIKE 2 (per-portrait UI): can we tell which player is in each leaderboard slot, and
                // where each slot is on screen? Logs (a) hover detection + entity→player, (b) the HS
                // window rect for slot-position math. Combined with DumpLobby's ORDER (does the roster
                // track live placement as players die?), this is the go/no-go for Image #1's per-portrait
                // labels vs. an anchored panel / hover-only fallback.
                DumpHoverWindow();

                // SPIKE 3 (the crux): live placement order via PLAYER_LEADERBOARD_PLACE on the hero
                // entities, mapped to player names. If this populates + reorders as standings change, we
                // can drive Image #1's per-portrait labels from (this order + fixed slot geometry).
                DumpPlacements();

                var sb = new StringBuilder();
                int turn = -1;
                try { turn = g.GetTurnNumber(); } catch { }
                sb.AppendLine($"[turn {turn}] solo={g.IsBattlegroundsSoloMatch} duos={g.IsBattlegroundsDuosMatch} combat={g.IsBattlegroundsCombatPhase}");
                sb.AppendLine("  player tags: " + CuratedTags(g.PlayerEntity));
                sb.AppendLine("  game tags:   " + CuratedTags(g.GameEntity));

                var p = g.Player;
                if (p != null)
                {
                    sb.AppendLine("  hero: " + Describe(p.Hero));
                    var minions = SafeList(p.Minions);
                    var allEntities = SafeList(g.Entities?.Values);
                    sb.AppendLine($"  board ({minions.Count}):");
                    foreach (var m in minions)
                    {
                        sb.AppendLine("    - " + Describe(m));
                        // Per-minion attached enchantments — does HDT expose card_id/name/text/creator?
                        foreach (var en in EnchantmentsOn(allEntities, m?.Id ?? -1, -1))
                            sb.AppendLine("        ench: " + DescribeEnch(en, g.Entities));
                    }
                    var trinkets = SafeList(p.Trinkets);
                    sb.AppendLine($"  trinkets ({trinkets.Count}):");
                    foreach (var t in trinkets) sb.AppendLine("    - " + Describe(t));

                    // Economy + hero/player-attached enchantments (gold-source + player-buff discovery).
                    sb.AppendLine("  gold: " + GoldLine(g.PlayerEntity));
                    int heroId = -1, peId = -1;
                    try { heroId = p.Hero?.Id ?? -1; } catch { }
                    try { peId = g.PlayerEntity?.Id ?? -1; } catch { }
                    var enchs = EnchantmentsOn(allEntities, heroId, peId);
                    sb.AppendLine($"  hero/player enchantments ({enchs.Count}):");
                    foreach (var e in enchs) sb.AppendLine("    - " + DescribeEnch(e, g.Entities));

                    // Anomaly diagnostics — ResolveAnomaly() came back blank for "all heroes are Marin".
                    // Is there a real CARDTYPE==BATTLEGROUND_ANOMALY entity, or is it only a GameEntity tag?
                    sb.AppendLine("  anomaly tags: globalDbid=" + GTag(g.GameEntity, GameTag.BACON_GLOBAL_ANOMALY_DBID)
                        + " allHeroesAreThisDbid=" + GTag(g.GameEntity, GameTag.BACON_ANOMALY_ALL_HEROES_ARE_THIS_DBID)
                        + " anomaly1=" + GTag(g.GameEntity, GameTag.ANOMALY1) + " anomaly2=" + GTag(g.GameEntity, GameTag.ANOMALY2));
                    foreach (var e in allEntities)
                    {
                        int ct; try { ct = e.GetTag(GameTag.CARDTYPE); } catch { continue; }
                        if (ct == (int)CardType.BATTLEGROUND_ANOMALY)
                            sb.AppendLine("    anomaly-entity: " + Describe(e));
                    }
                }

                var snap = sb.ToString().TrimEnd();
                if (snap != _last) { Write(snap); _last = snap; }
            }
            catch (Exception ex) { Write("poll error: " + ex.Message); }
        }

        // SPIKE: dump the BG lobby roster (all players' battletags), my own battletag, and my rating,
        // read via HearthMirror.Reflection.Client (HDT's in-process, already-connected mirror — no raw
        // scry, no Unity version string). Change-detected so we can see WHEN names populate relative to
        // match start (the go/no-go question: are all 8 available up front, not just on hover?).
        private void DumpLobby()
        {
            try
            {
                var client = HearthMirror.Reflection.Client;
                if (client == null) { if (_lastLobby != "noclient") { Write("LOBBY (Reflection.Client is null)"); _lastLobby = "noclient"; } return; }

                var sb = new StringBuilder();
                try { var bt = client.GetBattleTag(); sb.Append(bt == null ? "me=(null)" : "me=" + bt.Name + "#" + bt.Number); }
                catch (Exception ex) { sb.Append("me=ERR:" + ex.Message); }
                try { var ri = client.GetBattlegroundRatingInfo(); sb.Append(ri == null ? " myRating=(null)" : " myRating=" + ri.Rating); }
                catch (Exception ex) { sb.Append(" myRating=ERR:" + ex.Message); }

                var lobby = client.GetBattlegroundsLobbyInfo();
                if (lobby?.Players == null) sb.Append(" lobby=(null/empty)");
                else
                {
                    sb.Append(" uuid=" + lobby.GameUuid + " players(" + lobby.Players.Count + "):");
                    foreach (var p in lobby.Players)
                        sb.Append(" [" + (p?.Name ?? "?") + " hero=" + (p?.HeroCardId ?? "?") + "]");
                }

                var snap = sb.ToString();
                if (snap != _lastLobby) { Write("LOBBY " + snap); _lastLobby = snap; }
            }
            catch (Exception ex)
            {
                var msg = "lobby dump error: " + ex.Message;
                if (msg != _lastLobby) { Write(msg); _lastLobby = msg; }
            }
        }

        // SPIKE: leaderboard hover detection + HS window geometry.
        private void DumpHoverWindow()
        {
            // (a) Which portrait is the mouse over? HearthMirror exposes the hovered entity id; resolve it
            // to a card/name/controller so we learn how to map a hovered tile → a lobby player.
            try
            {
                int hid = -1;
                try { var h = HearthMirror.Reflection.Client?.GetBattlegroundsLeaderboardHoveredEntityId(); hid = h ?? -1; }
                catch { }
                if (hid != _lastHoverId)
                {
                    _lastHoverId = hid;
                    if (hid <= 0) Write("HOVER: (none)");
                    else
                    {
                        string d = "id=" + hid;
                        try
                        {
                            var g = Core.Game;
                            if (g?.Entities != null && g.Entities.TryGetValue(hid, out var e) && e != null)
                            {
                                string cid = e.CardId ?? "?";
                                string nm = "?"; try { nm = e.Card?.Name ?? "?"; } catch { }
                                int ctrl = -1; try { ctrl = e.GetTag(GameTag.CONTROLLER); } catch { }
                                d += " card=" + cid + " name='" + nm + "' controller=" + ctrl;
                            }
                            else d += " (not in Game.Entities)";
                        }
                        catch { }
                        Write("HOVER: " + d);
                    }
                }
            }
            catch { }

            // (b) The Hearthstone window rect — the leaderboard is a fixed-fraction layout within it, so
            // this lets us compute each slot's on-screen position without reading UI transforms.
            try
            {
                var hs = Process.GetProcessesByName("Hearthstone");
                if (hs.Length > 0)
                {
                    var hwnd = hs[0].MainWindowHandle;
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r))
                    {
                        string rect = $"pos=({r.left},{r.top}) size=({r.right - r.left}x{r.bottom - r.top})";
                        if (rect != _lastWinRect) { _lastWinRect = rect; Write("HS WINDOW: " + rect); }
                    }
                }
                foreach (var p in hs) p.Dispose();
            }
            catch { }
        }

        // SPIKE: live standings. Reads PLAYER_LEADERBOARD_PLACE off every entity that has it, maps each
        // to a lobby player by hero card id, and logs "place:name(hero)" sorted by place. Change-detected,
        // so a reorder (someone dies / overtakes) re-logs — proving whether we can track live standings.
        private void DumpPlacements()
        {
            try
            {
                var g = Core.Game;
                if (g?.Entities == null) return;

                var heroToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lobby = HearthMirror.Reflection.Client?.GetBattlegroundsLobbyInfo();
                    if (lobby?.Players != null)
                        foreach (var p in lobby.Players)
                            if (!string.IsNullOrEmpty(p?.HeroCardId) && !string.IsNullOrEmpty(p?.Name))
                                heroToName[p.HeroCardId] = p.Name;
                }
                catch { }

                var items = new List<KeyValuePair<int, string>>();
                foreach (var e in SafeList(g.Entities.Values))
                {
                    int place; try { place = e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE); } catch { continue; }
                    if (place <= 0) continue;
                    string cid = e?.CardId ?? "?";
                    string nm = heroToName.TryGetValue(cid, out var n) ? n : "?";
                    int ctrl = -1; try { ctrl = e.GetTag(GameTag.CONTROLLER); } catch { }
                    items.Add(new KeyValuePair<int, string>(place, $"{place}:{nm}({cid},ctrl{ctrl})"));
                }
                items.Sort((a, b) => a.Key.CompareTo(b.Key));
                var snap = string.Join("  ", items.Select(i => i.Value));
                if (snap != _lastPlaces) { _lastPlaces = snap; Write($"PLACES({items.Count}): {snap}"); }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        // Describe an entity: its CardId, live stats, HearthDb name, and OUR matched record (which
        // also validates the externalId join in practice).
        private string Describe(Entity e)
        {
            if (e == null) return "(none)";
            string cardId = e.CardId ?? "";
            string hdb = "?";
            try { hdb = e.Card?.Name ?? "?"; } catch { }
            int atk = 0, hp = 0;
            try { atk = e.Attack; hp = e.Health; } catch { }
            var ours = _store.Lookup(cardId);
            string oursStr = ours != null
                ? $"ours='{ours.Name}' tribe=[{string.Join(",", ours.MinionTypes ?? new List<string>())}] tier={ours.Tier}"
                : "ours=MISS";
            return $"{cardId} {atk}/{hp} hdb='{hdb}' {oursStr}";
        }

        private static List<Entity> SafeList(IEnumerable<Entity> src)
        {
            try { return src?.ToList() ?? new List<Entity>(); }
            catch { return new List<Entity>(); }
        }

        // Full economy tag readout for the player entity — to learn which tag carries each gold bonus
        // (MAXRESOURCES = cap raised; BACON_PLAYER_EXTRA_GOLD_NEXT_TURN = queued bonus like Overconfidence).
        private static string GoldLine(Entity pe)
        {
            if (pe == null) return "(no player entity)";
            try
            {
                int res = pe.GetTag(GameTag.RESOURCES), used = pe.GetTag(GameTag.RESOURCES_USED),
                    temp = pe.GetTag(GameTag.TEMP_RESOURCES), max = pe.GetTag(GameTag.MAXRESOURCES),
                    extraNext = pe.GetTag(GameTag.BACON_PLAYER_EXTRA_GOLD_NEXT_TURN),
                    overdrawn = pe.GetTag(GameTag.BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN);
                return $"RESOURCES={res} USED={used} TEMP={temp} MAX={max} EXTRA_NEXT={extraNext} OVERDRAWN_NEXT={overdrawn} (avail={res - used + temp})";
            }
            catch (Exception ex) { return "(err " + ex.Message + ")"; }
        }

        // Enchantment entities attached to the hero or the player entity (the persistent player-level buffs).
        private static string GTag(Entity e, GameTag t)
        {
            if (e == null) return "?";
            try { return e.GetTag(t).ToString(); } catch { return "?"; }
        }

        private static List<Entity> EnchantmentsOn(List<Entity> all, int heroId, int peId)
        {
            var outp = new List<Entity>();
            try
            {
                foreach (var e in all)
                {
                    if (e == null || !e.IsEnchantment) continue;
                    if ((heroId > 0 && e.IsAttachedTo(heroId)) || (peId > 0 && e.IsAttachedTo(peId)))
                        outp.Add(e);
                }
            }
            catch { }
            return outp;
        }

        // Dump one enchantment: name/text (HearthDb), its CREATOR (the source), and every tag — so we can
        // see what resolves cleanly vs. internal/[DNT] noise before committing to a CSV column.
        private static string DescribeEnch(Entity e, System.Collections.Generic.IDictionary<int, Entity> all)
        {
            if (e == null) return "(none)";
            string cardId = e.CardId ?? "";
            string name = "?"; try { name = e.Card?.Name ?? "?"; } catch { }
            string text = ""; try { text = (e.Card?.Text ?? "").Replace("\n", " ").Trim(); } catch { }
            string creator = "";
            try
            {
                int creatorId = e.GetTag(GameTag.CREATOR);
                if (creatorId > 0 && all != null && all.TryGetValue(creatorId, out var ce))
                    creator = ce?.Card?.Name ?? ce?.CardId ?? creatorId.ToString();
            }
            catch { }
            return $"'{name}' card={cardId} creator='{creator}' text=\"{text}\" tags=[{AllTags(e)}]";
        }

        private static string AllTags(Entity e)
        {
            if (e == null) return "(none)";
            try
            {
                return string.Join(", ", e.Tags
                    .OrderBy(kv => kv.Key.ToString())
                    .Select(kv => kv.Key.ToString() + "=" + kv.Value));
            }
            catch (Exception ex) { return "(err " + ex.Message + ")"; }
        }

        private static string CuratedTags(Entity e)
        {
            if (e == null) return "(none)";
            try
            {
                return string.Join(", ", e.Tags
                    .Where(kv => TagKeywords.Any(k => kv.Key.ToString().IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(kv => kv.Key.ToString())
                    .Select(kv => kv.Key.ToString() + "=" + kv.Value));
            }
            catch (Exception ex) { return "(err " + ex.Message + ")"; }
        }

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup", "gamestate.log");

        private static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
            }
            catch { /* diagnostics must never throw */ }
        }
    }
}
#endif
