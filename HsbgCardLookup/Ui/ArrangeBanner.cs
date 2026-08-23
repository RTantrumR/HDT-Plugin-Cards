using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A strip across the top of the game while a feature is being positioned: what is being
    /// arranged, how to move it, and a Done button.
    ///
    /// It exists because arrange now deliberately puts Hearthstone in front, and HDT hides its whole
    /// overlay the instant the game loses focus. Without a Done button ON the overlay the only way to
    /// finish is to click back into the settings window — which takes focus away from Hearthstone and
    /// makes everything you were just positioning disappear as you reach for it.
    ///
    /// Clicking here costs Hearthstone nothing: HDT's overlay window stays WS_EX_NOACTIVATE, and
    /// registering with <c>SetIsOverlayHitTestVisible</c> only drops WS_EX_TRANSPARENT while the
    /// cursor is over this element. That is the same mechanism the in-game search button already
    /// ships with.
    /// </summary>
    internal sealed class ArrangeBanner
    {
        private const double RefW = 1920;   // the reference width every other placement is tuned at

        private readonly Action _onDone;

        private Border _root;
        private TextBlock _title;
        private TextBlock _hint;
        private TextBlock _doneLabel;
        private Border _done;
        private StackPanel _stack;
        private bool _attached;

        public ArrangeBanner(Action onDone)
        {
            _onDone = onDone;
        }

        /// <summary>Show the banner for a feature, or hide it when the session ends.</summary>
        public void Show(ArrangeTarget target)
        {
            try
            {
                Core.OverlayCanvas?.Dispatcher?.BeginInvoke(new Action(() => Apply(target)));
            }
            catch { }
        }

        public void Close()
        {
            try
            {
                Core.OverlayCanvas?.Dispatcher?.Invoke(new Action(() =>
                {
                    var canvas = Core.OverlayCanvas;
                    if (canvas == null || !_attached) return;
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                    _attached = false;
                }));
            }
            catch { /* HDT may already be tearing down */ }
        }

        // ── Canvas thread from here down ─────────────────────────────────────────────────────────

        private void Apply(ArrangeTarget target)
        {
            if (target == ArrangeTarget.None)
            {
                if (_root != null) _root.Visibility = Visibility.Collapsed;
                return;
            }

            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            if (!_attached)
            {
                Build();
                canvas.Children.Add(_root);
                canvas.SizeChanged += OnCanvasSizeChanged;
                _attached = true;
            }

            _title.Text = "Positioning: " + Describe(target);
            _root.Visibility = Visibility.Visible;
            Layout();
        }

        private static string Describe(ArrangeTarget t)
        {
            switch (t)
            {
                case ArrangeTarget.Trinkets: return "trinket boxes";
                case ArrangeTarget.Anomaly: return "anomaly box";
                case ArrangeTarget.MmrPanel: return "opponents' MMR panel";
                default: return "";
            }
        }

        private void OnCanvasSizeChanged(object s, SizeChangedEventArgs e) => Layout();

        private void Build()
        {
            _title = new TextBlock
            {
                Foreground = UiKit.AccentBrush, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _hint = new TextBlock
            {
                Text = "Drag it to move  ·  drag its top-right corner to resize",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB6, 0xC1, 0xD2)),
                VerticalAlignment = VerticalAlignment.Center
            };

            _doneLabel = new TextBlock
            {
                Text = "Done", Foreground = UiKit.AccentBrush, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _done = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x2B, 0xE8, 0xB5, 0x4B)),
                BorderBrush = UiKit.AccentBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Child = _doneLabel
            };
            _done.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try { _onDone?.Invoke(); } catch { }
            };

            _stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _stack.Children.Add(_title);
            _stack.Children.Add(_hint);
            _stack.Children.Add(_done);

            _root = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x0A, 0x0D, 0x14)),
                BorderBrush = UiKit.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Child = _stack,
                Visibility = Visibility.Collapsed
            };

            // Only this element becomes clickable, and only while the cursor is on it — the rest of
            // the overlay stays click-through so the game still receives everything else.
            try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
        }

        private void Layout()
        {
            var canvas = Core.OverlayCanvas;
            if (canvas == null || _root == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // Scale by height like the in-game search button: the game's UI tracks height, and a
            // width-derived scale skews on ultrawide setups.
            double s = Math.Max(0.6, Math.Min(2.0, ch / (RefW * 9.0 / 16.0)));

            _title.FontSize = 15 * s;
            _title.Margin = new Thickness(0, 0, 14 * s, 0);
            _hint.FontSize = 12.5 * s;
            _hint.Margin = new Thickness(0, 0, 16 * s, 0);
            _doneLabel.FontSize = 13.5 * s;
            _done.Padding = new Thickness(16 * s, 5 * s, 16 * s, 5 * s);
            _root.Padding = new Thickness(16 * s, 8 * s, 16 * s, 8 * s);

            _root.UpdateLayout();
            double w = _root.ActualWidth;
            Canvas.SetLeft(_root, Math.Max(0, (cw - w) / 2.0));
            Canvas.SetTop(_root, 18 * s);
        }
    }
}
