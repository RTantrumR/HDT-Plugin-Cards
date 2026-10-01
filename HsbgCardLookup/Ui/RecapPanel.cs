using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions
using HsbgCardLookup.Game.Recap;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// The match-recap card: a small panel inside HDT's overlay canvas, shown at match end. A ✕
    /// closes it; left-drag moves it and reports the new placement fractions. The drag runs on a
    /// WH_MOUSE_LL hook installed only for the gesture, exactly as <see cref="MmrSidePanel"/> does —
    /// WPF capture cannot be trusted here, because HDT's 60 Hz hover loop re-enables click-through
    /// the instant the cursor outruns the panel.
    ///
    /// Layout: a title row (hero · place · turns), then one row per stat — label, the big number,
    /// and a detail column that carries the average only when there is one.
    /// </summary>
    internal sealed class RecapPanel
    {
        private const double DefaultXF = 0.005, DefaultYF = 0.12;
        private const double Width = 320;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xEC, 0x0F, 0x13, 0x1B));
        private static readonly Brush Rule = Frozen(Color.FromArgb(0x55, 0x39, 0x47, 0x5E));
        private static readonly Brush Good = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Nudge = Frozen(Color.FromRgb(0xF0, 0xA0, 0x60));
        private static readonly Brush ApmColor = Frozen(Color.FromRgb(0x7C, 0xC4, 0xFF));
        private static readonly Brush DamageColor = Frozen(Color.FromRgb(0xFF, 0x8A, 0x7A));

        /// <summary>A drag ended — receives the new placement fractions (xf, yf).</summary>
        public Action<double, double> GeometryChanged;
        public Action CloseRequested;

        private readonly Border _root;
        private readonly TextBlock _title;
        private readonly StackPanel _body;
        private bool _attached;
        private double _xf = DefaultXF, _yf = DefaultYF;
        private bool _dragging, _moved;
        private Point _startCursor;
        private double _startLeft, _startTop;

        private static Canvas Host => Core.OverlayCanvas;

        public RecapPanel()
        {
            _title = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 22, 6), TextWrapping = TextWrapping.Wrap };
            _body = new StackPanel();

            var stack = new StackPanel();
            stack.Children.Add(_title);
            stack.Children.Add(_body);

            var closeGlyph = new TextBlock
            {
                Text = "✕", FontSize = 12, Foreground = UiKit.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var close = new Border
            {
                Background = Brushes.Transparent, Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand, Child = closeGlyph
            };
            close.MouseEnter += (s, e) => closeGlyph.Foreground = UiKit.TextPrimary;
            close.MouseLeave += (s, e) => closeGlyph.Foreground = UiKit.TextMuted;
            close.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; try { CloseRequested?.Invoke(); } catch { } };

            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(close);

            _root = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 10),
                Width = Width,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.SizeAll,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.7 },
                Child = grid
            };
            _root.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginDrag(e); };
            _root.SizeChanged += (s, e) => { if (!_dragging) ClampPosition(); };
            try { OverlayExtensions.SetIsOverlayHitTestVisible(_root, true); } catch { }
        }

        public bool IsVisible => _attached && _root.Visibility == Visibility.Visible;

        /// <summary>Restore the saved placement (call before the first show).</summary>
        public void Place(double xf, double yf)
        {
            if (xf >= 0 && xf <= 1 && yf >= 0 && yf <= 1) { _xf = xf; _yf = yf; }
        }

        /// <summary>Fill and show. Canvas thread.</summary>
        public void Show(RecapText t)
        {
            if (!Attach()) return;
            var inv = CultureInfo.InvariantCulture;

            // Title: hero in accent, the rest muted — "Cenarius   1st · 16 turns".
            _title.Inlines.Clear();
            if (!string.IsNullOrEmpty(t.Hero))
                _title.Inlines.Add(new System.Windows.Documents.Run(t.Hero + "   ") { Foreground = UiKit.AccentBrush });
            else
                _title.Inlines.Add(new System.Windows.Documents.Run("Match recap   ") { Foreground = UiKit.AccentBrush });
            string meta = (t.Place != null ? t.Place + " · " : "") + t.Turns + (t.Turns == 1 ? " turn" : " turns");
            _title.Inlines.Add(new System.Windows.Documents.Run(meta) { Foreground = UiKit.TextSecondary, FontWeight = FontWeights.Normal, FontSize = 13 });

            _body.Children.Clear();
            _body.Children.Add(StatRow("APM", t.Apm.ToString("0", inv), ApmColor,
                t.PeakCount > 0 ? string.Format(inv, "peak {0} actions in {1:0} s on turn {2}", t.PeakCount, t.PeakWindow, t.PeakTurn) : null,
                t.AvgApm.HasValue ? "avg " + t.AvgApm.Value.ToString("0", inv) : null));
            _body.Children.Add(HRule());
            _body.Children.Add(StatRow("Damage dealt", t.Damage.ToString(inv), DamageColor,
                string.Format(inv, "{0:0.0} per combat", t.PerCombat),
                t.AvgPerCombat.HasValue ? "avg " + t.AvgPerCombat.Value.ToString("0.0", inv) + " per combat" : null));

            if (t.Sentences.Count > 0)
            {
                _body.Children.Add(HRule());
                foreach (var s in t.Sentences)
                {
                    _body.Children.Add(new TextBlock
                    {
                        Text = s.Key, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                        Foreground = s.Value ? Good : Nudge,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                }
            }

            _root.Visibility = Visibility.Visible;
            ClampPosition();
        }

        /// <summary>label | big number | detail (and, when known, the average under it).</summary>
        private static UIElement StatRow(string label, string value, Brush valueBrush, string detail, string avg)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock { Text = label, Foreground = UiKit.TextMuted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0); g.Children.Add(lbl);

            var val = new TextBlock
            {
                Text = value, Foreground = valueBrush, FontSize = 22, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 44
            };
            Grid.SetColumn(val, 1); g.Children.Add(val);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (detail != null)
                right.Children.Add(new TextBlock { Text = detail, Foreground = UiKit.TextSecondary, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            if (avg != null)
                right.Children.Add(new TextBlock { Text = avg, Foreground = UiKit.TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(right, 2); g.Children.Add(right);
            return g;
        }

        private static UIElement HRule() => new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 2, 0, 2) };

        public void Hide()
        {
            EndDrag(persist: false);
            _root.Visibility = Visibility.Collapsed;
        }

        public void Close()
        {
            EndDrag(persist: false);
            try
            {
                var canvas = Host;
                if (canvas != null)
                {
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_root);
                }
            }
            catch { /* HDT may already be tearing down */ }
            _attached = false;
        }

        private bool Attach()
        {
            if (_attached) return true;
            var canvas = Host;
            if (canvas == null) return false;
            canvas.Children.Add(_root);
            canvas.SizeChanged += OnCanvasSizeChanged;
            _attached = true;
            return true;
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) { if (!_dragging) ClampPosition(); }

        private void ClampPosition()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            double w = _root.ActualWidth > 0 ? _root.ActualWidth : Width;
            double h = _root.ActualHeight > 0 ? _root.ActualHeight : 80;
            Canvas.SetLeft(_root, Clamp(_xf * cw, 0, Math.Max(0, cw - w)));
            Canvas.SetTop(_root, Clamp(_yf * ch, 0, Math.Max(0, ch - h)));
        }

        // ── drag (LL mouse hook, installed only for the gesture) ────────────────────────────────
        private void BeginDrag(MouseButtonEventArgs e)
        {
            if (!_attached || _dragging) return;
            var canvas = Host;
            if (canvas == null) return;
            try { _startCursor = e.GetPosition(canvas); } catch { return; }
            _startLeft = Canvas.GetLeft(_root);
            _startTop = Canvas.GetTop(_root);
            if (double.IsNaN(_startLeft) || double.IsNaN(_startTop)) return;
            _moved = false;
            _dragging = true;
            InstallHook();
        }

        private void OnDragMove()
        {
            var canvas = Host;
            if (canvas == null) { EndDrag(persist: false); return; }
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            Point cur;
            try
            {
                GetCursorPos(out POINT p);
                cur = canvas.PointFromScreen(new Point(p.X, p.Y));
            }
            catch { return; }
            double dx = cur.X - _startCursor.X, dy = cur.Y - _startCursor.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 1) _moved = true;
            Canvas.SetLeft(_root, Clamp(_startLeft + dx, 0, Math.Max(0, cw - _root.ActualWidth)));
            Canvas.SetTop(_root, Clamp(_startTop + dy, 0, Math.Max(0, ch - _root.ActualHeight)));
        }

        private void EndDrag(bool persist = true)
        {
            if (!_dragging) return;
            _dragging = false;
            RemoveHook();
            if (!persist || !_moved) return;
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;
            _xf = Canvas.GetLeft(_root) / cw;
            _yf = Canvas.GetTop(_root) / ch;
            try { GeometryChanged?.Invoke(_xf, _yf); } catch { }
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelMouseProc _proc;

        private void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _proc = HookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            }
            catch { _hook = IntPtr.Zero; _proc = null; _dragging = false; }
        }

        private void RemoveHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _proc = null;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                if (wParam == (IntPtr)WM_MOUSEMOVE) { try { OnDragMove(); } catch { } }
                else if (wParam == (IntPtr)WM_LBUTTONUP) { try { EndDrag(); } catch { } }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
