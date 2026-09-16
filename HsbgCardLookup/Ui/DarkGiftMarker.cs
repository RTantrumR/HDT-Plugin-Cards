using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.Utility.Extensions;  // OverlayExtensions

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// The small round "?" marker that sits just above the game's Dark Discovery button and opens the
    /// Dark Gift panel on click. It replaces the old hover trigger: the panel now appears only when
    /// the player asks for it, so reading the button's own tooltip no longer drags a big panel across
    /// the board.
    ///
    /// Like <see cref="SearchButton"/> it is a child of HDT's overlay canvas registered with
    /// <c>OverlayExtensions.SetIsOverlayHitTestVisible</c>, so HDT's hover loop drops
    /// <c>WS_EX_TRANSPARENT</c> while the cursor is on it and the click lands here — the overlay
    /// window stays <c>WS_EX_NOACTIVATE</c>, so Hearthstone never loses foreground.
    ///
    /// Placement follows the same law as every other world-anchored surface here (leaderboard labels,
    /// MMR chart): reference pixels measured at 1920x1080, mapped into the canvas' centred 16:9
    /// content box. Neither HDT nor HearthMirror can read real UI positions, so calibrated constants
    /// ARE the attachment mechanism — hence the one-line-per-change layout log, which is the ground
    /// truth for calibrating against a screenshot.
    ///
    /// While the panel is open the marker is HIDDEN, not layered on top of it: the panel opens pinned
    /// to the top-right corner and covers this spot, and a "?" floating over the panel that explains
    /// the gifts reads like a smudge on it. Closing is then the panel's own ✕, a click outside it, or
    /// the press of the real button.
    /// </summary>
    public sealed class DarkGiftMarker
    {
        // Calibrated LIVE off a DESKTOP capture (2026-09-16) — a Hearthstone-internal screenshot does
        // not contain HDT's overlay, so the marker cannot be measured in one. With marker and gem in
        // the same frame no crop offset has to be guessed: the marker's 34px face equals its 34 DIP,
        // so that capture is 1:1 with canvas coordinates, and the gem's axis — the centre of its
        // "uses left" badge ring, confirmed by the centre of its two side clamps — sat 16px left of
        // where the logged rect had put the marker. Hence 1609. The badge's top edge is y ~= 251, so
        // the marker's bottom clears it by ~10px.
        private const double RefW = 1920, RefH = 1080;
        private const double RefCx = 1609, RefCy = 222, RefD = 38;
        private const double MinScale = 0.60, MaxScale = 2.00;

        // The game's own pills are near-black with a worn-metal rim (SearchButton matches them).
        private static readonly Color FaceColor = Color.FromArgb(0xE6, 0x22, 0x1C, 0x18);
        private static readonly Color FaceHover = Color.FromArgb(0xF0, 0x33, 0x2B, 0x25);
        private static readonly Color RimColor = Color.FromRgb(0x8F, 0x88, 0x7E);
        private static readonly Color RimHover = Color.FromRgb(0xEF, 0xEB, 0xE2);
        private static readonly Color GlyphColor = Color.FromRgb(0xC9, 0xC4, 0xBC);

        private readonly Action<string> _log;
        private Border _root;
        private TextBlock _glyph;
        private bool _attached, _hover, _dim;
        private string _lastLayoutLog;

        /// <summary>Raised on the canvas thread when the marker is clicked.</summary>
        public event Action Clicked;

        private sealed class Box { public double L, T, R, B; }
        private volatile Box _box;

        public DarkGiftMarker(Action<string> log = null) { _log = log; }

        /// <summary>True while the cursor is over the marker. Cached screen box (device px) rather
        /// than MouseEnter/MouseLeave, for the same reason the panel caches one: HDT re-enables
        /// click-through the moment the cursor leaves, so a Leave event may never arrive. Read from
        /// the OnUpdate thread and from the panel's mouse hook.</summary>
        public bool IsUnderMouse
        {
            get
            {
                var b = _box;
                if (b == null) return false;
                try
                {
                    GetCursorPos(out POINT p);
                    return p.X >= b.L && p.X <= b.R && p.Y >= b.T && p.Y <= b.B;
                }
                catch { return false; }
            }
        }

        /// <summary>Show or hide the marker (attaching it to the canvas on first use). Canvas thread.</summary>
        public void SetVisible(bool visible)
        {
            if (!visible) { if (_root != null) { _root.Visibility = Visibility.Collapsed; _box = null; } return; }
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

        /// <summary>Dimmed = there IS a Dark Discovery button, but the panel would come up empty in the
        /// current display mode (minions-only before the guaranteed-type rule is live, say). The marker
        /// stays where it is instead of vanishing — its spot should be predictable — but it is faded
        /// and does not respond to a click, since opening nothing is worse than not opening. Canvas
        /// thread.</summary>
        public void SetDimmed(bool dim)
        {
            if (_dim == dim) return;
            _dim = dim;
            if (_root != null)
            {
                _root.Opacity = dim ? 0.45 : 1.0;
                _root.Cursor = dim ? Cursors.Arrow : Cursors.Hand;
                _root.ToolTip = dim ? "Dark Gifts — nothing to show yet" : "Dark Gifts";
            }
            ApplyColors();
        }

        /// <summary>Remove the marker from the canvas (plugin unload). Canvas thread.</summary>
        public void Close()
        {
            _box = null;
            try
            {
                var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
                if (canvas == null || !_attached) return;
                canvas.SizeChanged -= OnCanvasSizeChanged;
                canvas.Children.Remove(_root);
            }
            catch { /* HDT may be tearing down */ }
            _attached = false;
        }

        private void OnCanvasSizeChanged(object s, SizeChangedEventArgs e) => Layout();

        private void Build()
        {
            _glyph = new TextBlock
            {
                Text = "?",
                Foreground = new SolidColorBrush(GlyphColor),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _root = new Border
            {
                Background = new SolidColorBrush(FaceColor),
                BorderBrush = new SolidColorBrush(RimColor),
                Child = _glyph,
                Cursor = Cursors.Hand,
                ToolTip = "Dark Gifts"
            };
            _root.MouseEnter += (s, e) => { _hover = true; ApplyColors(); };
            _root.MouseLeave += (s, e) => { _hover = false; ApplyColors(); };
            _root.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_dim) return;
                try { Clicked?.Invoke(); } catch { }
            };

            try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
        }

        private void ApplyColors()
        {
            if (_root == null) return;
            bool hot = _hover && !_dim;   // a dimmed marker does not light up under the cursor either
            _root.Background = new SolidColorBrush(hot ? FaceHover : FaceColor);
            _root.BorderBrush = new SolidColorBrush(hot ? RimHover : RimColor);
            _glyph.Foreground = new SolidColorBrush(hot ? RimHover : GlyphColor);
        }

        private void Layout()
        {
            var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
            if (canvas == null || _root == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // The board is world UI: it lives in the centred 16:9 content box, not against the window
            // corners (that mapping is the one the leaderboard labels are live-calibrated on).
            double contentW = Math.Min(cw, ch * (RefW / RefH));
            double contentH = Math.Min(ch, contentW * (RefH / RefW));
            double contentLeft = (cw - contentW) / 2.0;
            double contentTop = (ch - contentH) / 2.0;
            double scale = Math.Max(MinScale, Math.Min(MaxScale, contentH / RefH));

            double d = RefD * scale;
            _root.Width = _root.Height = d;
            _root.CornerRadius = new CornerRadius(d / 2);
            _root.BorderThickness = new Thickness(Math.Max(1.0, 2.0 * scale));
            _glyph.FontSize = Math.Max(9, d * 0.56);

            double left = contentLeft + (RefCx / RefW) * contentW - d / 2;
            double top = contentTop + (RefCy / RefH) * contentH - d / 2;
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);

            _root.Dispatcher.BeginInvoke(new Action(UpdateBox), DispatcherPriority.Loaded);

            string sig = $"DarkGiftMarker layout: canvas {cw:F0}x{ch:F0}, scale {scale:F3}, rect ({left:F0},{top:F0},{d:F0}x{d:F0})";
            if (sig != _lastLayoutLog) { _lastLayoutLog = sig; _log?.Invoke(sig); }
        }

        private void UpdateBox()
        {
            try
            {
                if (_root.Visibility != Visibility.Visible || _root.ActualWidth <= 0) { _box = null; return; }
                var tl = _root.PointToScreen(new Point(0, 0));
                var br = _root.PointToScreen(new Point(_root.ActualWidth, _root.ActualHeight));
                _box = new Box { L = tl.X, T = tl.Y, R = br.X, B = br.Y };
            }
            catch { _box = null; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
