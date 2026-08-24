using System;
using System.Collections.Generic;
using System.Linq;
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
    /// shows each as a card ON HDT'S OVERLAY CANVAS (see <see cref="HudCanvasCard"/> — drag/resize/
    /// right-click without Hearthstone ever losing foreground; window tracking, foreground gating and
    /// DPI come from HDT). Joins are pure <c>entity.CardId → our card</c> (CardStore.Lookup) — no
    /// HearthDb, no dbfId:
    ///   • trinkets — <c>Player.Trinkets</c>, filling up to four boxes positionally (a box appears
    ///     for each trinket actually held: usually two, but transforms and anomalies make other
    ///     counts real);
    ///   • anomaly  — the one entity in <c>Game.Entities</c> whose CardId maps to a <c>CardType=="anomaly"</c>.
    /// Driven by IPlugin.OnUpdate (throttled). Pure read — never mutates the game. State is read on the
    /// OnUpdate thread (defensively, collections mutate on HDT threads); all canvas work is marshalled
    /// to the overlay-canvas dispatcher, and only when the resolved set actually changes.
    ///
    /// An "arrange" mode (<see cref="SetArrange"/>, the HDT-style "unlock overlay") shows the boxes being
    /// positioned as draggable/resizable placeholders — with sample art + a dashed outline + a label — so
    /// the HUD can be laid out before anything is acquired, and before the feature is even switched on.
    /// NB: the canvas only renders while Hearthstone is up (and interacts while it's foreground), so
    /// arranging needs HS running.
    /// </summary>
    public sealed class BgHud
    {
        private const int TrinketSlots = 4;           // the most trinkets a player can hold at once
        // Arrange-mode slot labels. Boxes 1 and 2 keep their familiar names even though the fill is
        // positional: in the ordinary match that IS what lands in them, and a transform putting a
        // second greater in box 1 is exactly the case the player needs to notice.
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
        private bool _editing;                                // arrange mode owns the cards (canvas thread only)
        private ArrangeTarget _arrange = ArrangeTarget.None;  // which feature this arrange session targets

        // Match-end latch: HDT keeps IsBattlegroundsMatch true through the post-game/placement (MMR)
        // screen, so we hide on the OnGameEnd event and stay hidden until a NEW match begins (a raw
        // IsBattlegroundsMatch false→true transition clears the latch — independent of event timing).
        private volatile bool _ended;
        private bool _wasBgMatch;
        private static BgHud _current;     // OnGameEnd routes here (so reloads don't stack subscriptions)
        private static bool _hooked;

        public BgHud(CardStore store, PluginConfig config, Dispatcher ui)
        {
            _store = store; _config = config; _ui = ui;
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

        // The cards live on HDT's overlay canvas, so their work belongs on that canvas' dispatcher.
        private void Marshal(Action action)
        {
            try { (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.BeginInvoke(action); } catch { }
        }

        // ── Poll (OnUpdate thread) ───────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
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
                string sig = d.InMatch
                    ? "1|" + _config.ShowTrinkets + "|" + _config.ShowAnomaly + "|"
                        + string.Join(",", d.Trinkets.Select(c => c?.ExternalId)) + "|" + d.Anomaly?.ExternalId + "|"
                        + string.Concat(_trinkets.Select(s => s.Suppressed ? '1' : '0')) + (_anomaly.Suppressed ? '1' : '0')
                    : "0";
                if (sig == _lastSig) return;        // nothing changed → no UI work
                _lastSig = sig;
                var dd = d;
                Marshal(() => Apply(dd));
            }
            catch { /* OnUpdate must never throw */ }
        }

        /// <summary>Re-apply immediately after a settings toggle.</summary>
        public void OnSettingsChanged()
        {
            try
            {
                var d = ReadDesired();
                Marshal(() =>
                {
                    if (_editing) { EnterEdit(); return; }   // re-show placeholders for the new toggle state
                    _lastSig = null;
                    Apply(d);
                });
            }
            catch { }
        }

        // ── Arrange mode (the HDT-style "unlock overlay") ────────────────────────────────────────

        /// <summary>Enter/exit arrange mode. While on, every ENABLED box is shown as a draggable/
        /// resizable placeholder (sample art + dashed outline + label) regardless of match state, so
        /// the HUD can be laid out with nothing acquired. Geometry persists per move/resize via the
        /// normal <see cref="HudSlot"/> path. Needs Hearthstone running (the canvas renders over it).</summary>
        internal void SetArrange(ArrangeTarget target)
        {
            try
            {
                bool on = target == ArrangeTarget.Trinkets || target == ArrangeTarget.Anomaly;
                var d = on ? ReadDesired() : null;
                Marshal(() =>
                {
                    _arrange = target;
                    _editing = on;
                    if (on) EnterEdit(d);
                    else
                    {
                        foreach (var s in _trinkets) s.ExitEdit();
                        _anomaly.ExitEdit();
                        _lastSig = null;
                        // Also runs when ANOTHER feature is being arranged: Apply then hides these
                        // cards, which is exactly what "only show what's being arranged" means.
                        Apply(ReadDesired());   // restore live cards / hide empties
                    }
                });
            }
            catch { }
        }

        // Show placeholders for the slots being arranged (canvas thread). The feature's own on/off
        // toggle is deliberately ignored — you can position a HUD before switching it on. All four
        // boxes are shown: in play a box only exists while a trinket occupies it, so arranging is the
        // one moment the 3rd and 4th have to be placed, before the match that fills them.
        private void EnterEdit(Desired d = null)
        {
            if (d == null) d = ReadDesired();
            bool trinkets = _arrange == ArrangeTarget.Trinkets;
            for (int i = 0; i < _trinkets.Length; i++)
            {
                if (trinkets)
                    EnterEditSlot(_trinkets[i], d.InMatch ? d.Trinkets[i] : null,
                                  SampleTrinket(_store, greater: i == 1), TrinketLabels[i]);
                else _trinkets[i].Hide();
            }
            if (_arrange == ArrangeTarget.Anomaly)
                EnterEditSlot(_anomaly, d.InMatch ? d.Anomaly : null, SampleAnomaly(_store), "Anomaly");
            else _anomaly.Hide();
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
                    Marshal(() => { if (_editing) slot.SetEditArt(b); });
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch { }
        }

        /// <summary>A stand-in card for an empty box — arrange mode and the settings preview both use
        /// it, so a box looks the same wherever you are laying it out.</summary>
        internal static BgCard SampleTrinket(CardStore store, bool greater)
        {
            try
            {
                return store?.All?.FirstOrDefault(c => greater
                    ? IsGreater(c)
                    : (!string.IsNullOrEmpty(c.TrinketTier) && !IsGreater(c)));
            }
            catch { return null; }
        }

        internal static BgCard SampleAnomaly(CardStore store)
        {
            try { return store?.All?.FirstOrDefault(c => string.Equals(c.CardType, "anomaly", StringComparison.OrdinalIgnoreCase)); }
            catch { return null; }
        }

        // A translucent tile used as a placeholder before (or instead of) real sample art loads.
        // Its pixel WIDTH matches real card art (~256px) so the native-px scale ceiling in
        // HudCanvasCard can't collapse a saved size while the placeholder is up.
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

        /// <summary>Remove all HUD cards from the canvas (plugin unload).</summary>
        public void CloseAll()
        {
            try
            {
                var t = _trinkets; var a = _anomaly;
                (Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher ?? _ui)?.Invoke(new Action(() =>
                {
                    foreach (var s in t) s.Close();
                    a.Close();
                }));
            }
            catch { }
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
                    // Fill the boxes POSITIONALLY, in the order the trinkets were created (entity
                    // id ascending — a transform keeps its entity, so a card already in a box never
                    // gets reshuffled out of it). The old mapping reserved box 0 for a lesser and
                    // box 1 for a greater, which drops a trinket the moment the pair isn't one of
                    // each — and several lessers exist precisely to break that pairing: Souvenir
                    // Stand / Rune of Transmutation / Trip Vouchers all end up as a SECOND GREATER,
                    // and Mysterious Orb as a second LESSER. In each of those a real trinket landed
                    // in an overflow box while box 0 or box 1 sat empty.
                    int next = 0;
                    foreach (var e in Snapshot(g.Player?.Trinkets).OrderBy(x => x?.Id ?? int.MaxValue))
                    {
                        var c = _store.Lookup(StripGold(e?.CardId));
                        if (c == null) continue;
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

        // Every box follows the one ShowTrinkets toggle. There is no separate opt-in for the 3rd/4th:
        // a box is only ever drawn when a trinket is actually in it, so extra boxes can't be clutter —
        // they are the difference between seeing a trinket you hold and not seeing it.
        private bool TrinketsEnabled => _config.ShowTrinkets && ArrangeSession.AllowsTrinkets;

        // ── Apply to canvas cards (canvas thread) ────────────────────────────────────────────────
        private void Apply(Desired d)
        {
            if (_editing) return;   // arrange mode owns the cards; poll updates wait until it exits
            for (int i = 0; i < _trinkets.Length; i++)
                ReconcileSlot(_trinkets[i], TrinketsEnabled && d.InMatch && !_trinkets[i].Suppressed ? d.Trinkets[i] : null);
            ReconcileSlot(_anomaly, _config.ShowAnomaly && ArrangeSession.AllowsAnomaly
                && d.InMatch && !_anomaly.Suppressed ? d.Anomaly : null);
        }

        // ── HUD right-click menu (canvas thread — the card's WPF event routes here) ──────────────
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

            // Art may not be cached (HUD cards appear mid-match, not from browsing) → show once it loads.
            var bmp = CardArt.GetSync(target, false, 0);
            if (bmp != null) { slot.SetCard(target, bmp); return; }

            var tgt = target;
            CardArt.LoadAsync(target, false, 0).ContinueWith(t =>
            {
                var b = t.Result; if (b == null) return;
                Marshal(() =>
                {
                    if (slot.PendingId == tgt.ExternalId) slot.SetCard(tgt, b);
                });
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

        private sealed class Desired
        {
            public bool InMatch;
            public readonly BgCard[] Trinkets = new BgCard[TrinketSlots];
            public BgCard Anomaly;
        }

        /// <summary>One HUD slot: owns its canvas card, remembers what it's showing, and persists its
        /// own placement (canvas fractions; a legacy screen-DIP placement converts once).</summary>
        private sealed class HudSlot
        {
            private readonly PluginConfig _config;
            private readonly Func<HudPlacement> _slot;
            private readonly int _index;
            private readonly bool _isAnomaly;
            private readonly Action<HudSlot> _onRightClick;

            private HudCanvasCard _card;
            private string _shownId;
            public string PendingId;
            public bool Suppressed;   // "close until end of match" — cleared on match end / new match
            public bool IsAnomaly => _isAnomaly;

            public HudSlot(PluginConfig config, Func<HudPlacement> slot, int index, bool isAnomaly,
                Action<HudSlot> onRightClick)
            { _config = config; _slot = slot; _index = index; _isAnomaly = isAnomaly; _onRightClick = onRightClick; }

            public bool IsShowing(BgCard card) =>
                _card != null && _card.IsVisible && _shownId == card.ExternalId;

            public void SetCard(BgCard card, BitmapSource bmp)
            {
                EnsureCard();
                _card.SetArt(bmp);
                _card.ClearEditChrome();   // a real card is never a placeholder
                ShowSaved();
                _shownId = card.ExternalId;
            }

            // Arrange mode: show the slot as a placeholder (sample art + chrome) at its saved/default
            // placement, with no real card bound (so exiting arrange mode reconciles it cleanly).
            public void EnterEdit(BitmapSource bmp, string label)
            {
                EnsureCard();
                _card.SetArt(bmp);
                ShowSaved();
                _card.SetEditChrome(label);
                _shownId = null; PendingId = null;
            }

            public void SetEditArt(BitmapSource bmp) { try { _card?.SetArt(bmp); } catch { } }

            public void ExitEdit() { try { _card?.ClearEditChrome(); } catch { } }

            private void EnsureCard()
            {
                if (_card != null) return;
                _card = new HudCanvasCard(_index, _isAnomaly);
                _card.RightClicked = () => _onRightClick?.Invoke(this);
                _card.GeometryChanged = (xf, yf, wf) =>
                {
                    var p = _slot();
                    p.Set = true; p.XF = xf; p.YF = yf; p.WF = wf;
                    _config.Save();
                };
            }

            // Show at the saved fractions; a pre-canvas config (screen-DIP X/Y/W, no fractions yet)
            // converts once against the live canvas and is written back.
            private void ShowSaved()
            {
                var p = _slot();
                if (p.WF <= 0 && p.Set && p.W > 0
                    && HudCanvasCard.LegacyToFrac(p.X, p.Y, p.W, out double xf, out double yf, out double wf))
                {
                    p.XF = xf; p.YF = yf; p.WF = wf;
                    try { _config.Save(); } catch { }
                }
                _card.ShowAt(p.XF, p.YF, p.WF);
            }

            public void Hide()
            {
                PendingId = null;
                _shownId = null;
                try { _card?.ClearEditChrome(); } catch { }
                try { _card?.Hide(); } catch { }   // stays attached for reuse next match
            }

            public void Close()
            {
                try { _card?.Close(); } catch { }
                _card = null; _shownId = null; PendingId = null;
            }
        }
    }
}
