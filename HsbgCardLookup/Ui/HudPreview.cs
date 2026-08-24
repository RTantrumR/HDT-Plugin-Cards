using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A live preview of the trinket / anomaly HUD for the settings pages: ONE box, at exactly the
    /// size it will have in game, so "how big is this going to be" has a real answer before a match.
    ///
    /// One example rather than every box, and no map of where they sit: Arrange mode already shows the
    /// whole set on the real screen, which is a better answer to "where" than a thumbnail ever is.
    ///
    /// The card is a real <see cref="HudCanvasCard"/>, not a mock-up, laid out on a canvas sized to the
    /// game - so the saved placement, the default stacking and the width clamps all resolve exactly as
    /// they will in play, and the viewport simply crops to it. A hosted card registers no overlay
    /// hit-testing, wires no gestures and never writes geometry back to config.
    /// </summary>
    internal sealed class HudPreview
    {
        private const double FramePad = 14;                    // breathing room around the card
        private const double MinViewH = 120, MaxViewH = 340;

        private readonly PluginConfig _config;
        private readonly bool _isAnomaly;
        private readonly double _viewW;

        private readonly Canvas _stage;
        private readonly Border _viewport;
        private readonly HudCanvasCard _card;
        private readonly Func<HudPlacement> _placement;

        public FrameworkElement Root => _viewport;

        public HudPreview(PluginConfig config, CardStore store, bool isAnomaly, double viewW)
        {
            _config = config;
            _isAnomaly = isAnomaly;
            _viewW = viewW;

            PreviewStage.ResolveSize(out double cw, out double ch);

            _stage = new Canvas { Width = cw, Height = ch, IsHitTestVisible = false };

            var clip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            clip.Children.Add(_stage);
            // The card's size follows its saved placement and the canvas, so re-frame after any pass.
            clip.LayoutUpdated += (s, e) => UpdateCrop();

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

            // Box 1 stands in for the set: in the ordinary match it is the one every player has, and
            // the boxes share a default size, so it answers the size question for all of them.
            _placement = isAnomaly ? (Func<HudPlacement>)(() => _config.AnomalyHud)
                                   : () => _config.LesserTrinketHud;
            _card = new HudCanvasCard(0, isAnomaly, _stage);
            LoadArt(isAnomaly ? Game.BgHud.SampleAnomaly(store) : Game.BgHud.SampleTrinket(store, greater: false));

            Refresh();
        }

        // Art may not be cached yet (the HUD normally appears mid-match, not from browsing), so take
        // whatever is on disk and upgrade when a download lands.
        private void LoadArt(BgCard sample)
        {
            if (sample == null) return;
            BitmapSource bmp = null;
            try { bmp = CardArt.GetSync(sample, false, 0); } catch { }
            if (bmp != null) { SetArt(bmp); return; }
            try
            {
                CardArt.LoadAsync(sample, false, 0).ContinueWith(t =>
                {
                    var b = t.Result; if (b == null) return;
                    _viewport.Dispatcher.BeginInvoke(new Action(() => SetArt(b)));
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch { }
        }

        private void SetArt(BitmapSource bmp)
        {
            try { _card.SetArt(bmp); } catch { }
        }

        /// <summary>Re-read the saved placement and re-render. Called on every settings change.</summary>
        public void Refresh()
        {
            try
            {
                var p = _placement();
                double xf = p != null ? p.XF : 0, yf = p != null ? p.YF : 0, wf = p != null ? p.WF : 0;
                // A placement saved before the HUD moved onto the overlay canvas is still in screen
                // DIPs. The live HUD converts it on first show; do the same read-only conversion here
                // (never saved) so the preview shows the size the game will use. It needs the real
                // overlay canvas, and without one it reports failure and the box falls back to its
                // default size - which is also what the game would do.
                if (wf <= 0 && p != null && p.Set && p.W > 0)
                    HudCanvasCard.LegacyToFrac(p.X, p.Y, p.W, out xf, out yf, out wf);
                _card.ShowAt(xf, yf, wf);

                // A switched-off feature doesn't blank the preview - it still answers "what would this
                // look like" - but it shouldn't read as live either.
                bool on = _isAnomaly ? _config.ShowAnomaly : _config.ShowTrinkets;
                _viewport.Opacity = on ? 1.0 : 0.45;

                _viewport.Dispatcher.BeginInvoke(new Action(UpdateCrop),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        public void Close()
        {
            try { _card.Close(); } catch { }
        }

        /// <summary>Grow the viewport to the card and centre it. The crop is deliberately NOT clamped
        /// to the canvas: clamping is what stops a box parked against a screen edge - the default
        /// placement - from ever being centred.</summary>
        private void UpdateCrop()
        {
            try
            {
                var b = _card.Bounds;
                double pw = b.Width > 0 ? b.Width : 170;
                double ph = b.Height > 0 ? b.Height : 238;
                double px = b.Width > 0 ? b.X : 0;
                double py = b.Height > 0 ? b.Y : 0;

                double wantH = Clamp(ph + 2 * FramePad, MinViewH, MaxViewH);
                if (Changed(_viewport.Height, wantH)) _viewport.Height = wantH;

                double cropX = px + pw / 2.0 - _viewW / 2.0;
                double cropY = py + ph / 2.0 - wantH / 2.0;
                if (Changed(Canvas.GetLeft(_stage), -cropX)) Canvas.SetLeft(_stage, -cropX);
                if (Changed(Canvas.GetTop(_stage), -cropY)) Canvas.SetTop(_stage, -cropY);
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
