using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthstone_Deck_Tracker.API;                 // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility;             // ScreenCapture
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A live, 1:1 preview of the opponents'-MMR side panel, for the settings page.
    ///
    /// It renders the REAL <see cref="MmrSidePanel"/> — not a mock-up of one — into a canvas sized to
    /// the game's own logical size, so the panel's layout maths (width fraction → scale → every font
    /// size) produce exactly the pixels it will produce in game. The viewport then shows a crop around
    /// wherever the panel is currently placed, at true size, which is the only way to judge legibility
    /// on a 470px-wide settings window.
    ///
    /// The backdrop is a real Hearthstone frame when the game is running, so the panel is judged
    /// against the contrast it will actually sit on; otherwise a neutral gradient.
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
        private readonly Canvas _bgLayer;   // backdrop, kept covering the viewport
        private readonly Canvas _stage;     // the panel, centred
        private readonly Image _backdrop;
        private readonly System.Windows.Shapes.Rectangle _gradient;
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

            _backdrop = new Image
            {
                Width = _cw, Height = _ch, Stretch = Stretch.Fill,
                IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.HighQuality);

            _gradient = new System.Windows.Shapes.Rectangle
            {
                Width = _cw, Height = _ch, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(
                    Color.FromRgb(0x23, 0x2C, 0x3C), Color.FromRgb(0x0A, 0x0D, 0x14),
                    new Point(0.3, 0), new Point(0.7, 1))
            };

            // Two layers, offset independently. The panel is centred in the box for looks, which means
            // its crop runs past the canvas edge whenever it sits near one (the default placement does).
            // If the backdrop shared that offset it would run out and leave a bare strip, so it gets its
            // own offset that keeps it covering the viewport. The backdrop is context, not a map of
            // where the panel sits — arrange mode is what shows that.
            _bgLayer = new Canvas { Width = _cw, Height = _ch, IsHitTestVisible = false };
            _bgLayer.Children.Add(_gradient);
            _bgLayer.Children.Add(_backdrop);

            _stage = new Canvas { Width = _cw, Height = _ch, IsHitTestVisible = false };

            _clip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            _clip.Children.Add(_bgLayer);
            _clip.Children.Add(_stage);
            // The panel sizes to its content, so re-frame whenever anything about it changes.
            _clip.LayoutUpdated += (s, e) => UpdateCrop();

            _viewport = new Border
            {
                Width = _viewW, Height = _viewH,
                BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), ClipToBounds = true,
                Background = UiKit.Br(UiKit.RowBg),
                Margin = new Thickness(0, 2, 0, 10),
                Child = _clip
            };

            // A hosted panel: no overlay hit-testing, no drag gestures, and no GeometryChanged — it
            // cannot write geometry back to config. Positioning stays arrange mode's job.
            _panel = new MmrSidePanel(_stage);

            Refresh();
            LoadBackdropAsync();
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
                _panel.ShowTiers = BgMmrTiersInPanel();
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

        // Resolve the tier-location axis through the same rule the live panel uses.
        private bool BgMmrTiersInPanel() => Game.BgMmr.TiersInPanelFor(_config);

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
        /// The cost is that the backdrop can run out before the viewport does, which the viewport's own
        /// background covers.
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
                if (Math.Abs(_viewport.Height - wantH) > 0.5) _viewport.Height = wantH;

                double viewH = wantH;
                double cropX = px + pw / 2.0 - _viewW / 2.0;
                double cropY = py + ph / 2.0 - viewH / 2.0;
                if (Math.Abs(Canvas.GetLeft(_stage) + cropX) > 0.5) Canvas.SetLeft(_stage, -cropX);
                if (Math.Abs(Canvas.GetTop(_stage) + cropY) > 0.5) Canvas.SetTop(_stage, -cropY);

                // Same view, clamped inside the canvas so the backdrop never runs out.
                double bgX = Clamp(cropX, 0, Math.Max(0, _cw - _viewW));
                double bgY = Clamp(cropY, 0, Math.Max(0, _ch - viewH));
                if (Math.Abs(Canvas.GetLeft(_bgLayer) + bgX) > 0.5) Canvas.SetLeft(_bgLayer, -bgX);
                if (Math.Abs(Canvas.GetTop(_bgLayer) + bgY) > 0.5) Canvas.SetTop(_bgLayer, -bgY);
            }
            catch { }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // ── Backdrop ────────────────────────────────────────────────────────────────────────────

        private static BitmapSource _cachedFrame;   // last good capture, reused across page opens

        private void LoadBackdropAsync()
        {
            if (_cachedFrame != null) { ApplyBackdrop(_cachedFrame); return; }
            var dispatcher = _viewport.Dispatcher;
            Task.Run(() =>
            {
                var bmp = CaptureGameFrame();
                if (bmp == null) return;
                try { dispatcher.BeginInvoke(new Action(() => { _cachedFrame = bmp; ApplyBackdrop(bmp); })); }
                catch { }
            });
        }

        private void ApplyBackdrop(BitmapSource src)
        {
            _backdrop.Source = src;
            _backdrop.Visibility = Visibility.Visible;
            _gradient.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Grab the game's client area. Uses HDT's own capture helper with its ALTERNATIVE path, i.e.
        /// PrintWindow(PW_RENDERFULLCONTENT), because that works on an occluded window — the ordinary
        /// screen-copy path would capture whatever is on top, which here is our own topmost settings
        /// window. Returns null (→ gradient) whenever anything is off, including a frame that came back
        /// effectively blank: a black rectangle presented as the game is worse than an honest backdrop.
        /// </summary>
        private static BitmapSource CaptureGameFrame()
        {
            try
            {
                var hs = Hearthstone_Deck_Tracker.User32.GetHearthstoneWindow();
                if (hs == IntPtr.Zero) return null;
                var rect = Hearthstone_Deck_Tracker.User32.GetHearthstoneRect(false);
                if (rect.Width <= 0 || rect.Height <= 0) return null;   // minimized: Clone would throw

                using (var bmp = ScreenCapture.CaptureWindow(hs, new System.Drawing.Point(0, 0)))
                {
                    if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;
                    if (IsEffectivelyBlank(bmp)) return null;
                    return ToBitmapSource(bmp);
                }
            }
            catch { return null; }
        }

        // GPU-composited clients often hand PrintWindow a black or empty surface. Sample a grid rather
        // than trusting the call succeeded.
        private static bool IsEffectivelyBlank(System.Drawing.Bitmap bmp)
        {
            try
            {
                int first = -1;
                for (int gx = 1; gx <= 7; gx++)
                {
                    for (int gy = 1; gy <= 7; gy++)
                    {
                        int x = bmp.Width * gx / 8, y = bmp.Height * gy / 8;
                        int argb = bmp.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                        if (first == -1) first = argb;
                        else if (Math.Abs(argb - first) > 0x040404) return false;   // real variation
                    }
                }
                return true;   // every sample the same → nothing was actually rendered
            }
            catch { return true; }
        }

        // Encode to PNG and decode as WPF. Slower than CreateBitmapSourceFromHBitmap, but that one
        // leaks the HBITMAP unless it is explicitly DeleteObject'd — not worth the risk for a
        // once-per-page-open operation that already runs off the UI thread.
        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
        }
    }
}
