using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker;                       // Core
using Hearthstone_Deck_Tracker.Hearthstone.Entities;  // Entity
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;
using HsbgCardLookup.Ui;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// Always-on HUD: reads the player's current trinkets + the lobby anomaly from HDT's live state and
    /// shows each as a persistent floating card, placed/sized per slot (saved across restarts). Joins
    /// are pure <c>entity.CardId → our card</c> (CardStore.Lookup) — no HearthDb, no dbfId:
    ///   • trinkets — <c>Player.Trinkets</c>, split lesser/greater by our <c>BgCard.TrinketTier</c>;
    ///   • anomaly  — the one entity in <c>Game.Entities</c> whose CardId maps to a <c>CardType=="anomaly"</c>.
    /// Driven by IPlugin.OnUpdate (throttled). Pure read — never mutates the game. State is read on the
    /// OnUpdate thread (defensively, collections mutate on HDT threads); all window work is marshalled
    /// to the UI thread, and only when the resolved set actually changes.
    /// </summary>
    public sealed class BgHud
    {
        private const double DefaultWidthDip = 170;   // starting art width for a HUD card (user can resize)

        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;

        private readonly HudSlot _lesser, _greater, _anomaly;

        private DateTime _lastPoll = DateTime.MinValue;
        private volatile string _lastSig;
        private volatile Desired _desired = new Desired();   // last game-state read (refreshed every 750ms)

        // Match-end latch: HDT keeps IsBattlegroundsMatch true through the post-game/placement (MMR)
        // screen, so we hide on the OnGameEnd event and stay hidden until a NEW match begins (a raw
        // IsBattlegroundsMatch false→true transition clears the latch — independent of event timing).
        private volatile bool _ended;
        private bool _wasBgMatch;
        private static BgHud _current;     // OnGameEnd routes here (so reloads don't stack subscriptions)
        private static bool _hooked;

        // Foreground gating: only show while HS or our own process (HDT) is foreground — so the HUD
        // vanishes when the user alt-tabs to Chrome etc. Resolving the fg process name is cached per pid.
        private readonly int _ownPid;
        private uint _lastFgPid;
        private bool _lastFgIsHs;

        public BgHud(CardStore store, PluginConfig config, Dispatcher ui)
        {
            _store = store; _config = config; _ui = ui;
            try { _ownPid = Process.GetCurrentProcess().Id; } catch { _ownPid = -1; }
            HookGameEvents();
            _lesser  = new HudSlot(config, () => config.LesserTrinketHud, 0);
            _greater = new HudSlot(config, () => config.GreaterTrinketHud, 1);
            _anomaly = new HudSlot(config, () => config.AnomalyHud, 2);
        }

        // ── Poll (OnUpdate thread) ───────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
                // Foreground is checked EVERY tick (cheap) so the HUD hides ~instantly on alt-tab; the
                // game-state read stays throttled (entity scan is the expensive part).
                bool focused = IsForeground();

                // Raw match transition (every tick): a fresh match (false→true) clears the end latch.
                bool isBg = false;
                try { var gg = Core.Game; isBg = gg != null && gg.IsBattlegroundsMatch; } catch { }
                if (isBg && !_wasBgMatch) _ended = false;
                _wasBgMatch = isBg;

                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds >= 750)
                {
                    _lastPoll = now;
                    _desired = ReadDesired();
                }

                var d = _desired;
                bool show = focused && d.InMatch;
                string sig = show
                    ? $"1|{_config.ShowTrinkets}|{_config.ShowAnomaly}|{d.Lesser?.ExternalId}|{d.Greater?.ExternalId}|{d.Anomaly?.ExternalId}"
                    : "0";
                if (sig == _lastSig) return;        // nothing changed → no UI work
                _lastSig = sig;
                var dd = d; bool fg = focused;
                _ui?.BeginInvoke(new Action(() => Apply(dd, fg)));
            }
            catch { /* OnUpdate must never throw */ }
        }

        /// <summary>Re-apply immediately after a settings toggle (called on the UI thread).</summary>
        public void OnSettingsChanged()
        {
            try { _lastSig = null; Apply(ReadDesired(), IsForeground()); } catch { }
        }

        // True when the foreground window is Hearthstone OR our own process (HDT — which includes the
        // F3 overlay, settings, and these HUD windows). Anything else (Chrome, etc.) → false → hide.
        private bool IsForeground()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == 0) return false;
                if (pid == _ownPid) return true;             // HDT / our own windows
                if (pid == _lastFgPid) return _lastFgIsHs;   // cached resolution for this fg process
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

        /// <summary>Close all HUD windows (plugin unload, UI thread).</summary>
        public void CloseAll()
        {
            try { _lesser.Close(); _greater.Close(); _anomaly.Close(); } catch { }
        }

        // Subscribe to HDT's game-end once per process. ActionList has Add but no Remove, so route
        // through a static "current" pointer — a plugin reload swaps the target, never stacks handlers.
        private void HookGameEvents()
        {
            _current = this;
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => _current?.MarkEnded())); }
            catch { /* event API absent → fall back to between-match hiding only */ }
        }

        // Match ended (HDT fires this as it tallies placement/MMR) → hide now and stay hidden until a
        // new match clears the latch. Clearing _desired makes the next poll hide within one tick.
        private void MarkEnded()
        {
            _ended = true;
            _desired = new Desired();
            _lastSig = null;
        }

        // ── Read live state (defensive snapshots; HDT mutates these on its own threads) ───────────
        private Desired ReadDesired()
        {
            var d = new Desired();
            try
            {
                var g = Core.Game;
                if (g == null || !g.IsBattlegroundsMatch || _ended) return d;   // not in match / post-game → hide all
                d.InMatch = true;

                try
                {
                    foreach (var e in Snapshot(g.Player?.Trinkets))
                    {
                        var c = _store.Lookup(StripGold(e?.CardId));
                        if (c == null) continue;
                        if (string.Equals(c.TrinketTier, "greater", StringComparison.OrdinalIgnoreCase)) d.Greater = c;
                        else d.Lesser = c;   // "lesser" (or unspecified) → the lesser slot
                    }
                }
                catch { }

                try
                {
                    // The active anomaly is whichever in-game entity maps to one of our anomaly cards.
                    foreach (var e in Snapshot(g.Entities?.Values))
                    {
                        var c = _store.Lookup(StripGold(e?.CardId));
                        if (c != null && string.Equals(c.CardType, "anomaly", StringComparison.OrdinalIgnoreCase))
                        { d.Anomaly = c; break; }
                    }
                }
                catch { }
            }
            catch { }
            return d;
        }

        // ── Apply to windows (UI thread) ─────────────────────────────────────────────────────────
        private void Apply(Desired d, bool focused)
        {
            bool inMatch = d.InMatch && focused;   // not focused → treat as nothing to show (hide all)
            ReconcileSlot(_lesser,  _config.ShowTrinkets && inMatch ? d.Lesser  : null);
            ReconcileSlot(_greater, _config.ShowTrinkets && inMatch ? d.Greater : null);
            ReconcileSlot(_anomaly, _config.ShowAnomaly  && inMatch ? d.Anomaly : null);
        }

        private void ReconcileSlot(HudSlot slot, BgCard target)
        {
            if (target == null) { slot.Hide(); return; }
            if (slot.IsShowing(target)) return;             // already this card
            slot.PendingId = target.ExternalId;

            // Art may not be cached (HUD cards appear mid-match, not from browsing) → spawn once it loads.
            var bmp = CardArt.GetSync(target, false, 0);
            if (bmp != null) { slot.SetCard(target, bmp); return; }

            var tgt = target;
            CardArt.LoadAsync(target, false, 0).ContinueWith(t =>
            {
                var b = t.Result; if (b == null) return;
                _ui?.BeginInvoke(new Action(() =>
                {
                    if (slot.PendingId == tgt.ExternalId) slot.SetCard(tgt, b);
                }));
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        // Tripled/golden ids carry a trailing _G with no record of their own → use the base card.
        private static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);

        private static IEnumerable<Entity> Snapshot(IEnumerable<Entity> src)
        {
            try { return src == null ? new List<Entity>() : src.ToList(); }
            catch { return new List<Entity>(); }
        }

        // Default starting spot: a vertical stack down the right edge of the work area.
        private static Point DefaultPos(int index)
        {
            var wa = SystemParameters.WorkArea;
            double w = DefaultWidthDip + 2 * 5;       // + grab ring
            double h = w * 1.4;                        // rough card aspect
            double x = wa.Right - w - 24;
            double y = wa.Top + 24 + index * (h + 14);
            return new Point(x, y);
        }

        private sealed class Desired
        {
            public bool InMatch;
            public BgCard Lesser, Greater, Anomaly;
        }

        /// <summary>One HUD slot: owns its window, remembers what card it's showing, and persists its
        /// own placement+size (it is the window's <see cref="IFloatingCardHost"/>).</summary>
        private sealed class HudSlot : IFloatingCardHost
        {
            private readonly PluginConfig _config;
            private readonly Func<HudPlacement> _slot;
            private readonly int _index;

            private FloatingCard _win;
            private string _shownId;
            public string PendingId;

            public HudSlot(PluginConfig config, Func<HudPlacement> slot, int index)
            { _config = config; _slot = slot; _index = index; }

            public bool IsShowing(BgCard card) =>
                _win != null && _win.IsVisible && _shownId == card.ExternalId;

            public void SetCard(BgCard card, BitmapSource bmp)
            {
                var p = _slot();
                if (_win == null)
                {
                    double w = p.Set && p.W > 0 ? p.W : DefaultWidthDip;
                    _win = new FloatingCard(this, bmp, w, closable: false);
                    _win.Show();
                    var pos = p.Set ? new Point(p.X, p.Y) : DefaultPos(_index);
                    _win.Place(pos.X, pos.Y);
                }
                else
                {
                    _win.SetArt(bmp);     // card changed (e.g. trinket upgraded) → keep user's place/size
                    _win.Show();
                }
                _shownId = card.ExternalId;
            }

            public void Hide()
            {
                PendingId = null;
                _shownId = null;
                try { _win?.Hide(); } catch { }   // keep the window for reuse next match (place/size persist)
            }

            public void Close()
            {
                try { _win?.Close(); } catch { }
                _win = null; _shownId = null; PendingId = null;
            }

            // IFloatingCardHost
            public void Remove(FloatingCard card) { /* HUD cards aren't user-closable */ }

            public void GeometryChanged(FloatingCard card)
            {
                var p = _slot();
                p.Set = true; p.X = card.Left; p.Y = card.Top; p.W = card.DisplayWidth;
                _config.Save();
            }
        }
    }
}
