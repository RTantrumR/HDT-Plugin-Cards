using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A live preview of the Dark Gift panel for its settings page: the REAL panel, rendered by the
    /// REAL content builder, in whatever display mode is currently selected.
    ///
    /// It is shrunk to fit the page rather than shown 1:1 - the panel is 450-800px wide against a
    /// ~394px page - because the question this preview answers is composition (gift list, minion pool,
    /// or both) and the effect of the user's own scale, not the legibility of text they can already
    /// read in game.
    ///
    /// The content is real: the gift list, the tier windows, the guaranteed-tribe minion pool and its
    /// sole-enabler colouring all come from live card data. Only what cannot exist outside a match is
    /// supplied here - the turn, and the player's most common board tribe.
    /// </summary>
    internal sealed class DarkGiftPreview
    {
        // Tall enough that the gift-list-only shape is limited by the page WIDTH rather than by the
        // box height - that is the shape most users are looking at, and every pixel of scale it gets is
        // a pixel of readability.
        private const double MinViewH = 150, MaxViewH = 460;
        private const string SampleTribe = "Beast";
        internal static readonly int[] SampleTurns = { 4, 7, 11 };

        private readonly PluginConfig _config;
        private readonly Game.DarkGiftWatcher _engine;
        private readonly DarkGiftPanel _panel;
        private readonly Canvas _stage;
        private readonly Border _viewport;
        private readonly ScaleTransform _fit = new ScaleTransform(1, 1);
        private readonly double _viewW;

        private int _turn = SampleTurns[1];

        public FrameworkElement Root => _viewport;

        public DarkGiftPreview(PluginConfig config, CardStore store, double viewW)
        {
            _config = config;
            _viewW = viewW;

            PreviewStage.ResolveSize(out double cw, out double ch);

            // A DETACHED watcher: it hooks no game events (that would steal them from the live one via
            // the static "current" pointer) and only ever renders into the panel it is handed.
            _engine = new Game.DarkGiftWatcher(store, config, Application.Current?.Dispatcher, null, preview: true);

            _stage = new Canvas
            {
                Width = cw, Height = ch, IsHitTestVisible = false,
                RenderTransform = _fit, RenderTransformOrigin = new Point(0, 0)
            };

            var clip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            clip.Children.Add(_stage);
            clip.LayoutUpdated += (s, e) => UpdateFit();

            _viewport = new Border
            {
                Width = _viewW,
                Height = MinViewH,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x18, 0x1F, 0x2C), Color.FromRgb(0x0D, 0x11, 0x19),
                    new Point(0.35, 0), new Point(0.65, 1)),
                Margin = new Thickness(0, 2, 0, 10),
                Child = clip
            };

            // A hosted panel: no overlay hit-testing, no drag/resize gestures, no geometry written back.
            _panel = new DarkGiftPanel(_stage);

            Refresh();
        }

        /// <summary>Preview a different shop turn. The turn decides which gifts are offerable now,
        /// which are still to come, the offered tier window, and whether the turn-6 guaranteed-type
        /// rule (and so the minion pool) applies at all - so it changes the panel more than any other
        /// single fact.</summary>
        public void SetTurn(int turn)
        {
            if (_turn == turn) return;
            _turn = turn;
            Refresh();
        }

        /// <summary>Re-render in the current display mode. Called on every settings change.</summary>
        public void Refresh()
        {
            try
            {
                // Pin it to the stage origin at the user's own scale; the fit transform does the rest.
                var hud = _config.DarkGiftHud;
                _panel.Place(0, 0, hud != null ? hud.WF : 0);

                _engine.RenderInto(_panel, _turn, SampleTribe, duos: false, mode: _config.DarkGiftMode);

                _viewport.Dispatcher.BeginInvoke(new Action(UpdateFit),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        public void Close()
        {
            try { _panel.Close(); } catch { }
        }

        /// <summary>Scale the stage so the whole panel fits the box, and shrink the box to what the
        /// panel actually needs. Driven from LayoutUpdated because the panel's size is content-driven
        /// and only known once WPF has measured it; every write is guarded so this settles instead of
        /// re-triggering layout.</summary>
        private void UpdateFit()
        {
            try
            {
                // Minions-only with no pool renders nothing at all - that is the mode's real
                // behaviour, so show an empty box rather than a stale last frame.
                if (!_panel.IsVisible)
                {
                    if (Changed(_viewport.Height, MinViewH)) _viewport.Height = MinViewH;
                    return;
                }

                var b = _panel.Bounds;
                if (b.Width <= 0 || b.Height <= 0) return;

                double s = Math.Min(1.0, Math.Min(_viewW / b.Width, MaxViewH / b.Height));
                // NOT the pixel-sized Changed(): a scale lives in 0..1, so a half-unit threshold would
                // treat every real change as no change and the panel would never shrink at all.
                if (Math.Abs(_fit.ScaleX - s) > 0.002) { _fit.ScaleX = s; _fit.ScaleY = s; }

                double wantH = Clamp(b.Height * s, MinViewH, MaxViewH);
                if (Changed(_viewport.Height, wantH)) _viewport.Height = wantH;

                // Centre the PANEL in the box - not the stage. The panel sits wherever its own
                // placement puts it on the 1920x1080 stage (an unarranged one centres itself there), so
                // the stage has to be offset by that position, scaled, or the preview frames empty
                // canvas next to the panel instead of the panel.
                double ox = (_viewW - b.Width * s) / 2.0 - b.X * s;
                double oy = (wantH - b.Height * s) / 2.0 - b.Y * s;
                if (Changed(Canvas.GetLeft(_stage), ox)) Canvas.SetLeft(_stage, ox);
                if (Changed(Canvas.GetTop(_stage), oy)) Canvas.SetTop(_stage, oy);
            }
            catch { }
        }

        /// <summary>Has this layout value actually moved? Unset Canvas.Left/Top read back as NaN, and
        /// EVERY comparison against NaN is false - so a plain "difference > epsilon" guard silently
        /// skips the FIRST write and the stage never gets positioned at all.</summary>
        private static bool Changed(double current, double wanted) =>
            double.IsNaN(current) || Math.Abs(current - wanted) > 0.5;

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
