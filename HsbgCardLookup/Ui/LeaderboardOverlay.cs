using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Hearthstone_Deck_Tracker.API;   // Core.OverlayCanvas
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Per-portrait MMR labels for the BG leaderboard, drawn inside HDT's own overlay canvas
    /// (<c>Core.OverlayCanvas</c>). Solo and duos layouts (<see cref="IsDuos"/> picks the slot
    /// arrays). Reference geometry adapted from HDT-BGMMRPlugin (MIT) — see NOTICE (repo root).
    /// </summary>
    public sealed class LeaderboardOverlay
    {
        public sealed class Row
        {
            public string Name;
            public string HeroName;     // hero card name — the panel shows it when player names are hidden
            public int Place;           // leaderboard place; in duos both teammates share one
            public int Rating;          // 0 = below the leaderboard cutoff (shown as 8000↓)
            public bool RatingPending;  // leaderboard blob not loaded yet → show "…" instead of 8000↓
            public int Delta;
            public int TavernTier;      // 1..7; 0 = unknown (icon hidden)
            public bool IsDead;
            public bool IsLastOpponent;
            public bool IsCurrentOpponent;
        }

        private const int MaxSlots = 8;

        // Reference geometry at a 1920×1080 game area, from HDT-BGMMRPlugin (see NOTICE).
        private const double RefW = 1920.0, RefH = 1080.0;
        private static readonly double[] RefSlotLeft = { 255.00, 252.14, 249.29, 246.43, 243.57, 240.71, 237.86, 235.00 };
        private static readonly double[] RefSlotTop = { 168.0, 260.0, 355.0, 445.0, 540.0, 633.0, 727.0, 822.0 };
        // Duos: each team is ONE 156px block holding two glued 78px player cells; a player's health
        // bar straddles the split between them. Solved 2026-08-23 (round 4) on a full-screen 1080p
        // capture, from nine mutually-consistent landmarks — the blue/red block frames' top and
        // bottom borders plus the mid-block health bars — which all fit: block tops 165/360/555/750
        // (team pitch 195), cell = block + {0, 78}. Earlier rounds were wrong in the PITCH as well
        // as the offset (192.5/80, inherited from the reference plugin's older-UI tables), so every
        // shift-only fix still drifted the labels down onto each portrait's lower edge.
        // Values below = cellTop + 13, i.e. after the layout's bottom-anchoring (−RefLabelUp) each
        // label lands 8px inside its own portrait's TOP — clearing the block's frame border on the
        // upper cell and the health bar on the lower one.
        private static readonly double[] DuosRefSlotLeft = { 245.0, 245.0, 242.0, 242.0, 239.0, 239.0, 236.0, 236.0 };
        private static readonly double[] DuosRefSlotTop = { 178.0, 256.0, 373.0, 451.0, 568.0, 646.0, 763.0, 841.0 };
        // Label grew 5px UPWARD from the original 28px (bottom edge stays put on the portrait) to fit
        // a larger, readable font. Without names it's a single line, so it shrinks to RefLabelH1 —
        // still bottom-anchored, so the rating sits where it always did.
        private const double RefLabelW = 90.0, RefLabelH = 33.0, RefLabelUp = 5.0;
        private const double RefLabelH1 = 20.0;
        // …and then sits 10px higher still (user-tuned) — a lone rating line reads better a touch
        // above where the two-line label's bottom edge was.
        private const double RefNoNameLift = 10.0;
        private const double RefOpponentShift = 30.0;
        private const double RefTierH = 35.0;

        private readonly Border[] _labels = new Border[MaxSlots];
        private readonly TextBlock[] _names = new TextBlock[MaxSlots];
        private readonly TextBlock[] _ratings = new TextBlock[MaxSlots];
        private readonly TextBlock[] _arrows = new TextBlock[MaxSlots];
        private readonly Image[] _tiers = new Image[MaxSlots];
        private readonly TextBlock[] _lastOpp = new TextBlock[MaxSlots];
        private readonly bool[] _shifted = new bool[MaxSlots];

        private bool _attached;

        /// <summary>Per-part content switches (set from config before <see cref="SetStandings"/>).
        /// The label box renders only the enabled lines (and hides entirely when names, rating AND
        /// deltas are all off — e.g. the "tavern tiers only" setup); a single-line label shrinks to
        /// <see cref="RefLabelH1"/> and lifts <see cref="RefNoNameLift"/>.</summary>
        public bool ShowNames { get; set; }
        public bool ShowRating { get; set; } = true;
        public bool ShowDeltas { get; set; } = true;
        public bool ShowTiers { get; set; } = true;
        public bool ShowLastOpp { get; set; } = true;
        public bool DimDead { get; set; } = true;
        /// <summary>Duos layout: teamed slot geometry (rows arrive team-ordered from BgMmr).</summary>
        public bool IsDuos { get; set; }

        private bool TwoLines => ShowNames && (ShowRating || ShowDeltas);
        private bool BoxVisible => ShowNames || ShowRating || ShowDeltas;

        private static readonly Brush LabelBg = Frozen(Color.FromArgb(0xDC, 0x0A, 0x0D, 0x14));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB4));
        private static readonly Brush Dead = Frozen(Color.FromRgb(0x91, 0x91, 0x91));
        private static readonly Brush Up = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Down = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly Brush Swords = Frozen(Color.FromRgb(0xF5, 0xC4, 0x51));

        public LeaderboardOverlay()
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                var name = new TextBlock
                {
                    Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                var rating = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var arrow = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
                var line2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                line2.Children.Add(rating); line2.Children.Add(arrow);
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(name); stack.Children.Add(line2);
                var border = new Border
                {
                    Background = LabelBg,
                    Child = stack,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                    Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.75 }
                };
                var tier = new Image
                {
                    Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed
                };
                RenderOptions.SetBitmapScalingMode(tier, BitmapScalingMode.HighQuality);
                var swords = new TextBlock
                {
                    Text = "⚔", Foreground = Swords, FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                    Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 }
                };
                _names[i] = name; _ratings[i] = rating; _arrows[i] = arrow;
                _labels[i] = border; _tiers[i] = tier; _lastOpp[i] = swords;
            }
        }

        /// <summary>Add our elements to HDT's overlay canvas. Canvas (UI) thread only.</summary>
        public void Attach()
        {
            if (_attached) return;
            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            for (int i = 0; i < MaxSlots; i++)
            {
                canvas.Children.Add(_labels[i]);
                canvas.Children.Add(_tiers[i]);
                canvas.Children.Add(_lastOpp[i]);
            }
            canvas.SizeChanged += OnCanvasSizeChanged;
            _attached = true;
        }

        /// <summary>Remove our elements from HDT's overlay canvas. Canvas (UI) thread only.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            try
            {
                var canvas = Core.OverlayCanvas;
                if (canvas == null) return;
                canvas.SizeChanged -= OnCanvasSizeChanged;
                for (int i = 0; i < MaxSlots; i++)
                {
                    canvas.Children.Remove(_labels[i]);
                    canvas.Children.Remove(_tiers[i]);
                    canvas.Children.Remove(_lastOpp[i]);
                }
            }
            catch { /* HDT may already be tearing its overlay down */ }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayout();

        /// <summary>Fill + show labels for the current standings (row 0 = 1st place). Canvas thread.
        /// Empty/null hides everything.</summary>
        public void SetStandings(IReadOnlyList<Row> rows)
        {
            if (!_attached) Attach();
            int n = rows?.Count ?? 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (i >= n) { HideSlot(i); continue; }
                var r = rows[i];
                _shifted[i] = r.IsCurrentOpponent;

                bool dim = DimDead && r.IsDead;
                _names[i].Text = r.Name;
                _names[i].Foreground = dim ? Dead : Brushes.White;
                _names[i].Visibility = ShowNames ? Visibility.Visible : Visibility.Collapsed;
                _ratings[i].Text = r.RatingPending ? "…" : (r.Rating > 0 ? r.Rating.ToString() : "8000↓");
                _ratings[i].Foreground = dim ? Dead : (!r.RatingPending && r.Rating > 0 ? UiKit.AccentBrush : Muted);
                _ratings[i].Visibility = ShowRating ? Visibility.Visible : Visibility.Collapsed;
                if (ShowDeltas && r.Delta != 0 && !dim)
                {
                    _arrows[i].Text = (r.Delta > 0 ? "▲" : "▼") + Math.Abs(r.Delta);
                    _arrows[i].Foreground = r.Delta > 0 ? Up : Down;
                    _arrows[i].Visibility = Visibility.Visible;
                }
                else _arrows[i].Visibility = Visibility.Collapsed;

                _labels[i].Opacity = dim ? 0.65 : 1.0;
                _labels[i].Visibility = BoxVisible ? Visibility.Visible : Visibility.Collapsed;

                if (ShowTiers && r.TavernTier >= 1 && r.TavernTier <= 7)
                {
                    var icon = TierIcon(r.TavernTier);
                    if (icon != null)
                    {
                        _tiers[i].Source = icon;
                        _tiers[i].Opacity = dim ? 0.65 : 1.0;
                        _tiers[i].Visibility = Visibility.Visible;
                    }
                    else _tiers[i].Visibility = Visibility.Collapsed;
                }
                else _tiers[i].Visibility = Visibility.Collapsed;

                _lastOpp[i].Visibility = ShowLastOpp && r.IsLastOpponent ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateLayout();
        }

        public void HideAll()
        {
            for (int i = 0; i < MaxSlots; i++) HideSlot(i);
        }

        private void HideSlot(int i)
        {
            _shifted[i] = false;
            _labels[i].Visibility = Visibility.Collapsed;
            _tiers[i].Visibility = Visibility.Collapsed;
            _lastOpp[i].Visibility = Visibility.Collapsed;
        }

        private void UpdateLayout()
        {
            var canvas = Core.OverlayCanvas;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double contentW = Math.Min(cw, ch * (16.0 / 9.0));
            double contentH = Math.Min(ch, contentW * (9.0 / 16.0));
            double contentLeft = (cw - contentW) / 2.0;
            double contentTop = (ch - contentH) / 2.0;
            double scale = Math.Max(0.60, Math.Min(contentH / RefH, 2.00));

            double refH = TwoLines ? RefLabelH : RefLabelH1;
            double lift = TwoLines ? 0.0 : RefNoNameLift;

            for (int i = 0; i < MaxSlots; i++)
            {
                var b = _labels[i];
                b.Width = RefLabelW * scale;
                b.Height = refH * scale;
                b.CornerRadius = new CornerRadius(4.0 * scale);
                b.Padding = new Thickness(2.0 * scale, 0, 2.0 * scale, 0);
                _names[i].FontSize = 11.5 * scale;
                _ratings[i].FontSize = 11.5 * scale;
                _arrows[i].FontSize = 9.5 * scale;

                double refSlotLeft = IsDuos ? DuosRefSlotLeft[i] : RefSlotLeft[i];
                double refSlotTop = IsDuos ? DuosRefSlotTop[i] : RefSlotTop[i];
                double refLeft = refSlotLeft + (_shifted[i] ? RefOpponentShift : 0.0);
                double left = contentLeft + (refLeft / RefW) * contentW;
                // Bottom-anchored: a shorter (name-less) label keeps the same bottom edge, then lifts.
                double top = contentTop + ((refSlotTop - RefLabelUp + (RefLabelH - refH) - lift) / RefH) * contentH;
                Canvas.SetLeft(b, left);
                Canvas.SetTop(b, top);

                var t = _tiers[i];
                t.Height = RefTierH * scale;
                double tierLeft = left + RefLabelW * scale;
                double tierTop = top + (refH - RefTierH) * 0.5 * scale;
                Canvas.SetLeft(t, tierLeft);
                Canvas.SetTop(t, tierTop);

                var s = _lastOpp[i];
                s.FontSize = 16.0 * scale;
                Canvas.SetLeft(s, tierLeft + 4.0 * scale);
                Canvas.SetTop(s, tierTop + RefTierH * scale);
            }
        }

        private static ImageSource TierIcon(int tier)
        {
            try { return ImageCache.Load(CardStore.TierIconPath(tier), 64); }
            catch { return null; }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
