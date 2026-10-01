using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Session;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HsbgCardLookup.Game.Recap;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A click on a game row in HDT's own Battlegrounds session list ("Latest Games") re-opens the
    /// recap for that match. Lifted from the final-board-stats branch's SessionRowHook, where the
    /// mechanism was live-verified 2026-08-25: HDT registers those rows hover-visible only — its
    /// 60 Hz loop synthesises MouseEnter/Leave while the overlay stays click-through — so a click
    /// can never land on them as shipped; opting a row into HDT's hit-test set through the public
    /// <c>OverlayExtensions.SetIsOverlayHitTestVisible</c> is what makes the click arrive.
    ///
    /// Rows are re-created whenever HDT rebuilds the list, so this is a rescan every
    /// <see cref="ScanMs"/> while Battlegrounds is on screen; an attached property marks each
    /// instance already hooked. The row's view model carries the match start time in exactly the
    /// string HDT's store keys games by — which is also our record's GameId.
    ///
    /// This touches HDT's visual tree, so every step degrades to "no click" and never throws into
    /// HDT's UI thread. Promotion is not free: while a row is hit-test visible, HDT drops
    /// click-through over it, so switching the feature off un-promotes the rows again.
    /// </summary>
    internal sealed class RecapSessionRowHook
    {
        private const int ScanMs = 2000;
        private const int MaxDepth = 40;

        private static readonly DependencyProperty HookedProp = DependencyProperty.RegisterAttached(
            "HsbgRecapRowHooked", typeof(bool), typeof(RecapSessionRowHook), new PropertyMetadata(false));

        private readonly RecapStore _store;
        private readonly Func<bool> _enabled;
        private readonly Action<RecapRecord> _show;
        private readonly Action<string> _log;
        private DateTime _lastScan = DateTime.MinValue;

        public RecapSessionRowHook(RecapStore store, Func<bool> enabled, Action<RecapRecord> show, Action<string> log)
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
                if (rec == null) { Log("row clicked, no recap for " + id + " (duos, or played with the recap off)"); return; }

                Log("session row -> " + id);
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
            try { _log?.Invoke("[Recap] rows: " + msg); } catch { }
        }
    }
}
