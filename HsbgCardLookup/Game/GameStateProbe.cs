// DEV-ONLY diagnostic logger. Compiled into Debug builds only (#if DEBUG) — Release/distribution
// builds exclude it entirely (and the Plugin.cs _probe wiring is likewise #if DEBUG'd), so it never
// ships and never writes gamestate.log for end users.
#if DEBUG
#warning GameStateProbe diagnostic is ACTIVE in this DEBUG build (writes gamestate.log via OnUpdate).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hearthstone_Deck_Tracker;                       // Core
using Hearthstone_Deck_Tracker.Hearthstone.Entities;  // Entity
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
                    if (_inMatch) { Write("=== left BG match ==="); _inMatch = false; _last = null; _dumpedAllTags = false; }
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
                    sb.AppendLine($"  board ({minions.Count}):");
                    foreach (var m in minions) sb.AppendLine("    - " + Describe(m));
                    var trinkets = SafeList(p.Trinkets);
                    sb.AppendLine($"  trinkets ({trinkets.Count}):");
                    foreach (var t in trinkets) sb.AppendLine("    - " + Describe(t));
                }

                var snap = sb.ToString().TrimEnd();
                if (snap != _last) { Write(snap); _last = snap; }
            }
            catch (Exception ex) { Write("poll error: " + ex.Message); }
        }

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
