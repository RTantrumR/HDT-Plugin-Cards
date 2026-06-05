using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Tracks the floating cards the user has dragged onto the screen. Owns their lifetime, remembers
    /// the last scale (so each new card matches the previous one), and ties their visibility to the
    /// overlay when <see cref="PluginConfig.HideDraggedWithApp"/> is on — they hide with the app and
    /// reappear when it reopens. When that setting is off they live independently of the overlay.
    /// All methods run on the UI thread.
    /// </summary>
    public sealed class FloatingCardManager : IFloatingCardHost
    {
        private readonly PluginConfig _config;
        private readonly List<FloatingCard> _cards = new List<FloatingCard>();
        private bool _appVisible = true;   // overlay open while a card can be dragged out

        public FloatingCardManager(PluginConfig config) { _config = config; }

        // A card's desired visibility is a pure function of the setting + the app's state, so it never
        // gets stranded: hide-with-app on → follow the overlay; off → always visible (independent).
        private bool DesiredVisible => !_config.HideDraggedWithApp || _appVisible;

        // Reconcile every card to the desired visibility. Called on app show/hide AND on setting change,
        // so flipping the toggle can never leave a hidden subset behind to resurface later.
        private void Reconcile()
        {
            bool show = DesiredVisible;
            foreach (var c in _cards)
            {
                if (show) c.Show(); else c.Hide();
            }
        }

        /// <summary>Create a floating card from the given art and return it so the caller can keep it
        /// under the cursor while the drag-out continues. Sizes it to the last scaled width if the user
        /// has resized one before; otherwise to <paramref name="fallbackWidth"/> (the detail-pane size)
        /// so it appears at the size the user was just looking at — not blown up to native resolution.</summary>
        public FloatingCard Spawn(BitmapSource art, double fallbackWidth)
        {
            if (art == null) return null;
            _appVisible = true;   // you can only drag a card out while the overlay is open
            double initial = _config.FloatingCardWidth > 0 ? _config.FloatingCardWidth : fallbackWidth;
            var card = new FloatingCard(this, art, initial);
            _cards.Add(card);
            card.Show();          // ShowActivated=false → doesn't steal foreground from the overlay/game
            card.CenterOnCursor();
            return card;
        }

        /// <summary>Remember the width a card was scaled to so the next dragged card matches it.</summary>
        public void RememberWidth(double width)
        {
            if (width <= 0) return;
            _config.FloatingCardWidth = width;
            _config.Save();
        }

        public void Remove(FloatingCard card)
        {
            _cards.Remove(card);
        }

        // IFloatingCardHost: a dragged card's move/resize ended → remember its width for the next spawn.
        // (Dragged cards don't persist position — only the width is carried forward.)
        public void GeometryChanged(FloatingCard card) => RememberWidth(card.DisplayWidth);

        /// <summary>Called when the overlay shows/hides. With HideDraggedWithApp on, the floating cards
        /// follow it (hidden, then restored on reopen). With it off they stay visible regardless.</summary>
        public void OnAppVisibilityChanged(bool appVisible)
        {
            _appVisible = appVisible;
            Reconcile();
        }

        /// <summary>Called when the HideDraggedWithApp setting is toggled — reconcile immediately so the
        /// single card pool can't split into stranded hidden/visible subsets across the change.</summary>
        public void OnSettingChanged() => Reconcile();

        /// <summary>Close every floating card (plugin unload).</summary>
        public void CloseAll()
        {
            foreach (var c in _cards.ToArray())
            {
                try { c.Close(); } catch { }
            }
            _cards.Clear();
        }
    }
}
