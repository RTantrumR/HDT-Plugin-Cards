using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hearthstone_Deck_Tracker;                     // Core (the canvas is API.Core, qualified inline)
using Hearthstone_Deck_Tracker.Utility.Extensions;  // OverlayExtensions
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Small magnifying-glass button on HDT's overlay canvas, docked just left of the game's own
    /// card-list book in the bottom-right corner during a BG match. Clicking it toggles the search
    /// overlay — a mouse path for players who don't use (or haven't bound) the summon hotkey.
    /// </summary>
    public sealed class SearchButton
    {
        // Reference geometry at 1920×1080, measured from a clean screenshot: the game's book pill
        // sits at (1752..1812, 1033..1076) and the gear at (1844..1905, same row) — ~61×43 pills,
        // ~31px apart. Ours forms a third pill left of the book with the same size and spacing.
        // Anchored to the canvas' BOTTOM-RIGHT corner (this corner UI hugs the window corner,
        // unlike the leaderboard column which maps into the centred 16:9 content area).
        private const double RefBtnW = 61, RefBtnH = 43;
        private const double RefRightInset = 199;    // canvas right edge → our right edge (book left − 31 − our width)
        private const double RefBottomInset = 4;     // canvas bottom edge → our bottom edge (matches the pills' row)
        private const double MinScale = 0.60, MaxScale = 2.00;

        // Matches the game's pills: near-black face, worn-metal rim, silvery glyph.
        private static readonly Color FaceColor = Color.FromArgb(0xE6, 0x22, 0x1C, 0x18);
        private static readonly Color FaceHover = Color.FromArgb(0xF0, 0x33, 0x2B, 0x25);
        private static readonly Color RimColor = Color.FromRgb(0x8F, 0x88, 0x7E);
        private static readonly Color RimHover = Color.FromRgb(0xEF, 0xEB, 0xE2);
        private static readonly Color GlyphColor = Color.FromRgb(0xC9, 0xC4, 0xBC);

        private readonly PluginConfig _config;
        private readonly Action _toggle;             // toggles the search overlay (wired by Plugin)

        private Border _root;
        private Path _glyph;
        private bool _attached;
        private DateTime _lastPoll = DateTime.MinValue;
        private string _lastSig;

        public SearchButton(PluginConfig config, Action toggle)
        {
            _config = config;
            _toggle = toggle;
        }

        // OnUpdate thread (~100ms), throttled; pure read → marshal to the canvas thread on change.
        public void Poll()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPoll).TotalMilliseconds < 500) return;
            _lastPoll = now;

            bool show = false;
            try { show = _config.ShowSearchButton && Core.Game != null && Core.Game.IsBattlegroundsMatch; }
            catch { }

            string sig = show ? "1" : "0";
            if (sig == _lastSig) return;
            _lastSig = sig;

            try { Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher?.BeginInvoke(new Action(() => Apply(show))); }
            catch { }
        }

        /// <summary>Settings changed — drop the signature so the next poll re-applies.</summary>
        public void OnSettingsChanged() => _lastSig = null;

        // ── Canvas thread from here down ─────────────────────────────────────────────────────────

        private void Apply(bool show)
        {
            if (!show) { HideIfShown(); return; }
            var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
            if (canvas == null) return;
            if (!_attached)
            {
                Build();
                canvas.Children.Add(_root);
                canvas.SizeChanged += OnCanvasSizeChanged;
                _attached = true;
            }
            _root.Visibility = Visibility.Visible;
            Layout();
        }

        private void HideIfShown()
        {
            if (_attached && _root != null) _root.Visibility = Visibility.Collapsed;
        }

        public void CloseAll()
        {
            try
            {
                Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher?.Invoke(new Action(() =>
                {
                    var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
                    if (canvas == null || !_attached) return;
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                    _attached = false;
                }));
            }
            catch { /* HDT may be tearing down */ }
        }

        private void OnCanvasSizeChanged(object s, SizeChangedEventArgs e) => Layout();

        private void Build()
        {
            _glyph = new Path
            {
                // Magnifying glass: a circle (two arcs) + a handle stroke toward bottom-right.
                Data = Geometry.Parse("M17,10.5 A6.5,6.5 0 1 1 4,10.5 A6.5,6.5 0 1 1 17,10.5 M15.3,15.3 L21,21"),
                Stroke = new SolidColorBrush(GlyphColor),
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _root = new Border
            {
                Background = new SolidColorBrush(FaceColor),
                BorderBrush = new SolidColorBrush(RimColor),
                Child = _glyph,
                Cursor = Cursors.Hand,
                ToolTip = "Card search"
            };
            _root.MouseEnter += (s, e) =>
            {
                _root.Background = new SolidColorBrush(FaceHover);
                _root.BorderBrush = new SolidColorBrush(RimHover);
                _glyph.Stroke = new SolidColorBrush(RimHover);
            };
            _root.MouseLeave += (s, e) =>
            {
                _root.Background = new SolidColorBrush(FaceColor);
                _root.BorderBrush = new SolidColorBrush(RimColor);
                _glyph.Stroke = new SolidColorBrush(GlyphColor);
            };
            _root.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try { _toggle?.Invoke(); } catch { }
            };

            // Register with HDT's hover loop so the overlay window becomes clickable over the button.
            try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
        }

        private void Layout()
        {
            var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
            if (canvas == null || _root == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double scale = Math.Min(cw / 1920.0, ch / 1080.0);
            scale = Math.Max(MinScale, Math.Min(MaxScale, scale));

            double w = RefBtnW * scale, h = RefBtnH * scale;
            _root.Width = w;
            _root.Height = h;
            _root.CornerRadius = new CornerRadius(h / 2);           // stadium shape, like the game's pills
            _root.BorderThickness = new Thickness(Math.Max(1.0, 1.6 * scale));
            _glyph.Width = _glyph.Height = h * 0.52;

            Canvas.SetLeft(_root, cw - RefRightInset * scale - w);
            Canvas.SetTop(_root, ch - RefBottomInset * scale - h);
        }
    }
}
