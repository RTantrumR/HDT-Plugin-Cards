using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Session;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// Surface S1: a click on a game row in HDT's own Battlegrounds session list opens our panel
    /// for that match. HDT registers those rows hover-visible only — its 60 Hz loop synthesises
    /// MouseEnter/Leave for them while the overlay window stays click-through — so a click can
    /// never land on them as shipped. Opting the row into HDT's hit-test set through its public
    /// <c>OverlayExtensions.SetIsOverlayHitTestVisible</c> is what makes the click arrive
    /// (live-verified 2026-08-25: Down/Up reach the row with handled=False, hover keeps working).
    ///
    /// Rows are re-created whenever HDT rebuilds the session list, so this is a rescan every
    /// <see cref="ScanMs"/> while Battlegrounds is on screen, not a one-time hookup; an attached
    /// property marks each instance already hooked. The row's view model carries the match start
    /// time in exactly the string HDT's store keys games by — which is also our record's GameId.
    ///
    /// This touches HDT's visual tree, so every step degrades to "no click" and never throws into
    /// HDT's UI thread.
    /// </summary>
    internal sealed class SessionRowHook
    {
        private const int ScanMs = 2000;
        private const int MaxDepth = 40;

        private static readonly DependencyProperty HookedProp = DependencyProperty.RegisterAttached(
            "HsbgSessionRowHooked", typeof(bool), typeof(SessionRowHook), new PropertyMetadata(false));

        private readonly FinalBoardStore _store;
        private readonly Func<bool> _enabled;
        private readonly Action<FinalBoardRecord> _show;
        private readonly Action<string> _log;
        private DateTime _lastScan = DateTime.MinValue;

        public SessionRowHook(FinalBoardStore store, Func<bool> enabled, Action<FinalBoardRecord> show, Action<string> log)
        {
            _store = store;
            _enabled = enabled;
            _show = show;
            _log = log;
        }

        /// <summary>Call from OnUpdate (~100 ms); self-throttled. Cheap when not in Battlegrounds.</summary>
        public void Poll()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastScan).TotalMilliseconds < ScanMs) return;
            _lastScan = now;

            try
            {
                var g = Core.Game;
                if (g == null) return;
                bool bgOnScreen = false;
                try { bgOnScreen = g.IsBattlegroundsMatch || g.CurrentMode == Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode.BACON; } catch { }
                if (!bgOnScreen) return;

                var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
                if (canvas == null) return;
                canvas.Dispatcher.BeginInvoke(new Action(ScanOnUi));
            }
            catch { }
        }

        private void ScanOnUi()
        {
            try
            {
                var overlay = Core.Overlay;
                if (overlay == null) return;

                var rows = new List<BattlegroundsGameView>();
                Walk(overlay, rows, 0);
                if (rows.Count == 0) return;

                bool on = false;
                try { on = _enabled(); } catch { }

                foreach (var row in rows)
                {
                    bool hooked = (bool)row.GetValue(HookedProp);
                    if (on && !hooked) Hook(row);
                    else if (!on && hooked) Unhook(row);
                }
            }
            catch (Exception ex) { Log("scan failed: " + ex.Message); }
        }

        private void Hook(BattlegroundsGameView row)
        {
            row.SetValue(HookedProp, true);
            row.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(RowUp), true);
            try { OverlayExtensions.SetIsOverlayHitTestVisible(row, true); }
            catch (Exception ex) { Log("could not promote a session row: " + ex.Message); }
        }

        /// <summary>
        /// Promotion is not free: while a row is hit-test visible, HDT drops click-through over it,
        /// so a click there no longer reaches the game. Switching the feature off gives that back.
        /// The handler stays attached — it checks the toggle itself — and is simply idle.
        /// </summary>
        private void Unhook(BattlegroundsGameView row)
        {
            row.SetValue(HookedProp, false);
            try { OverlayExtensions.SetIsOverlayHitTestVisible(row, false); } catch { }
        }

        private void RowUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                bool on = false;
                try { on = _enabled(); } catch { }
                if (!on) return;

                var row = sender as BattlegroundsGameView;
                var vm = row?.DataContext as BattlegroundsGameViewModel;
                string id = vm?.StartTime;
                if (string.IsNullOrEmpty(id)) { Log("row clicked, no start time on its view model"); return; }

                var rec = _store.Find(id);
                if (rec == null)
                {
                    // The row exists, so HDT has the game; our store may just not have copied it yet.
                    try { _store.ImportFromHdt(); } catch { }
                    rec = _store.Find(id);
                }
                if (rec == null) { Log("row clicked, no record for " + id); return; }

                Log("session row → " + id);
                _show(rec);
            }
            catch (Exception ex) { Log("row click failed: " + ex.Message); }
        }

        private static void Walk(DependencyObject node, List<BattlegroundsGameView> found, int depth)
        {
            if (node == null || depth > MaxDepth) return;
            var row = node as BattlegroundsGameView;
            if (row != null) { found.Add(row); return; }   // nothing of ours lives inside a row
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(node); } catch { return; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(node, i); } catch { continue; }
                Walk(child, found, depth + 1);
            }
        }

        private void Log(string msg)
        {
            try { _log?.Invoke("[SessionRow] " + msg); } catch { }
        }
    }
}
