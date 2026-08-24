using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// The Dark Gift list panel — summoned while the player hovers the in-game Dark Discovery button
    /// (see <see cref="Game.DarkGiftWatcher"/>). It is NOT a window: the whole panel
    /// lives inside HDT's own overlay canvas (<c>Core.OverlayCanvas</c>), registered with
    /// <c>OverlayExtensions.SetIsOverlayHitTestVisible</c> so HDT drops <c>WS_EX_TRANSPARENT</c> while
    /// the cursor is on it — real wheel/right-click reach us while the overlay window stays
    /// <c>WS_EX_NOACTIVATE</c>, so Hearthstone never loses foreground.
    ///
    /// Placement is cursor-anchored and NOT user-movable: the panel lands well left of the hovered
    /// button, so the button stays clickable and its tooltip readable. Moving and scaling it in match
    /// was built and taken back out — the panel is hit-test-visible, so a player who parked it over
    /// the Dark Discovery button would have been unable to press the button at all, since the panel
    /// summons on hovering that very button and would then sit on top of it swallowing the click.
    ///
    /// Rows per the design sketch: a rounded container per gift — name | separator line | effect text —
    /// stacked vertically, each sized to its text. Gifts offerable THIS turn glow (accent border + soft
    /// shadow, bold name); future gifts are dimmed; expired gifts aren't passed in at all. Emphasis
    /// levels recolor a current row: 1 = relevant to the guaranteed most-common-type offer (green),
    /// 2 = unique enabler pairing (purple, reserved for the minion-pool feature).
    ///
    /// While visible, a low-level mouse hook forwards the wheel to the list even when the cursor is on
    /// the game (hovering the button) — so the user can scroll the panel without mousing onto it. The
    /// hook is installed ONLY while visible, never swallows events, and defers to normal WPF wheel
    /// handling when the cursor is over the panel itself.
    /// </summary>
    public sealed class DarkGiftPanel
    {
        private const double ContentWidth = 450;   // gift-list column width (panel width = this + art column)
        // Minion-art grid (left column): full card renders auto-fit into ≤4 columns — 200px wide for a
        // small pool, shrinking (never below ~110) as the pool grows so everything stays visible with
        // no scrolling. Card aspect ≈ the production full renders (404×558).
        private const double ArtMaxW = 200, ArtMinW = 110, ArtGap = 6, ArtMaxH = 560, ArtAspect = 558.0 / 404.0;

        private readonly Border _root;             // the canvas child holding everything
        private readonly TextBlock _headerSub;
        private readonly StackPanel _rows;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _giftColumn;   // header + gift list (collapsed in minions-only mode)
        private readonly StackPanel _artColumn;
        private readonly TextBlock _artCaption;
        private readonly WrapPanel _artWrap;
        private readonly TextBlock _artMore;
        private readonly Canvas _host;             // null = HDT's overlay canvas
        private bool _attached;

        /// <summary>The canvas this panel lives in: HDT's game overlay by default, or a caller's own
        /// canvas for an off-game preview. Held per instance rather than swapped globally, so a preview
        /// can never capture the live in-match panel's attach (and vice versa).</summary>
        private Canvas Host => _host ?? Core.OverlayCanvas;

        /// <summary>The panel's width. Read from <c>_root.Width</c>, not ActualWidth, and that
        /// matters: SetContent assigns the new width and the cursor-anchored placement runs in the SAME
        /// pass, before WPF has measured — ActualWidth would still be the previous content's width and
        /// the panel would land offset by the difference. Height has no explicit value, so it can only
        /// be read back after a layout pass (hence the deferred correction in PlaceForSummon).</summary>
        private double RenderedW =>
            double.IsNaN(_root.Width) ? (_root.ActualWidth > 0 ? _root.ActualWidth : ContentWidth + 22) : _root.Width;
        private double RenderedH => _root.ActualHeight;

        // The panel's on-screen box in DEVICE pixels, refreshed after every placement/layout. Read from
        // the OnUpdate thread (IsUnderMouse) and the mouse hook, so it's swapped as one immutable object
        // instead of maintained by MouseEnter/MouseLeave — those can't be trusted here, since HDT makes
        // the overlay click-through again the moment the cursor leaves us (the leave event may never
        // arrive, which would strand the panel visible forever).
        private sealed class Box { public double L, T, R, B; }
        private volatile Box _box;

        /// <summary>True while the cursor is over the panel (read cross-thread by the watcher so the
        /// panel survives the mouse travelling from the game button onto it).</summary>
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

        /// <summary>Raised on a right-click anywhere on the panel — the watcher cycles the display
        /// mode (gifts+minions / gifts only / minions only). Fired on the UI thread.</summary>
        public event Action ModeCycleRequested;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xEE, 0x10, 0x14, 0x1C));
        private static readonly Brush RowBg = Frozen(Color.FromArgb(0xFF, 0x1A, 0x21, 0x30));
        private static readonly Brush RowBgDim = Frozen(Color.FromArgb(0xFF, 0x14, 0x19, 0x24));
        private static readonly Brush StatBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));    // +X/+Y buffs
        private static readonly Brush KeywordBrush = Frozen(Color.FromRgb(0xE8, 0xB5, 0x4B)); // keywords (accent gold)
        private static readonly Color TribeColor = Color.FromRgb(0x4A, 0xDE, 0x80);   // Emph 1 (green)
        private static readonly Color UniqueColor = Color.FromRgb(0xC0, 0x84, 0xFC);  // Emph 2 (purple)
        private static readonly Brush TribeBrush = Frozen(TribeColor);
        private static readonly Brush UniqueBrush = Frozen(UniqueColor);

        public DarkGiftPanel() : this(null) { }

        /// <param name="host">Render into this canvas instead of HDT's overlay. A hosted panel is a
        /// passive preview: no overlay hit-testing and no mouse hook of its own.</param>
        public DarkGiftPanel(Canvas host)
        {
            _host = host;
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 7) };
            var title = new TextBlock
            {
                Text = "Dark Gifts",
                Foreground = UiKit.AccentBrush,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 10, 0)
            };
            _headerSub = new TextBlock
            {
                Foreground = UiKit.TextMuted,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextWrapping = TextWrapping.Wrap
            };
            header.Children.Add(title);
            header.Children.Add(_headerSub);

            _rows = new StackPanel();
            _scroll = new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 500
            };

            _giftColumn = new StackPanel { Width = ContentWidth };
            _giftColumn.Children.Add(header);
            _giftColumn.Children.Add(_scroll);

            // Left column: the guaranteed-tribe pool as actual card renders (collapsed when absent).
            _artCaption = new TextBlock
            {
                Foreground = UiKit.TextSecondary,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 6),
                TextWrapping = TextWrapping.Wrap
            };
            _artWrap = new WrapPanel();
            _artMore = new TextBlock
            {
                Foreground = UiKit.TextMuted,
                FontSize = 12,
                Margin = new Thickness(2, 1, 2, 0)
            };
            _artColumn = new StackPanel { Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
            _artColumn.Children.Add(_artCaption);
            _artColumn.Children.Add(_artWrap);
            _artColumn.Children.Add(_artMore);

            var outer = new StackPanel { Orientation = Orientation.Horizontal };
            outer.Children.Add(_artColumn);
            outer.Children.Add(_giftColumn);

            _root = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 9),
                Width = ContentWidth + 22,          // content + padding/border; SetContent adjusts
                Visibility = Visibility.Collapsed,
                Child = outer
            };
            try { _root.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = UiKit.ThinScrollBarStyle(); } catch { }

            _root.MouseRightButtonUp += (s, e) => { e.Handled = true; try { ModeCycleRequested?.Invoke(); } catch { } };

            if (_host == null)
            {
                // Ask HDT to treat this element as clickable: its 60Hz hover loop drops WS_EX_TRANSPARENT
                // from the overlay window while the cursor is inside us, so wheel/right-click land here.
                // Meaningless for a preview, which is not in HDT's overlay at all.
                try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
            }
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        /// <summary>Where the panel currently sits in its canvas. Zero-size until it has been laid
        /// out, and at the canvas origin unless a summon has placed it.</summary>
        public Rect Bounds
        {
            get
            {
                double x = Canvas.GetLeft(_root), y = Canvas.GetTop(_root);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                return new Rect(x, y, RenderedW, RenderedH);
            }
        }

        /// <summary>Show the panel (adding it to the canvas on first use). Canvas thread.</summary>
        public void Show()
        {
            if (!Attach()) return;
            _root.Visibility = Visibility.Visible;
            if (_host == null) InstallWheelHook();   // a settings preview must never hook the mouse
            // Keep the list inside the canvas on short screens (the panel is centered vertically).
            // Set on every show, not only a fresh summon: an off-game preview never goes through
            // PlaceForSummon at all, and the cap is what stops a long gift list running off-screen.
            var host = Host;
            if (host != null && host.ActualHeight > 0)
                _scroll.MaxHeight = Math.Min(500, Math.Max(200, host.ActualHeight - 120));
            // Content changes resize the panel, so refresh the hover box after this layout pass too —
            // not only on a fresh summon (a stale box would make the panel vanish under the cursor).
            _root.Dispatcher.BeginInvoke(new Action(UpdateBox), DispatcherPriority.Loaded);
        }

        private bool Attach()
        {
            if (_attached) return true;
            var canvas = Host;
            if (canvas == null) return false;
            canvas.Children.Add(_root);
            _attached = true;
            return true;
        }

        /// <summary>Hide the panel but keep it attached for the next summon. Canvas thread.</summary>
        public void Hide()
        {
            _root.Visibility = Visibility.Collapsed;
            _box = null;
            RemoveWheelHook();
        }

        /// <summary>Remove the panel from its canvas (plugin unload). Canvas thread.</summary>
        public void Close()
        {
            RemoveWheelHook();
            _box = null;
            try { Host?.Children.Remove(_root); }
            catch { /* HDT may be tearing down */ }
            _attached = false;
        }

        /// <summary>Summon placement, in canvas coordinates. The CURSOR is the reference point (it
        /// sits on the hovered button) — the panel's right edge lands ~350px left of it, scaled by the
        /// canvas' 16:9 content width (350 tuned at 1920×1080 → about-centered), so the button and the
        /// game's own tooltip stay clear. Vertical: centered (height isn't known until layout →
        /// corrected right after the next layout pass). Fully clamped inside the canvas.</summary>
        public void PlaceForSummon()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double contentW = Math.Min(cw, ch * (16.0 / 9.0));
            double offset = 350.0 * contentW / 1920.0;

            double w = RenderedW;
            var cursor = new Point(cw / 2, ch / 2);
            try
            {
                GetCursorPos(out POINT c);
                cursor = canvas.PointFromScreen(new Point(c.X, c.Y));
            }
            catch { }

            Canvas.SetLeft(_root, Clamp(cursor.X - offset - w, 0, Math.Max(0, cw - w)));
            Canvas.SetTop(_root, Clamp((ch - 380) / 2, 0, Math.Max(0, ch - 100)));   // estimate; fixed below

            canvas.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    double h = RenderedH;
                    if (h > 0) Canvas.SetTop(_root, Clamp((ch - h) / 2, 0, Math.Max(0, ch - h)));
                    UpdateBox();
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        // Cache the panel's screen box (device px) for the cross-thread hover test + the wheel hook.
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

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        public struct Row
        {
            public string Name; public string Text; public string Note; public bool Current;
            public int Emph;   // 0 = normal, 1 = guaranteed-tribe relevant (green), 2 = unique enabler (purple)
        }

        /// <summary>One guaranteed-tribe pool minion, rendered as its card art in the left column.
        /// Emph: 1 = pool member (green outline), 2 = sole enabler of some gift (purple outline).</summary>
        public struct MinionArt { public BgCard Card; public int Emph; }

        /// <summary>Replace the content: contextual header line (may be empty → hidden), one container
        /// per still-obtainable gift, and optionally the guaranteed-tribe pool as card renders in the
        /// left column (auto-fit ≤4 columns; poolTotal &gt; shown count adds a "+N more" note). Sets
        /// the panel width — callers reposition AFTER calling this.</summary>
        public void SetContent(string headerSub, IReadOnlyList<Row> rows, string poolCaption,
            IReadOnlyList<MinionArt> minions, int poolTotal)
        {
            _headerSub.Text = headerSub ?? "";
            _headerSub.Visibility = string.IsNullOrEmpty(headerSub) ? Visibility.Collapsed : Visibility.Visible;
            _rows.Children.Clear();
            foreach (var r in rows) _rows.Children.Add(BuildRow(r));
            try { _scroll.ScrollToTop(); } catch { }
            // Minions-only mode passes an empty row list — the whole gift column collapses and the
            // panel is just the art column.
            bool giftsShown = rows != null && rows.Count > 0;
            _giftColumn.Visibility = giftsShown ? Visibility.Visible : Visibility.Collapsed;

            double artWidth = 0;
            _artWrap.Children.Clear();
            if (minions != null && minions.Count > 0)
            {
                // Auto-fit: fewest columns unless another one buys meaningfully bigger cards (+15px),
                // capped at 200 wide / 4 columns, floored at ~110 so a huge pool stays recognizable.
                int n = minions.Count;
                double bestW = 0; int bestC = 1;
                for (int c = 1; c <= 4; c++)
                {
                    int rws = (n + c - 1) / c;
                    double w = Math.Min(ArtMaxW, (ArtMaxH / rws - ArtGap) / ArtAspect);
                    if (w > bestW + 15) { bestW = w; bestC = c; }
                }
                double cardW = Math.Max(ArtMinW, bestW);
                int decode = (int)Math.Min(256, cardW * 1.25);

                _artWrap.Width = bestC * (cardW + ArtGap + 8);   // +8 ≈ per-cell border/padding
                foreach (var m in minions)
                {
                    var img = new Image { Width = cardW, Stretch = Stretch.Uniform };
                    ArtImage.SetDecode(img, decode);
                    ArtImage.SetCard(img, m.Card);
                    _artWrap.Children.Add(new Border
                    {
                        BorderBrush = m.Emph == 2 ? UniqueBrush : TribeBrush,
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(2),
                        Margin = new Thickness(0, 0, ArtGap, ArtGap),
                        Child = img
                    });
                }

                _artCaption.Text = poolCaption ?? "";
                _artCaption.Visibility = string.IsNullOrEmpty(poolCaption) ? Visibility.Collapsed : Visibility.Visible;
                _artCaption.MaxWidth = _artWrap.Width;
                if (poolTotal > n) { _artMore.Text = "+" + (poolTotal - n) + " more in the pool"; _artMore.Visibility = Visibility.Visible; }
                else _artMore.Visibility = Visibility.Collapsed;

                _artColumn.Visibility = Visibility.Visible;
                artWidth = _artWrap.Width + 10;                  // + column right margin
            }
            else _artColumn.Visibility = Visibility.Collapsed;

            _root.Width = (giftsShown ? ContentWidth : 0) + 22 + artWidth;
        }

        private static UIElement BuildRow(Row r)
        {
            Brush accent = r.Emph == 2 ? UniqueBrush : r.Emph == 1 ? TribeBrush : UiKit.AccentBrush;
            Color glow = r.Emph == 2 ? UniqueColor : r.Emph == 1 ? TribeColor : UiKit.Accent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = r.Name,
                Foreground = r.Current ? accent : UiKit.TextSecondary,
                FontSize = 14,
                FontWeight = r.Current ? FontWeights.Bold : FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var sep = new Rectangle
            {
                Width = 1,
                Fill = r.Current ? accent : UiKit.StrokeBrush,
                Opacity = r.Current ? 0.55 : 1.0,
                Margin = new Thickness(8, 1, 9, 1),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(sep, 1);
            grid.Children.Add(sep);

            var text = new TextBlock
            {
                FontSize = 14,
                Foreground = r.Current ? UiKit.TextPrimary : UiKit.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            AddColoredRuns(text, r.Text);
            if (!string.IsNullOrEmpty(r.Note))
                text.Inlines.Add(new Run("  — " + r.Note)
                {
                    Foreground = UiKit.TextSecondary,   // italic already distinguishes it — don't dim
                    FontStyle = FontStyles.Italic,
                    FontSize = 13
                });
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var box = new Border
            {
                Background = r.Current ? RowBg : RowBgDim,
                BorderBrush = r.Current ? accent : UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 5, 9, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid
            };
            if (r.Current)
                box.Effect = new DropShadowEffect
                {
                    Color = glow,
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.45
                };
            else
                box.Opacity = 0.5;
            return box;
        }

        // Word-level color highlighting: stat gains in green, keyword-ish terms in gold, rest default.
        private static readonly Regex Highlight = new Regex(
            @"(?<stat>\+\d+(?:/\+\d+)?(?:\s+(?:Attack|Health))?)|" +
            @"(?<kw>Divine Shield|Windfury|Stealth|Venomous|Reborn|Golden|Immune|Deathrattles?|Battlecr(?:y|ies)|Rally|Spellcrafts?|Start of Combat|Magnetize|Blood Gems|Taunt)",
            RegexOptions.Compiled);

        private static void AddColoredRuns(TextBlock tb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int pos = 0;
            foreach (Match m in Highlight.Matches(text))
            {
                if (m.Index > pos) tb.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                tb.Inlines.Add(new Run(m.Value)
                {
                    Foreground = m.Groups["stat"].Success ? StatBrush : KeywordBrush,
                    FontWeight = FontWeights.SemiBold
                });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) tb.Inlines.Add(new Run(text.Substring(pos)));
        }

        // ── Wheel forwarding (low-level mouse hook, active only while visible) ──────────────────────
        // The cursor sits on the game's button while the panel is up, so WPF never receives the wheel.
        // The hook watches WM_MOUSEWHEEL globally, scrolls our list, and NEVER swallows the event; when
        // the cursor IS over the panel, native WPF wheel handling takes over instead (no double-scroll).
        private IntPtr _wheelHook = IntPtr.Zero;
        private LowLevelMouseProc _wheelProc;    // keep the delegate alive while hooked

        private void InstallWheelHook()
        {
            if (_wheelHook != IntPtr.Zero) return;
            try
            {
                _wheelProc = WheelHookProc;
                _wheelHook = SetWindowsHookEx(WH_MOUSE_LL, _wheelProc, GetModuleHandle(null), 0);
            }
            catch { _wheelHook = IntPtr.Zero; _wheelProc = null; }
        }

        private void RemoveWheelHook()
        {
            if (_wheelHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_wheelHook); } catch { }
            _wheelHook = IntPtr.Zero;
            _wheelProc = null;
        }

        private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL && !IsUnderMouse)
            {
                try
                {
                    // MSLLHOOKSTRUCT: POINT pt (8 bytes) then DWORD mouseData — wheel delta in the high word.
                    int mouseData = Marshal.ReadInt32(lParam, 8);
                    int delta = (short)((mouseData >> 16) & 0xFFFF);
                    _root.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - delta / 120.0 * 64.0); } catch { }
                    }));
                }
                catch { }
            }
            return CallNextHookEx(_wheelHook, nCode, wParam, lParam);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
