// DEV-ONLY diagnostic for the Final Board feature. Debug builds only (#if DEBUG, like
// GameStateProbe / DarkGiftProbe) — never ships, never writes for end users.
//
// Four questions, none of which can be answered by reading HDT's assembly (all of them were tried
// there first — see final-board-plan.md for what IS already settled):
//
//   1. WHERE the post-match window actually is. We want to draw on the death/victory screen, which
//      is not a state HDT names: it sits between our elimination and the return to the BG menu.
//      Logged as a change-only state line (CurrentMode/PreviousMode, IsBattlegroundsMatch, combat
//      phase, IsInMenu, turn, our leaderboard place, hero HP) so the real sequence can be read off.
//
//   2. WHETHER a click we attach to HDT's own session rows fires. HDT shows a final board on HOVER
//      there (BattlegroundsGameView.Game_MouseEnter), so those elements are hit-testable — but hover
//      and click are registered separately in HDT's overlay (GetIsOverlayHoverVisible vs
//      GetIsOverlayHitTestVisible), and a hover-only registration would let our click fall through
//      to Hearthstone. We attach down/up/enter handlers AND log which ancestor carries which flag,
//      so a negative result says why.
//
//   3. WHICH entities still exist after OnGameEnd, and for how long. Reign's plugin clones them
//      early to survive HDT clearing the match; before copying that workaround we measure the decay
//      ourselves (hero, HERO_POWER_ENTITY, trinkets, anomaly, board, leaderboard place).
//
//   4. WHAT HDT's own store receives, and when — including for DUOS, where Hearthstone shows both
//      teammates' warbands but HDT's GameItem may carry only ours. Also catches how long after the
//      match CurrentGameStats' MMR fields populate (they arrive asynchronously).
//
// Pure read — the ONE thing it adds to HDT is event handlers on HDT's own session rows, which is
// itself question 2. Writes finalboard.log next to spike.log; safe to delete wholesale.
#if DEBUG
#warning FinalBoardProbe diagnostic is ACTIVE in this DEBUG build (writes finalboard.log via OnUpdate).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;                                             // Core.Game, Core.Overlay
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Session;      // BattlegroundsGameView(Model)
using Hearthstone_Deck_Tracker.Hearthstone.Entities;                        // Entity
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;                       // BattlegroundsLastGames

namespace HsbgCardLookup.Game
{
    internal sealed class FinalBoardProbe
    {
        private const int PollMs = 300;          // fine enough to time the post-match transitions
        private const int RowScanMs = 2000;      // session rows are rebuilt as games are added
        private const double DecaySeconds = 30;  // how long to watch entities after the match ends

        // Routed through a static so a plugin reload can't leave two instances hooked to HDT's
        // events (GameEvents is an ActionList — Add only, no Remove).
        private static FinalBoardProbe _current;
        private static bool _hooked;

        // Marks a session row we already attached to, per element instance — survives the row being
        // re-parented, and unlike a HashSet of references it doesn't keep dead rows alive.
        private static readonly DependencyProperty HookedProp = DependencyProperty.RegisterAttached(
            "HsbgFinalBoardProbeHooked", typeof(bool), typeof(FinalBoardProbe), new PropertyMetadata(false));

        private DateTime _lastPoll = DateTime.MinValue;
        private DateTime _lastRowScan = DateTime.MinValue;
        private volatile bool _startFlag, _endFlag;
        private DateTime? _endedAt;
        private bool _inMatch;
        private string _lastState, _lastDecay, _lastStats;
        private int _lastStoreCount = -1;
        private int _rowsHooked;
        private int _lastRowCount = -1;

        public FinalBoardProbe()
        {
            Write("=== FinalBoardProbe armed (plugin loaded) ===");
            DumpStore("load");
            HookGameEvents();
        }

        // ── Poll (OnUpdate thread) ──────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < PollMs) return;
                _lastPoll = now;

                if (_startFlag)
                {
                    _startFlag = false;
                    _endedAt = null;
                    _lastDecay = null;
                    _lastStats = null;
                    Write("=== EVENT OnGameStart ===");
                }
                if (_endFlag)
                {
                    _endFlag = false;
                    _endedAt = now;
                    Write("=== EVENT OnGameEnd ===");
                }

                LogState();
                if (_endedAt.HasValue && (now - _endedAt.Value).TotalSeconds <= DecaySeconds)
                {
                    LogDecay(now - _endedAt.Value);
                    LogGameStats();
                }
                if (_lastStoreCount != StoreCount()) DumpStore("changed");

                if ((now - _lastRowScan).TotalMilliseconds >= RowScanMs)
                {
                    _lastRowScan = now;
                    ScanSessionRows();
                }
            }
            catch (Exception ex) { Write("Poll EX: " + ex.Message); }
        }

        // ── 1. Match/menu state, logged only when it changes ────────────────────────────────────
        private void LogState()
        {
            var g = Core.Game;
            if (g == null) return;

            string mode = "?", prev = "?";
            bool isBg = false, duos = false, combat = false, inMenu = false;
            int turn = 0, place = 0, hp = 0, armor = 0;

            try { mode = g.CurrentMode.ToString(); } catch { }
            try { prev = g.PreviousMode.ToString(); } catch { }
            try { isBg = g.IsBattlegroundsMatch; } catch { }
            try { duos = g.IsBattlegroundsDuosMatch; } catch { }
            try { combat = g.IsBattlegroundsCombatPhase; } catch { }
            try { inMenu = g.IsInMenu; } catch { }
            try { turn = g.GetTurnNumber(); } catch { }

            Entity hero = PlayerHero();
            if (hero != null)
            {
                try { hp = hero.Health; } catch { }
                try { armor = hero.GetTag(GameTag.ARMOR); } catch { }
                try { place = hero.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE); } catch { }
            }

            string line = string.Format(
                "STATE | mode={0} prev={1} | bg={2} duos={3} combat={4} menu={5} | turn={6} place={7} heroHp={8}+{9} | stats={10}",
                mode, prev, isBg, duos, combat, inMenu, turn, place, hp, armor, HasGameStats() ? "yes" : "no");

            if (line == _lastState) return;
            _lastState = line;
            Write(line);

            if (isBg && !_inMatch) { _inMatch = true; Write("--- entered BG match ---"); }
            else if (!isBg && _inMatch) { _inMatch = false; Write("--- left BG match ---"); }
        }

        // ── 3. What survives after the match ends ───────────────────────────────────────────────
        private void LogDecay(TimeSpan since)
        {
            var g = Core.Game;
            if (g == null) return;

            string heroCard = "-", hpCard = "-";
            int minions = 0, trinkets = 0, anomaly = 0, entities = 0, place = 0;

            Entity hero = PlayerHero();
            if (hero != null)
            {
                try { heroCard = hero.CardId ?? "-"; } catch { }
                try { place = hero.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE); } catch { }
            }
            try { var pe = g.PlayerEntity; if (pe != null) hpCard = HeroPowerCard(g, pe.GetTag(GameTag.HERO_POWER_ENTITY)); } catch { }
            try { var p = g.Player; if (p != null && p.Minions != null) minions = new List<Entity>(p.Minions).Count; } catch { }
            try { var p = g.Player; if (p != null && p.Trinkets != null) trinkets = new List<Entity>(p.Trinkets).Count; } catch { }
            try { var ge = g.GameEntity; if (ge != null) anomaly = ge.GetTag(GameTag.BACON_GLOBAL_ANOMALY_DBID); } catch { }
            try { if (g.Entities != null) entities = g.Entities.Count; } catch { }

            string line = string.Format(
                "DECAY | hero={0} place={1} | heroPower={2} | minions={3} trinkets={4} anomaly={5} | entities={6}",
                heroCard, place, hpCard, minions, trinkets, anomaly, entities);

            if (line == _lastDecay) return;
            _lastDecay = line;
            Write(string.Format("t=+{0:0.0}s  ", since.TotalSeconds) + line);
        }

        // ── 4a. GameStats — the MMR fields arrive asynchronously after the match ────────────────
        private void LogGameStats()
        {
            try
            {
                var s = Core.Game != null ? Core.Game.CurrentGameStats : null;
                if (s == null) return;

                int placement = 0, anomalyDbf = 0, lobbyHeroes = 0;
                try { var d = s.BattlegroundsDetails; if (d != null) { placement = d.FinalPlacement ?? 0; anomalyDbf = d.AnomalyDbfId ?? 0; lobbyHeroes = d.LobbyRawHeroDbfIds != null ? d.LobbyRawHeroDbfIds.Count : 0; } }
                catch { }

                string line = string.Format(
                    "GAMESTATS | rating={0}->{1} | placement={2} | turns={3} | anomalyDbf={4} | lobbyHeroes={5} | result={6} | hero={7}",
                    s.BattlegroundsRating, s.BattlegroundsRatingAfter, placement, s.Turns, anomalyDbf, lobbyHeroes, s.Result, s.PlayerHeroCardId);

                if (line == _lastStats) return;
                _lastStats = line;
                Write(line);
            }
            catch (Exception ex) { Write("GAMESTATS EX: " + ex.Message); }
        }

        // ── 4b. HDT's own final-board store ─────────────────────────────────────────────────────
        private void DumpStore(string why)
        {
            try
            {
                var inst = BattlegroundsLastGames.Instance;
                var games = inst != null ? inst.Games : null;
                _lastStoreCount = games != null ? games.Count : 0;
                Write(string.Format("STORE ({0}) | games={1}", why, _lastStoreCount));
                if (games == null) return;

                for (int i = 0; i < games.Count; i++)
                {
                    var it = games[i];
                    if (it == null) continue;
                    int n = 0;
                    var ids = new StringBuilder();
                    if (it.FinalBoard != null && it.FinalBoard.FinalBoard != null)
                    {
                        var board = it.FinalBoard.FinalBoard;
                        n = board.Count;
                        for (int k = 0; k < board.Count && k < 4; k++)
                        {
                            if (ids.Length > 0) ids.Append(',');
                            ids.Append(board[k] != null ? board[k].CardId : "?");
                        }
                    }
                    Write(string.Format(
                        "  STORE#{0} | start={1} | hero={2} | place={3} | {4}->{5} | duos={6} | friendly={7} | minions={8} | {9}",
                        i, it.StartTime, it.Hero, it.Placement, it.Rating,
                        it.RatingAfter.HasValue ? it.RatingAfter.Value.ToString() : "null",
                        it.Duos, it.FriendlyGame, n, ids));
                }
            }
            catch (Exception ex) { Write("STORE EX: " + ex.Message); }
        }

        private static int StoreCount()
        {
            try
            {
                var inst = BattlegroundsLastGames.Instance;
                return inst != null && inst.Games != null ? inst.Games.Count : 0;
            }
            catch { return -1; }
        }

        // ── 2. Can we click HDT's own session rows? ─────────────────────────────────────────────
        // The tree walk and the handler attach must both run on the thread that owns those elements
        // (HDT's UI thread), so everything here is marshalled onto the overlay's dispatcher.
        private void ScanSessionRows()
        {
            try
            {
                // Only while Battlegrounds is on screen: the rows exist in the BG menu and in a match,
                // and walking HDT's whole overlay tree every 2s elsewhere buys nothing.
                var g = Core.Game;
                if (g == null) return;
                bool bgOnScreen = false;
                try { bgOnScreen = g.IsBattlegroundsMatch || g.CurrentMode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.BACON; } catch { }
                if (!bgOnScreen) return;

                var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
                if (canvas == null) return;
                canvas.Dispatcher.BeginInvoke(new Action(ScanSessionRowsOnUi));
            }
            catch { }
        }

        private void ScanSessionRowsOnUi()
        {
            try
            {
                var overlay = Core.Overlay;
                if (overlay == null) return;

                var found = new List<BattlegroundsGameView>();
                Walk(overlay, found, 0);

                // Change-only, so an empty session list is distinguishable from a walk that cannot
                // see HDT's rows at all — without it, both look like silence.
                if (found.Count != _lastRowCount)
                {
                    _lastRowCount = found.Count;
                    Write("ROWS | visible in HDT's overlay tree: " + found.Count);
                }
                if (found.Count == 0) return;

                foreach (var row in found)
                {
                    if ((bool)row.GetValue(HookedProp)) continue;
                    row.SetValue(HookedProp, true);
                    row.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(RowDown), true);
                    row.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(RowUp), true);
                    row.MouseEnter += RowEnter;
                    _rowsHooked++;
                    Write("ROW HOOKED #" + _rowsHooked + " | " + Describe(row) + " | " + Registrations(row));
                }
            }
            catch (Exception ex) { Write("ROWSCAN EX: " + ex.Message); }
        }

        private static void Walk(DependencyObject node, List<BattlegroundsGameView> found, int depth)
        {
            if (node == null || depth > 40) return;
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(node, i); } catch { }
                if (child == null) continue;
                var row = child as BattlegroundsGameView;
                if (row != null) found.Add(row);
                Walk(child, found, depth + 1);
            }
        }

        // Which ancestor (if any) is registered with HDT's overlay as hit-test- or hover-visible.
        // A hover-only chain would explain a click that never arrives.
        private static string Registrations(DependencyObject node)
        {
            var sb = new StringBuilder();
            DependencyObject cur = node;
            for (int depth = 0; cur != null && depth < 25; depth++)
            {
                bool hit = false, hover = false;
                try { hit = Hearthstone_Deck_Tracker.Utility.Extensions.OverlayExtensions.GetIsOverlayHitTestVisible(cur); } catch { }
                try { hover = Hearthstone_Deck_Tracker.Utility.Extensions.OverlayExtensions.GetIsOverlayHoverVisible(cur); } catch { }
                if (hit || hover)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(cur.GetType().Name).Append('@').Append(depth)
                      .Append(hit ? " hitTest" : "").Append(hover ? " hover" : "");
                }
                try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
            }
            return sb.Length > 0 ? "registered: " + sb : "registered: NONE in ancestor chain";
        }

        private static string Describe(BattlegroundsGameView row)
        {
            try
            {
                var vm = row.DataContext as BattlegroundsGameViewModel;
                if (vm == null) return "vm=null";
                return string.Format("hero={0} place={1} mmr={2} minions={3} start={4}",
                    vm.HeroName, vm.Placement, vm.MMRDeltaText,
                    vm.FinalBoardMinions != null ? vm.FinalBoardMinions.Count : 0, vm.StartTime);
            }
            catch { return "vm=?"; }
        }

        private void RowDown(object sender, MouseButtonEventArgs e)
        {
            Write("ROW PreviewMouseLeftButtonDown | " + Describe(sender as BattlegroundsGameView) + " | handled=" + e.Handled);
        }

        private void RowUp(object sender, MouseButtonEventArgs e)
        {
            Write("ROW MouseLeftButtonUp | " + Describe(sender as BattlegroundsGameView) + " | handled=" + e.Handled);
        }

        private void RowEnter(object sender, MouseEventArgs e)
        {
            Write("ROW MouseEnter | " + Describe(sender as BattlegroundsGameView));
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────
        private static Entity PlayerHero()
        {
            try
            {
                var g = Core.Game;
                var p = g != null ? g.Player : null;
                return p != null ? p.Hero : null;
            }
            catch { return null; }
        }

        private static bool HasGameStats()
        {
            try { return Core.Game != null && Core.Game.CurrentGameStats != null; }
            catch { return false; }
        }

        private static string HeroPowerCard(Hearthstone_Deck_Tracker.Hearthstone.GameV2 g, int entityId)
        {
            if (entityId <= 0) return "-";
            try
            {
                Entity e;
                if (g.Entities != null && g.Entities.TryGetValue(entityId, out e) && e != null)
                    return (e.CardId ?? "?") + "#" + entityId;
            }
            catch { }
            return "missing#" + entityId;
        }

        private void HookGameEvents()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => { if (_current != null) _current._startFlag = true; })); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => { if (_current != null) _current._endFlag = true; })); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnInMenu.Add(new Action(() => Write("=== EVENT OnInMenu ==="))); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameWon.Add(new Action(() => Write("=== EVENT OnGameWon ==="))); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameLost.Add(new Action(() => Write("=== EVENT OnGameLost ==="))); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameTied.Add(new Action(() => Write("=== EVENT OnGameTied ==="))); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnModeChanged.Add(new Action<Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode>(m => Write("=== EVENT OnModeChanged -> " + m + " ==="))); } catch { }
        }

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup", "finalboard.log");

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
