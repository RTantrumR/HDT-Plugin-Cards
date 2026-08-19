using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hearthstone_Deck_Tracker;                     // Core (the canvas is API.Core, qualified inline)
using Hearthstone_Deck_Tracker.Utility.Extensions;  // OverlayExtensions
using HsbgCardLookup.Config;
using HsbgCardLookup.Update;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Small magnifying-glass button on HDT's overlay canvas, docked just left of the game's own
    /// card-list book in the bottom-right corner during a BG match. Clicking it toggles the search
    /// overlay — a mouse path for players who don't use (or haven't bound) the summon hotkey.
    /// Also owns the small "Update available" badge that floats just above itself — this is the one
    /// surface visible to players who never open F3 or Settings, so it's the actual fix for updates
    /// otherwise going unnoticed for an entire session (see <see cref="SetUpdateState"/>).
    /// </summary>
    public sealed class SearchButton
    {
        // Reference geometry at 1920×1080, measured from a clean screenshot: the game's book pill
        // sits at (1752..1812, 1033..1076) and the gear at (1844..1905, same row) — ~61×43 pills,
        // ~31px apart. Ours forms a third pill left of the book with the same size and spacing.
        // Anchored to the canvas' BOTTOM-RIGHT corner (this corner UI hugs the window corner,
        // unlike the leaderboard column which maps into the centred 16:9 content area).
        // Scaling law = HDT's own (OverlayWindow: LeaderboardTop => Height * 0.15 etc.): HS renders
        // with a fixed vertical world size, so ALL of the game's UI — sizes and corner insets alike —
        // scales with window HEIGHT only. Neither HDT nor HearthMirror can read real UI positions
        // (verified: HearthMirror's API is state-only; HDT's leaderboard "attachment" is exactly this
        // kind of height-fraction constant), so calibrated constants ARE the attachment mechanism.
        private const double RefBtnW = 61, RefBtnH = 43;
        private const double RefRightInset = 199;    // canvas right edge → our right edge (book left − 31 − our width)
        private const double RefBottomInset = 1;     // canvas bottom edge → our bottom edge (4 measured, −3 from live calibration)
        private const double MinScale = 0.60, MaxScale = 2.00;

        // Matches the game's pills: near-black face, worn-metal rim, silvery glyph.
        private static readonly Color FaceColor = Color.FromArgb(0xE6, 0x22, 0x1C, 0x18);
        private static readonly Color FaceHover = Color.FromArgb(0xF0, 0x33, 0x2B, 0x25);
        private static readonly Color RimColor = Color.FromRgb(0x8F, 0x88, 0x7E);
        private static readonly Color RimHover = Color.FromRgb(0xEF, 0xEB, 0xE2);
        private static readonly Color GlyphColor = Color.FromRgb(0xC9, 0xC4, 0xBC);

        // Badge: same near-black face as the button, gold rim so it reads as "notable" against the
        // game's own near-black/worn-metal palette without introducing a foreign color.
        private static readonly Color BadgeFace = Color.FromArgb(0xF0, 0x22, 0x1C, 0x18);
        private static readonly Color BadgeRim = Color.FromRgb(0xE8, 0xB5, 0x4B);
        private static readonly Color BadgeText = Color.FromRgb(0xF2, 0xF5, 0xFA);
        private static readonly Color BadgeCloseIdle = Color.FromRgb(0x8F, 0x88, 0x7E);
        private static readonly Color BadgeCloseHover = Color.FromRgb(0xEF, 0xEB, 0xE2);

        private readonly PluginConfig _config;
        private readonly Action _toggle;             // toggles the search overlay (wired by Plugin)
        private readonly Action<string> _onSkip;      // badge's ✕ — skip this update version
        private readonly Action<string> _log;

        private Border _root;
        private Path _glyph;
        private bool _attached;
        private DateTime _lastPoll = DateTime.MinValue;
        private string _lastSig;
        private string _lastLayoutLog;               // dedupe: log geometry only when it changes

        private Border _badge;
        private TextBlock _badgeText;
        private Border _badgeClose;
        private UpdateNotice _updateNotice;
        private double? _updateProgress;              // non-null while a download is in flight

        public SearchButton(PluginConfig config, Action toggle, Action<string> onSkip, Action<string> log = null)
        {
            _config = config;
            _toggle = toggle;
            _onSkip = onSkip;
            _log = log;
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

        /// <summary>Pushed by Plugin whenever the known update state changes (background check, manual
        /// check, or download progress) — the one source of truth shared with the F3 banner and the
        /// Settings "Updates" page. <paramref name="progress"/> non-null means a download is in
        /// flight (0..1, or -1 if the server didn't report a size); null means not downloading.</summary>
        internal void SetUpdateState(UpdateNotice notice, double? progress)
        {
            try
            {
                Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    _updateNotice = notice;
                    _updateProgress = progress;
                    RefreshBadge();
                }));
            }
            catch { }
        }

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
            RefreshBadge();
        }

        private void HideIfShown()
        {
            if (_attached && _root != null) _root.Visibility = Visibility.Collapsed;
            if (_badge != null) _badge.Visibility = Visibility.Collapsed;
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
                    if (_badge != null) canvas.Children.Remove(_badge);
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

        private void BuildBadge()
        {
            _badgeText = new TextBlock
            {
                Foreground = new SolidColorBrush(BadgeText),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var closeGlyph = new TextBlock
            {
                Text = "✕", Foreground = new SolidColorBrush(BadgeCloseIdle),
                VerticalAlignment = VerticalAlignment.Center
            };
            _badgeClose = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Child = closeGlyph
            };
            _badgeClose.MouseEnter += (s, e) => closeGlyph.Foreground = new SolidColorBrush(BadgeCloseHover);
            _badgeClose.MouseLeave += (s, e) => closeGlyph.Foreground = new SolidColorBrush(BadgeCloseIdle);
            _badgeClose.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try { if (_updateNotice != null) _onSkip?.Invoke(_updateNotice.AvailableVersion); } catch { }
            };
            DockPanel.SetDock(_badgeClose, Dock.Right);

            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(_badgeClose);
            row.Children.Add(_badgeText);

            _badge = new Border
            {
                Background = new SolidColorBrush(BadgeFace),
                BorderBrush = new SolidColorBrush(BadgeRim),
                Cursor = Cursors.Hand,
                Child = row,
                Visibility = Visibility.Collapsed
            };
            _badge.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // _badgeClose's own handler already ran + stopped bubbling for its clicks
                try { _toggle?.Invoke(); } catch { }
            };

            var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
            canvas?.Children.Add(_badge);
            try { OverlayExtensions.SetIsOverlayHitTestVisible(_badge, true); } catch { }
        }

        // Whether there's currently something worth showing the badge for — a plain "up to date" /
        // error notice stays silent (that's what the F3 banner and Settings page are for).
        private bool HasNoteworthyUpdate() =>
            _updateProgress.HasValue || (_updateNotice != null && (_updateNotice.AvailableForDownload || _updateNotice.RestartReady));

        private void RefreshBadge()
        {
            bool show = _attached && _root != null && _root.Visibility == Visibility.Visible && HasNoteworthyUpdate();
            if (show && _badge == null) BuildBadge();
            if (_badge == null) return;

            if (!show) { _badge.Visibility = Visibility.Collapsed; return; }

            bool downloading = _updateProgress.HasValue;
            bool restartReady = !downloading && _updateNotice.RestartReady;

            if (downloading)
            {
                var f = _updateProgress.Value;
                _badgeText.Text = f >= 0 ? $"Updating… {(int)Math.Round(Math.Max(0, Math.Min(1, f)) * 100)}%" : "Updating…";
            }
            else if (restartReady) _badgeText.Text = "Restart to update";
            else _badgeText.Text = "Update available";

            // Skipping only makes sense while it's still an offer — not mid-download or once staged.
            _badgeClose.Visibility = (!downloading && !restartReady) ? Visibility.Visible : Visibility.Collapsed;

            _badge.Visibility = Visibility.Visible;
            Layout();   // re-measure — the badge's own width depends on its text
        }

        private void Layout()
        {
            var canvas = Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
            if (canvas == null || _root == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // Height-based scale only (see the constants' comment) — the game never scales by width.
            double scale = Math.Max(MinScale, Math.Min(MaxScale, ch / 1080.0));

            double w = RefBtnW * scale, h = RefBtnH * scale;
            _root.Width = w;
            _root.Height = h;
            _root.CornerRadius = new CornerRadius(h * 0.40);        // the game's pills are flatter than a stadium
            _root.BorderThickness = new Thickness(Math.Max(1.0, 2.2 * scale));
            _glyph.Width = _glyph.Height = h * 0.52;

            double left = cw - RefRightInset * scale - w;
            double top = ch - RefBottomInset * scale - h;
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);

            if (_badge != null && _badge.Visibility == Visibility.Visible)
            {
                // Wide enough for "Restart to update" at this scale's font size; centered over the
                // button, floating just above it with a small gap.
                double bh = Math.Max(20, h * 0.62);
                double bw = Math.Max(150 * scale, w * 2.6);   // fits "Restart to update", the longest state text
                double bLeft = left + (w - bw) / 2;
                double bTop = top - bh - (5 * scale);

                _badge.Width = bw;
                _badge.Height = bh;
                _badge.CornerRadius = new CornerRadius(bh * 0.32);
                _badge.BorderThickness = new Thickness(Math.Max(1.0, 1.6 * scale));
                _badge.Padding = new Thickness(8 * scale, 0, 4 * scale, 0);
                _badgeText.FontSize = Math.Max(10, 12 * scale);
                if (_badgeClose.Child is TextBlock ct) ct.FontSize = Math.Max(10, 12 * scale);
                Canvas.SetLeft(_badge, bLeft);
                Canvas.SetTop(_badge, bTop);
            }

            // One line per geometry change — the ground truth for calibrating against screenshots.
            string sig = $"SearchButton layout: canvas {cw:F0}x{ch:F0}, scale {scale:F3}, rect ({left:F0},{top:F0},{w:F0}x{h:F0})";
            if (sig != _lastLayoutLog) { _lastLayoutLog = sig; _log?.Invoke(sig); }
        }
    }
}
