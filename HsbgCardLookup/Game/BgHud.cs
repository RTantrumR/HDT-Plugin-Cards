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
    ///   • trinkets — <c>Player.Trinkets</c>, mapped to up to four boxes (lesser, greater, + 2 overflow,
    ///     since an anomaly can grant more than the usual pair);
    ///   • anomaly  — the one entity in <c>Game.Entities</c> whose CardId maps to a <c>CardType=="anomaly"</c>.
    /// Driven by IPlugin.OnUpdate (throttled). Pure read — never mutates the game. State is read on the
    /// OnUpdate thread (defensively, collections mutate on HDT threads); all window work is marshalled
    /// to the UI thread, and only when the resolved set actually changes.
    ///
    /// An "arrange" mode (<see cref="SetEditMode"/>, the HDT-style "unlock overlay") shows every enabled
    /// box as a draggable/resizable placeholder — with sample art + a dashed outline + a label — even out
    /// of match with no card present, so the HUD can be laid out before anything is acquired.
    /// </summary>
    public sealed class BgHud
    {
        private const double DefaultWidthDip = 170;   // starting art width for a HUD card (user can resize)

        private const int TrinketSlots = 4;           // lesser + greater + two overflow boxes
        private static readonly string[] TrinketLabels =
            { "Lesser Trinket", "Greater Trinket", "Trinket 3", "Trinket 4" };

        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;

        private readonly HudSlot[] _trinkets;   // [0]=lesser, [1]=greater, [2..3]=overflow
        private readonly HudSlot _anomaly;

        private DateTime _lastPoll = DateTime.MinValue;
        private volatile string _lastSig;
        private volatile Desired _desired = new Desired();   // last game-state read (refreshed every 750ms)
        private bool _editing;                                // arrange mode owns the windows (UI thread only)

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
            _trinkets = new[]
            {
                new HudSlot(config, () => config.LesserTrinketHud,  0, false, OnSlotRightClick),
                new HudSlot(config, () => config.GreaterTrinketHud, 1, false, OnSlotRightClick),
                new HudSlot(config, () => config.Trinket3Hud,       2, false, OnSlotRightClick),
                new HudSlot(config, () => config.Trinket4Hud,       3, false, OnSlotRightClick),
            };
            _anomaly = new HudSlot(config, () => config.AnomalyHud, 0, true, OnSlotRightClick);
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
                if (isBg && !_wasBgMatch) { _ended = false; ClearSuppressions(); }
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
                    ? "1|" + _config.ShowTrinkets + "|" + _config.ShowExtraTrinkets + "|" + _config.ShowAnomaly + "|"
                        + string.Join(",", d.Trinkets.Select(c => c?.ExternalId)) + "|" + d.Anomaly?.ExternalId + "|"
                        + string.Concat(_trinkets.Select(s => s.Suppressed ? '1' : '0')) + (_anomaly.Suppressed ? '1' : '0')
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
            try
            {
                if (_editing) { SetEditMode(true); return; }   // re-show placeholders for the new toggle state
                _lastSig = null; Apply(ReadDesired(), IsForeground());
            }
            catch { }
        }

        // ── Arrange mode (the HDT-style "unlock overlay") ────────────────────────────────────────

        /// <summary>Enter/exit arrange mode (UI thread). While on, every ENABLED box is shown as a
        /// draggable/resizable placeholder (sample art + dashed outline + label) regardless of match or
        /// foreground state, so the HUD can be laid out with nothing acquired. Geometry persists on each
        /// move/resize via the normal <see cref="HudSlot.GeometryChanged"/> path.</summary>
        public void SetEditMode(bool on)
        {
            try
            {
                _editing = on;
                if (on)
                {
                    var d = ReadDesired();   // reuse any live cards for true-to-life sizing
                    for (int i = 0; i < _trinkets.Length; i++)
                    {
                        if (TrinketSlotEnabled(i))
                            EnterEditSlot(_trinkets[i], d.InMatch ? d.Trinkets[i] : null,
                                          RepresentativeTrinket(greater: i == 1), TrinketLabels[i]);
                        else { _trinkets[i].ExitEdit(); _trinkets[i].Hide(); }
                    }
                    if (_config.ShowAnomaly)
                        EnterEditSlot(_anomaly, d.InMatch ? d.Anomaly : null, RepresentativeAnomaly(), "Anomaly");
                    else { _anomaly.ExitEdit(); _anomaly.Hide(); }
                }
                else
                {
                    foreach (var s in _trinkets) s.ExitEdit();
                    _anomaly.ExitEdit();
                    _lastSig = null;
                    Apply(ReadDesired(), IsForeground());   // restore live cards / hide empties
                }
            }
            catch { }
        }

        // Show one slot's placeholder: prefer a live card (real sizing); else a representative sample.
        // Art may not be cached yet → start with a fallback tile and upgrade async.
        private void EnterEditSlot(HudSlot slot, BgCard liveCard, BgCard representative, string label)
        {
            var sample = liveCard ?? representative;
            BitmapSource bmp = null;
            try { if (sample != null) bmp = CardArt.GetSync(sample, false, 0); } catch { }
            bool needUpgrade = bmp == null && sample != null;
            if (bmp == null) bmp = FallbackArt();
            slot.EnterEdit(bmp, label);
            if (needUpgrade) UpgradeEditArt(slot, sample);
        }

        private void UpgradeEditArt(HudSlot slot, BgCard sample)
        {
            try
            {
                CardArt.LoadAsync(sample, false, 0).ContinueWith(t =>
                {
                    var b = t.Result; if (b == null) return;
                    _ui?.BeginInvoke(new Action(() => { if (_editing) slot.SetEditArt(b); }));
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch { }
        }

        private BgCard RepresentativeTrinket(bool greater)
        {
            try
            {
                return _store.All?.FirstOrDefault(c => greater
                    ? IsGreater(c)
                    : (!string.IsNullOrEmpty(c.TrinketTier) && !IsGreater(c)));
            }
            catch { return null; }
        }

        private BgCard RepresentativeAnomaly()
        {
            try { return _store.All?.FirstOrDefault(c => string.Equals(c.CardType, "anomaly", StringComparison.OrdinalIgnoreCase)); }
            catch { return null; }
        }

        // A translucent tile used as a placeholder before (or instead of) real sample art loads.
        // CRITICAL: its pixel WIDTH must be >= real card art's (~256px) so it doesn't shrink a saved
        // size. FloatingCard clamps its initial width to native_px * 1.5; with a tiny tile that ceiling
        // would collapse a saved width (e.g. 300 → ~96) the moment a cold-cache placeholder is built,
        // and the later SetArt keeps that shrunk width. Matching the real native px (256) keeps the
        // saved size intact; real sample art re-derives the aspect on upgrade.
        private static BitmapSource _fallbackArt;
        private static BitmapSource FallbackArt()
        {
            if (_fallbackArt != null) return _fallbackArt;
            const int w = 256, h = 358;   // ~card aspect (1.4); width matches real art so saved sizes survive
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 0x30; px[i + 1] = 0x28; px[i + 2] = 0x20; px[i + 3] = 0x55; }
            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            _fallbackArt = bmp;
            return _fallbackArt;
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
            try { foreach (var s in _trinkets) s.Close(); _anomaly.Close(); } catch { }
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
            ClearSuppressions();   // "close until end of match" ends here
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
                    // Resolve every trinket entity, then map: first lesser → slot 0, first greater →
                    // slot 1, any extras (anomaly cases) fill the overflow slots in order.
                    var resolved = new List<BgCard>();
                    foreach (var e in Snapshot(g.Player?.Trinkets))
                    {
                        var c = _store.Lookup(StripGold(e?.CardId));
                        if (c != null) resolved.Add(c);
                    }
                    BgCard lesser = resolved.FirstOrDefault(c => !IsGreater(c));
                    BgCard greater = resolved.FirstOrDefault(IsGreater);
                    d.Trinkets[0] = lesser;
                    d.Trinkets[1] = greater;
                    int next = 2;
                    foreach (var c in resolved)
                    {
                        if (ReferenceEquals(c, lesser) || ReferenceEquals(c, greater)) continue;
                        if (next >= d.Trinkets.Length) break;
                        d.Trinkets[next++] = c;
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

        // Lesser/greater (0,1) follow ShowTrinkets; the two overflow boxes (2,3) also require the
        // opt-in ShowExtraTrinkets flag (off by default — some people only want the usual pair).
        private bool TrinketSlotEnabled(int i) => _config.ShowTrinkets && (i < 2 || _config.ShowExtraTrinkets);

        // ── Apply to windows (UI thread) ─────────────────────────────────────────────────────────
        private void Apply(Desired d, bool focused)
        {
            if (_editing) return;   // arrange mode owns the windows; poll updates wait until it exits
            bool inMatch = d.InMatch && focused;   // not focused → treat as nothing to show (hide all)
            for (int i = 0; i < _trinkets.Length; i++)
                ReconcileSlot(_trinkets[i], TrinketSlotEnabled(i) && inMatch && !_trinkets[i].Suppressed ? d.Trinkets[i] : null);
            ReconcileSlot(_anomaly, _config.ShowAnomaly && inMatch && !_anomaly.Suppressed ? d.Anomaly : null);
        }

        // ── HUD right-click menu (UI thread — the window's WndProc routes here) ──────────────────
        private void OnSlotRightClick(HudSlot slot)
        {
            if (_editing) return;   // arrange mode: right-click does nothing (boxes are placeholders)
            try
            {
                string feature = slot.IsAnomaly ? "anomaly display" : "trinket display";
                HudContextMenu.ShowMenu(new[]
                {
                    new KeyValuePair<string, Action>("Close until end of match", () =>
                    {
                        slot.Suppressed = true;
                        slot.Hide();
                        _lastSig = null;
                    }),
                    new KeyValuePair<string, Action>("Turn off " + feature, () =>
                    {
                        if (slot.IsAnomaly) _config.ShowAnomaly = false;
                        else _config.ShowTrinkets = false;
                        try { _config.Save(); } catch { }
                        OnSettingsChanged();
                    }),
                });
            }
            catch { }
        }

        private void ClearSuppressions()
        {
            foreach (var s in _trinkets) s.Suppressed = false;
            _anomaly.Suppressed = false;
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

        private static bool IsGreater(BgCard c) =>
            c != null && string.Equals(c.TrinketTier, "greater", StringComparison.OrdinalIgnoreCase);

        // Tripled/golden ids carry a trailing _G with no record of their own → use the base card.
        private static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);

        private static IEnumerable<Entity> Snapshot(IEnumerable<Entity> src)
        {
            try { return src == null ? new List<Entity>() : src.ToList(); }
            catch { return new List<Entity>(); }
        }

        // Default starting spot. Trinkets stack down the right work-area edge, wrapping to a new column
        // to the left when they'd run past the bottom (so all four fit). The anomaly defaults to the
        // top-left, well clear of the trinket column. All of this is just a first guess — the user drags.
        private static Point DefaultPos(int index, bool anomaly)
        {
            var wa = SystemParameters.WorkArea;
            double w = DefaultWidthDip + 2 * 5;       // + grab ring
            double h = w * 1.4;                         // rough card aspect
            if (anomaly) return new Point(wa.Left + 24, wa.Top + 24);

            double step = h + 14;
            int perCol = Math.Max(1, (int)((wa.Height - 48) / step));
            int col = index / perCol;
            int row = index % perCol;
            double x = wa.Right - w - 24 - col * (w + 14);
            double y = wa.Top + 24 + row * step;
            return new Point(x, y);
        }

        private sealed class Desired
        {
            public bool InMatch;
            public readonly BgCard[] Trinkets = new BgCard[TrinketSlots];
            public BgCard Anomaly;
        }

        /// <summary>One HUD slot: owns its window, remembers what card it's showing, and persists its
        /// own placement+size (it is the window's <see cref="IFloatingCardHost"/>).</summary>
        private sealed class HudSlot : IFloatingCardHost
        {
            private readonly PluginConfig _config;
            private readonly Func<HudPlacement> _slot;
            private readonly int _index;
            private readonly bool _isAnomaly;
            private readonly Action<HudSlot> _onRightClick;

            private FloatingCard _win;
            private string _shownId;
            public string PendingId;
            public bool Suppressed;   // "close until end of match" — cleared on match end / new match
            public bool IsAnomaly => _isAnomaly;

            public HudSlot(PluginConfig config, Func<HudPlacement> slot, int index, bool isAnomaly,
                Action<HudSlot> onRightClick)
            { _config = config; _slot = slot; _index = index; _isAnomaly = isAnomaly; _onRightClick = onRightClick; }

            public bool IsShowing(BgCard card) =>
                _win != null && _win.IsVisible && _shownId == card.ExternalId;

            public void SetCard(BgCard card, BitmapSource bmp)
            {
                EnsureWindow(bmp);
                if (_win == null) return;
                _win.SetArt(bmp);
                _win.ClearEditChrome();   // a real card is never a placeholder
                _win.Show();
                _shownId = card.ExternalId;
            }

            // Arrange mode: show the slot as a placeholder (sample art + chrome) at its saved/default
            // placement, with no real card bound (so exiting arrange mode reconciles it cleanly).
            public void EnterEdit(BitmapSource bmp, string label)
            {
                EnsureWindow(bmp);
                if (_win == null) return;
                _win.SetArt(bmp);
                _win.Show();
                _win.SetEditChrome(label);
                _shownId = null; PendingId = null;
            }

            public void SetEditArt(BitmapSource bmp) { try { _win?.SetArt(bmp); } catch { } }

            public void ExitEdit() { try { _win?.ClearEditChrome(); } catch { } }

            // Create the window at the saved-or-default placement if it doesn't exist yet.
            private void EnsureWindow(BitmapSource bmp)
            {
                if (_win != null || bmp == null) return;
                var p = _slot();
                double w = p.Set && p.W > 0 ? p.W : DefaultWidthDip;
                _win = new FloatingCard(this, bmp, w, closable: false);
                _win.RightClicked = () => _onRightClick?.Invoke(this);
                _win.Show();
                var pos = p.Set ? new Point(p.X, p.Y) : DefaultPos(_index, _isAnomaly);
                _win.Place(pos.X, pos.Y);
            }

            public void Hide()
            {
                PendingId = null;
                _shownId = null;
                try { _win?.ClearEditChrome(); } catch { }
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
