using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json;
using Hearthstone_Deck_Tracker.API;   // Core.OverlayCanvas
using HsbgCardLookup.Net;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// DEBUG mock of the planned on-portrait MMR chart (config flag <c>DebugMmrGraph</c>, no settings
    /// UI): shown via the REAL trigger — while a leaderboard portrait is focused (HearthMirror
    /// <c>GetBattlegroundsLeaderboardHoveredEntityId</c>, the hover that opens the game's expanded
    /// opponent view) — at the real anchor: the area right of that expanded panel's top. Data is a
    /// REAL player's (the busiest today in <c>bgmmr/{REGION}-history.json</c>, e.g. DoGBiscuit)
    /// regardless of whose portrait is focused. Exists so the in-game look can be screenshotted
    /// without an 8k+ lobby. Same geometry mapping as <see cref="LeaderboardOverlay"/>; driven by
    /// <c>IPlugin.OnUpdate</c> → <see cref="Poll"/> like the other watchers.
    /// </summary>
    public sealed class MmrChartOverlay
    {
        // 1920×1080 reference: right of the expanded opponent panel (~x 380-710), in the strip
        // ABOVE the hero-power/quest/trinket card row — measured across both layout screenshots,
        // the cards never reach above ~y 320, so y 190-310 stays clear of them in every observed
        // case. True dynamic placement would need either a scry of the game's UI transforms
        // (fragile) or a probe-verified read of the opponent's displayed extras — deferred.
        private const double RefX = 745, RefY = 150;   // raised to keep the BIGGER card above ~y 320 (card row)
        private const double RefPad = 10, RefChartW = 260, RefChartH = 88;
        private const int LingerMs = 350;   // bridge hover flicker; the game panel closes on unhover too

        private readonly Border _panel;
        private readonly TextBlock _name;
        private readonly TextBlock _rating;
        private readonly TextBlock _net;
        private readonly TextBlock _foot;
        private readonly Canvas _chart;
        private readonly DispatcherTimer _timer;

        private List<int> _series = new List<int>();   // [day open] + each recorded rating
        private bool _attached;
        private bool _hasData;

        // Hover gate (OnUpdate thread writes, UI thread reads via marshalled flag).
        private DateTime _lastPoll = DateTime.MinValue;
        private DateTime _lastHover = DateTime.MinValue;
        private volatile bool _shown;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xDC, 0x0A, 0x0D, 0x14));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB4));
        private static readonly Brush Up = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Down = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly Brush Axis = Frozen(Color.FromArgb(0x60, 0x9A, 0xA3, 0xB4));

        public MmrChartOverlay()
        {
            _name = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            _rating = new TextBlock { Foreground = UiKit.AccentBrush, FontWeight = FontWeights.Bold };
            _net = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 0, 1) };
            var line2 = new StackPanel { Orientation = Orientation.Horizontal };
            line2.Children.Add(_rating);
            line2.Children.Add(_net);
            _chart = new Canvas { ClipToBounds = true };
            _foot = new TextBlock { Foreground = Muted };

            var stack = new StackPanel();
            stack.Children.Add(_name);
            stack.Children.Add(line2);
            stack.Children.Add(_chart);
            stack.Children.Add(_foot);

            _panel = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                Child = stack,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.7 }
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _timer.Tick += (s, e) => FetchAsync();
        }

        public void Attach()
        {
            if (_attached) return;
            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            canvas.Children.Add(_panel);
            canvas.SizeChanged += OnCanvasSizeChanged;
            _attached = true;
            _timer.Start();
            FetchAsync();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _timer.Stop();
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas == null) return;
                canvas.SizeChanged -= OnCanvasSizeChanged;
                canvas.Children.Remove(_panel);
            }
            catch { }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

        /// <summary>OnUpdate thread (~100ms): show the card only while a leaderboard portrait is
        /// focused — the same hover that opens the game's expanded opponent view.</summary>
        public void Poll()
        {
            try
            {
                if (!_attached) return;
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < 100) return;
                _lastPoll = now;

                int hid = -1;
                try { var h = HearthMirror.Reflection.Client?.GetBattlegroundsLeaderboardHoveredEntityId(); hid = h ?? -1; }
                catch { }
                if (hid > 0) _lastHover = now;

                bool show = (now - _lastHover).TotalMilliseconds < LingerMs;
                if (show == _shown) return;
                _shown = show;
                _panel.Dispatcher.BeginInvoke(new Action(ApplyVisibility));
            }
            catch { /* OnUpdate must never throw */ }
        }

        private void ApplyVisibility()
        {
            bool visible = _shown && _hasData;
            _panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) Layout();
        }

        private void FetchAsync()
        {
            string region = Game.BgMmr.CurrentRegion() ?? "EU";
            Task.Run(async () =>
            {
                string json = await AssetClient.GetStringAsync(AssetClient.SiteBase + "/bgmmr/" + region + "-history.json");
                Blob blob = null;
                if (!string.IsNullOrEmpty(json))
                    try { blob = JsonConvert.DeserializeObject<Blob>(json); } catch { }
                var b = blob;
                _panel.Dispatcher.BeginInvoke(new Action(() => Apply(b)));
            });
        }

        private void Apply(Blob blob)
        {
            var players = blob?.Players;
            if (players == null || players.Count == 0) { _hasData = false; ApplyVisibility(); return; }

            // The busiest player today — the mock exists to show a chart with real steps on it.
            P p = players
                .OrderByDescending(x => x.Today?.Count ?? 0)
                .ThenByDescending(x => x.Rating)
                .First();

            _series = new List<int> { p.TodayOpen };
            if (p.Today != null) _series.AddRange(p.Today.Select(t => t.R));

            int now = _series[_series.Count - 1];
            int net = now - p.TodayOpen;
            _name.Text = p.Name;
            _rating.Text = now.ToString();
            _net.Text = net == 0 ? "" : (net > 0 ? "▲" : "▼") + Math.Abs(net);
            _net.Foreground = net >= 0 ? Up : Down;
            int games = _series.Count - 1;
            _foot.Text = games == 0 ? "no games yet today" : "today · " + games + (games == 1 ? " game" : " games");

            _hasData = true;
            ApplyVisibility();
        }

        // Same centered-16:9 mapping as LeaderboardOverlay.
        private void Layout()
        {
            var canvas = Core.OverlayCanvas;
            if (canvas == null || _panel.Visibility != Visibility.Visible) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double contentW = Math.Min(cw, ch * (16.0 / 9.0));
            double contentH = Math.Min(ch, contentW * (9.0 / 16.0));
            double contentLeft = (cw - contentW) / 2.0;
            double contentTop = (ch - contentH) / 2.0;
            double scale = Math.Max(0.60, Math.Min(contentH / 1080.0, 2.00));

            Canvas.SetLeft(_panel, contentLeft + (RefX / 1920.0) * contentW);
            Canvas.SetTop(_panel, contentTop + (RefY / 1080.0) * contentH);
            _panel.CornerRadius = new CornerRadius(8 * scale);
            _panel.Padding = new Thickness(RefPad * scale, 7 * scale, RefPad * scale, 6 * scale);
            _name.FontSize = 15 * scale;
            _rating.FontSize = 18.5 * scale;
            _net.FontSize = 13.5 * scale;
            _foot.FontSize = 11.5 * scale;
            _chart.Width = RefChartW * scale;
            _chart.Height = RefChartH * scale;
            _chart.Margin = new Thickness(0, 4 * scale, 0, 3 * scale);

            Redraw(scale);
        }

        private void Redraw(double scale)
        {
            _chart.Children.Clear();
            var pts = _series;
            double W = _chart.Width, H = _chart.Height;
            if (pts.Count < 2)
            {
                var tb = new TextBlock { Text = "—", Foreground = Muted, FontSize = 11 * scale };
                Canvas.SetLeft(tb, W / 2 - 4);
                Canvas.SetTop(tb, H / 2 - 8);
                _chart.Children.Add(tb);
                return;
            }

            int min = pts.Min(), max = pts.Max();
            if (max == min) max = min + 1;
            double padL = 34 * scale, padY = 6 * scale;
            double w = W - padL - 2 * scale, h = H - 2 * padY;
            Func<int, double> Y = r => padY + (max - r) / (double)(max - min) * h;
            Func<int, double> X = i => padL + i / (double)(pts.Count - 1) * w;

            _chart.Children.Add(new Line { X1 = padL, Y1 = Y(min), X2 = W, Y2 = Y(min), Stroke = Axis, StrokeThickness = 1 });
            _chart.Children.Add(new Line { X1 = padL, Y1 = Y(max), X2 = W, Y2 = Y(max), Stroke = Axis, StrokeThickness = 1 });
            _chart.Children.Add(AxisLabel(max.ToString(), Y(max), scale));
            _chart.Children.Add(AxisLabel(min.ToString(), Y(min), scale));

            for (int i = 1; i < pts.Count; i++)
            {
                int d = pts[i] - pts[i - 1];
                _chart.Children.Add(new Line
                {
                    X1 = X(i - 1), Y1 = Y(pts[i - 1]), X2 = X(i), Y2 = Y(pts[i]),
                    Stroke = d > 0 ? Up : d < 0 ? Down : Muted, StrokeThickness = 2.2 * scale
                });
            }
            for (int i = 0; i < pts.Count; i++)
            {
                double r = 2.3 * scale;
                var dot = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = Brushes.White };
                Canvas.SetLeft(dot, X(i) - r);
                Canvas.SetTop(dot, Y(pts[i]) - r);
                _chart.Children.Add(dot);
            }
        }

        private TextBlock AxisLabel(string text, double y, double scale)
        {
            var tb = new TextBlock { Text = text, Foreground = Muted, FontSize = 11 * scale };
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, y - 7.5 * scale);
            return tb;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private sealed class Blob
        {
            [JsonProperty("players")] public List<P> Players { get; set; }
        }
        private sealed class P
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("rating")] public int Rating { get; set; }
            [JsonProperty("todayOpen")] public int TodayOpen { get; set; }
            [JsonProperty("today")] public List<Pt> Today { get; set; }
        }
        private sealed class Pt
        {
            [JsonProperty("t")] public string T { get; set; }
            [JsonProperty("r")] public int R { get; set; }
        }
    }
}
