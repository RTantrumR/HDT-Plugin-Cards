using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.API;                 // Core.OverlayCanvas
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A live, 1:1 preview of the opponents'-MMR side panel, for the settings page.
    ///
    /// It renders the REAL <see cref="MmrSidePanel"/> — not a mock-up of one — into a canvas sized to
    /// the game's own logical size, so the panel's layout maths (width fraction → scale → every font
    /// size) produce exactly the pixels it will produce in game. The panel is then centred in the box
    /// at true size, which is what makes it possible to judge legibility on a 470px-wide window.
    ///
    /// The backdrop is a plain gradient of our own. Capturing a real Hearthstone frame was built and
    /// then removed: it attached awkwardly, and since the panel is centred rather than shown at its
    /// true screen position, the frame behind it was never the region it would actually sit on anyway.
    /// </summary>
    internal sealed class MmrPanelPreview
    {
        private const double FallbackW = 1920, FallbackH = 1080;
        private const double FramePad = 14;                    // breathing room around the panel
        private const double MinViewH = 120, MaxViewH = 360;   // the page has a ScrollViewer, but stay sane

        private readonly PluginConfig _config;
        private readonly double _viewW, _viewH;

        private readonly Border _viewport;
        private readonly Canvas _clip;
        private readonly Canvas _stage;
        private readonly MmrSidePanel _panel;

        private double _cw, _ch;
        private bool _duos;

        public FrameworkElement Root => _viewport;

        public MmrPanelPreview(PluginConfig config, double viewW, double viewH)
        {
            _config = config;
            _viewW = viewW;
            _viewH = viewH;

            ResolveStageSize();

            _stage = new Canvas { Width = _cw, Height = _ch, IsHitTestVisible = false };

            _clip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            _clip.Children.Add(_stage);
            // The panel sizes to its content, so re-frame whenever anything about it changes.
            _clip.LayoutUpdated += (s, e) => UpdateCrop();

            _viewport = new Border
            {
                Width = _viewW,
                Height = _viewH,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                // Subtle on purpose: this now spans the whole box, where it used to be a narrow crop of
                // a screen-wide gradient and so read as almost flat. A strong ramp here competes with
                // the panel, which is the thing being judged.
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x18, 0x1F, 0x2C), Color.FromRgb(0x0D, 0x11, 0x19),
                    new Point(0.35, 0), new Point(0.65, 1)),
                Margin = new Thickness(0, 2, 0, 10),
                Child = _clip
            };

            // A hosted panel: no overlay hit-testing, no drag gestures, and no GeometryChanged — it
            // cannot write geometry back to config. Positioning stays arrange mode's job.
            _panel = new MmrSidePanel(_stage);

            Refresh();
        }

        /// <summary>Preview the solo or the duos shape. Duos pairs rows into teams that share one
        /// place number, so it is a genuinely different layout worth seeing before a duos match.</summary>
        public void SetDuos(bool duos)
        {
            if (_duos == duos) return;
            _duos = duos;
            Refresh();
        }

        /// <summary>Re-read the content settings and re-render. Called on every settings change.</summary>
        public void Refresh()
        {
            try
            {
                var hud = _config.MmrPanelHud;
                double wf = hud != null && hud.WF > 0 ? hud.WF : 0;
                double xf = hud != null && hud.WF > 0 ? hud.XF : 0.005;
                double yf = hud != null && hud.WF > 0 ? hud.YF : 0.30;
                _panel.Place(xf, yf, wf);

                _panel.NameMode = _config.OpponentNameMode;
                _panel.ShowRating = _config.ShowMmrRating;
                _panel.ShowDeltas = _config.ShowMmrDeltas;
                _panel.ShowTiers = Game.BgMmr.TiersInPanelFor(_config);
                _panel.DimDead = _config.DimDeadPlayers;
                _panel.IsDuos = _duos;

                _panel.SetStandings(MmrSidePanel.SampleRows(_duos));

                // The panel surface being switched off doesn't blank the preview — it still answers
                // "what would it look like" — but it shouldn't read as live either.
                _viewport.Opacity = _config.ShowMmrPanel ? 1.0 : 0.45;

                // Re-frame after the panel has laid out, so its real size is known.
                _viewport.Dispatcher.BeginInvoke(new Action(UpdateCrop),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        public void Close()
        {
            try { _panel.Close(); } catch { }
        }

        /// <summary>The stage must match the LOGICAL size of the live overlay canvas, because that is
        /// what the panel scales against. HDT's canvas is the exact coordinate space when it is up;
        /// otherwise the game's client rect in DIPs (never raw pixels — at 125% scaling a pixel-sized
        /// stage would render every font a quarter too small relative to the game).</summary>
        private void ResolveStageSize()
        {
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas != null && canvas.ActualWidth > 0 && canvas.ActualHeight > 0)
                {
                    _cw = canvas.ActualWidth; _ch = canvas.ActualHeight;
                    return;
                }
            }
            catch { }
            try
            {
                var r = Hearthstone_Deck_Tracker.User32.GetHearthstoneRect(true);   // true = DIPs
                if (r.Width > 0 && r.Height > 0) { _cw = r.Width; _ch = r.Height; return; }
            }
            catch { }
            _cw = FallbackW; _ch = FallbackH;
        }

        /// <summary>
        /// Frame the panel: grow the viewport to whatever the panel needs (duos is a good deal taller
        /// than solo, and the scale is the user's) and centre it.
        ///
        /// The crop is deliberately NOT clamped to the canvas. Clamping is what stops a panel parked
        /// against the screen edge — the default placement — from ever being centred; letting the crop
        /// run past the edge keeps the panel in the middle of the box whatever its in-game position.
        ///
        /// Driven from LayoutUpdated because the panel's size is content-dependent and only known after
        /// WPF measures it; every write is guarded so this settles instead of re-triggering layout.
        /// </summary>
        private void UpdateCrop()
        {
            try
            {
                var b = _panel.Bounds;
                double px = b.Width > 0 ? b.X : _cw * 0.005;
                double py = b.Height > 0 ? b.Y : _ch * 0.30;
                double pw = b.Width > 0 ? b.Width : 210;
                double ph = b.Height > 0 ? b.Height : 200;

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
        /// EVERY comparison against NaN is false — so a plain "difference > epsilon" guard silently
        /// skips the FIRST write and the panel never gets positioned at all.</summary>
        private static bool Changed(double current, double wanted) =>
            double.IsNaN(current) || Math.Abs(current - wanted) > 0.5;

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
