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
        // bar straddles the split between them. Values below = cellTop + 13 on the LOWER cell of each
        // team (odd indices) and cellTop + 9 on the UPPER one (even indices) — so after the layout's
        // bottom-anchoring (−RefLabelUp) the lower label lands 8px inside its cell's top and the
        // upper one 4px higher still. The pair is deliberately NOT symmetric (user direction): the
        // upper cell's top edge is the block's outer frame border, which is thicker than the internal
        // split below it, so an equal numeric inset reads as the top label sitting lower.
        //
        // Round 5 (2026-08-24) — block tops 162.6 / 356.1 / 549.6 / 743.1, TEAM PITCH 193.5.
        // Round 4 shipped pitch 195, which drifted the labels ~2px lower per team — barely visible on
        // team 1, ~7px onto the portrait by team 4. Method, and why it beats rounds 1-4:
        //   * The measurement no longer trusts ANY assumed coordinate. Our own 8 plates render at
        //     known ref positions and measured exactly 90px wide (= RefLabelW), which proves the
        //     capture is 1:1 with the 1080p reference AND solves the crop origin: ref_y = crop_y +
        //     118.6, consistent to 1px across all 8. The +30 offset that showed up on plates 0-1 is
        //     the current-opponent shift, an independent check that the fit is real.
        //   * Pitch was fitted BEFORE the offset (the rule rounds 1-3 broke), by matched-filtering
        //     three independent landmark families down the column — health bar, block top border,
        //     block bottom border — rather than eyeballing one edge. All three agree: 193.5 / 192.5 /
        //     195.0, mean 193.6. The health bar is the strongest signal (correlation 0.65-1.00) and
        //     gives 193.5 on its own.
        //   * Team 1 is EXCLUDED from the fit: it was the current opponent, whose tile the game draws
        //     enlarged, and it sits +2px off the line the other three define. Fitting it in would bake
        //     a transient distortion into the permanent geometry.
        // Health-bar residuals against the shipped numbers: +0.5 / -1.0 / +0.5 on teams 2-4.
        private static readonly double[] DuosRefSlotLeft = { 245.0, 245.0, 242.0, 242.0, 239.0, 239.0, 236.0, 236.0 };
        private static readonly double[] DuosRefSlotTop = { 171.6, 253.6, 365.1, 447.1, 558.6, 640.6, 752.1, 834.1 };
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

        // A delta arrow only renders when that player actually moved today (and isn't dimmed), so
        // ShowDeltas being ON does NOT mean a second line exists: with the rating hidden and a lobby
        // full of players below the leaderboard cutoff, every delta is 0 and the reserved line stays
        // empty — the name then floats centred in a box sized for content that never arrives.
        // _anyDelta records whether the last fill actually put an arrow on screen, so the box is only
        // two lines tall when there really are two. Set from SetStandings, read here.
        private bool _anyDelta;

        private bool TwoLines => ShowNames && (ShowRating || _anyDelta);
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
            // Recomputed over the whole fill, then used by TwoLines: one height for all 8 plates, so
            // the column can't go ragged just because a single player happens to have moved today.
            _anyDelta = false;
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
                    _anyDelta = true;
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
                // Width stays fixed for every slot on purpose — a column of equal-width plates reads
                // better than one that ragged-edges itself to each battletag. Only the HEIGHT follows
                // the content (see TwoLines).
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
