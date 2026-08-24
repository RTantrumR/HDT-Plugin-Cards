using System;
using System.Collections.Generic;
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
    /// A live preview of the trinket / anomaly HUD for the settings pages, in two views of the SAME
    /// thing: real <see cref="HudCanvasCard"/> instances laid out on a canvas sized to the game.
    ///
    ///   * the strip - each box at its true in-game size, so "how big will this be" has a real answer;
    ///   * the map   - the whole game screen shrunk to fit, each box where it actually sits, so the
    ///                 arrangement can be checked without starting a match.
    ///
    /// The cards are the shipped class, not a mock-up, so the default stacking, the width clamps and
    /// the saved placements all come out exactly as they will in game. A hosted card registers no
    /// overlay hit-testing, wires no gestures and never writes geometry - positioning stays Arrange's
    /// job.
    /// </summary>
    internal sealed class HudPreview
    {
        private const double MapMinH = 120;
        private const double StripGap = 10;

        private readonly PluginConfig _config;
        private readonly CardStore _store;
        private readonly bool _isAnomaly;

        private readonly Canvas _stage;
        private readonly StackPanel _root;
        private readonly StackPanel _strip;
        private readonly List<Slot> _slots = new List<Slot>();

        public FrameworkElement Root => _root;

        private sealed class Slot
        {
            public HudCanvasCard Card;
            public Func<HudPlacement> Placement;
            public Image TrueSize;      // the 1:1 copy in the strip (same bitmap, no second decode)
        }

        public HudPreview(PluginConfig config, CardStore store, bool isAnomaly, double viewW)
        {
            _config = config;
            _store = store;
            _isAnomaly = isAnomaly;

            PreviewStage.ResolveSize(out double cw, out double ch);

            _stage = new Canvas { Width = cw, Height = ch, IsHitTestVisible = false };

            var mapFrame = new Border
            {
                Width = viewW,
                Height = Math.Max(MapMinH, viewW * ch / Math.Max(1, cw)),
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x18, 0x1F, 0x2C), Color.FromRgb(0x0D, 0x11, 0x19),
                    new Point(0.35, 0), new Point(0.65, 1)),
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = _stage }
            };

            _strip = new StackPanel { Orientation = Orientation.Horizontal };

            _root = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
            _root.Children.Add(Caption("Actual size"));
            _root.Children.Add(new ScrollViewer
            {
                Content = _strip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, StripGap)
            });
            _root.Children.Add(Caption("On screen"));
            _root.Children.Add(mapFrame);

            BuildSlots();

            // A card's on-canvas size is only known once WPF has measured the stage, and it changes
            // again whenever a placement does - so mirror it into the strip after every layout pass.
            _stage.LayoutUpdated += (s, e) => SyncTrueSizes();

            Refresh();
        }

        private void BuildSlots()
        {
            if (_isAnomaly)
            {
                AddSlot(0, () => _config.AnomalyHud, Game.BgHud.SampleAnomaly(_store), "Anomaly");
                return;
            }
            var placements = new Func<HudPlacement>[]
            {
                () => _config.LesserTrinketHud, () => _config.GreaterTrinketHud,
                () => _config.Trinket3Hud,      () => _config.Trinket4Hud,
            };
            for (int i = 0; i < placements.Length; i++)
                AddSlot(i, placements[i], Game.BgHud.SampleTrinket(_store, greater: i == 1),
                        Game.BgHud.TrinketLabels[i]);
        }

        private void AddSlot(int index, Func<HudPlacement> placement, BgCard sample, string label)
        {
            var card = new HudCanvasCard(index, _isAnomaly, _stage);
            var shot = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.HighQuality);

            var cell = new StackPanel { Margin = new Thickness(0, 0, StripGap, 0) };
            cell.Children.Add(shot);
            cell.Children.Add(new TextBlock
            {
                Text = label, Foreground = UiKit.TextMuted, FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            });
            _strip.Children.Add(cell);

            var slot = new Slot { Card = card, Placement = placement, TrueSize = shot };
            _slots.Add(slot);
            LoadArt(slot, sample);
        }

        // Art may not be cached yet (the HUD normally appears mid-match, not from browsing), so take
        // whatever is on disk and upgrade when a download lands.
        private void LoadArt(Slot slot, BgCard sample)
        {
            if (sample == null) return;
            BitmapSource bmp = null;
            try { bmp = CardArt.GetSync(sample, false, 0); } catch { }
            if (bmp != null) { SetArt(slot, bmp); return; }
            try
            {
                CardArt.LoadAsync(sample, false, 0).ContinueWith(t =>
                {
                    var b = t.Result; if (b == null) return;
                    _root.Dispatcher.BeginInvoke(new Action(() => SetArt(slot, b)));
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            catch { }
        }

        private void SetArt(Slot slot, BitmapSource bmp)
        {
            try
            {
                slot.Card.SetArt(bmp);
                slot.TrueSize.Source = bmp;
            }
            catch { }
        }

        /// <summary>Re-read the saved placements and re-render. Called on every settings change.</summary>
        public void Refresh()
        {
            try
            {
                foreach (var s in _slots)
                {
                    var p = s.Placement();
                    double xf = p != null ? p.XF : 0, yf = p != null ? p.YF : 0, wf = p != null ? p.WF : 0;
                    // A placement saved before the HUD moved onto the overlay canvas is still in screen
                    // DIPs. The live HUD converts it on first show; do the same read-only conversion
                    // here (never saved) so the preview shows the box where the game will put it. It
                    // needs the real overlay canvas, and without one it reports failure and the slot
                    // falls back to its default spot - which is also what the game would do.
                    if (wf <= 0 && p != null && p.Set && p.W > 0)
                        HudCanvasCard.LegacyToFrac(p.X, p.Y, p.W, out xf, out yf, out wf);
                    s.Card.ShowAt(xf, yf, wf);
                }

                // A switched-off feature doesn't blank the preview - it still answers "what would this
                // look like" - but it shouldn't read as live either.
                bool on = _isAnomaly ? _config.ShowAnomaly : _config.ShowTrinkets;
                _root.Opacity = on ? 1.0 : 0.45;
            }
            catch { }
        }

        public void Close()
        {
            foreach (var s in _slots) { try { s.Card.Close(); } catch { } }
            _slots.Clear();
        }

        // Mirror each card's real on-canvas size into its 1:1 copy. Guarded, so this settles instead
        // of re-triggering layout forever.
        private void SyncTrueSizes()
        {
            try
            {
                foreach (var s in _slots)
                {
                    double w = s.Card.LayoutWidth, h = s.Card.LayoutHeight;
                    if (double.IsNaN(w) || w <= 0 || double.IsNaN(h) || h <= 0) continue;
                    if (double.IsNaN(s.TrueSize.Width) || Math.Abs(s.TrueSize.Width - w) > 0.5) s.TrueSize.Width = w;
                    if (double.IsNaN(s.TrueSize.Height) || Math.Abs(s.TrueSize.Height - h) > 0.5) s.TrueSize.Height = h;
                }
            }
            catch { }
        }

        private static TextBlock Caption(string text) => new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = UiKit.TextMuted, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        };
    }
}
