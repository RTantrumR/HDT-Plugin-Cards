using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// A small draggable, no-activate/topmost text panel that lists opponents + their MMR during a BG
    /// match. Sibling of <see cref="FloatingCard"/>: it reuses the same Win32 techniques (never takes the
    /// OS foreground, so Hearthstone stays foreground and HDT keeps its overlay; the whole body reports
    /// <c>HTCAPTION</c> so a left-drag moves it) but renders rows of text instead of aspect-locked art.
    /// Auto-sizes to its content; position persists via the host's <see cref="IFloatingCardHost.GeometryChanged"/>.
    /// </summary>
    public sealed class MmrPanel : Window
    {
        private readonly Action<MmrPanel> _onMoved;
        private readonly StackPanel _rows;
        private HwndSource _src;

        private static readonly Brush PanelBg = Frozen(Color.FromArgb(0xE6, 0x12, 0x16, 0x1E));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x8A, 0x93, 0xA6));
        private static readonly Brush UpBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));    // gained MMR today
        private static readonly Brush DownBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));  // lost MMR today

        public MmrPanel(Action<MmrPanel> onMoved)
        {
            _onMoved = onMoved;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;               // never grab activation/foreground
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;

            _rows = new StackPanel { MinWidth = 168 };

            var root = new Border
            {
                Background = PanelBg,
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8, 11, 9),
                Child = _rows
            };
            Content = root;

            SourceInitialized += OnSourceInitialized;
        }

        /// <summary>Position the window's top-left at the given DIP coordinates (restores saved placement).</summary>
        public void Place(double left, double top) { Left = left; Top = top; }

        public struct Row { public string Name; public int Rating; public int Delta; }  // Delta = today's change (0 = no arrow)

        /// <summary>Replace the panel contents with a header + one row per opponent (rating, or 8000↓).</summary>
        public void SetRows(IReadOnlyList<Row> items)
        {
            _rows.Children.Clear();
            _rows.Children.Add(new TextBlock
            {
                Text = "Lobby MMR",
                Foreground = UiKit.AccentBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });

            foreach (var it in items)
            {
                var name = new TextBlock
                {
                    Text = it.Name,
                    Foreground = UiKit.TextPrimary,
                    FontSize = 13,
                    Margin = new Thickness(0, 1, 12, 1),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 220
                };
                var rating = new TextBlock
                {
                    Text = it.Rating > 0 ? it.Rating.ToString() : "8000↓",
                    Foreground = it.Rating > 0 ? UiKit.AccentBrush : Muted,
                    FontSize = 13,
                    FontWeight = it.Rating > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    Margin = new Thickness(0, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Rating + today's ↑/↓ arrow (green up / red down), right-aligned.
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(rating);
                if (it.Delta != 0)
                    right.Children.Add(new TextBlock
                    {
                        Text = (it.Delta > 0 ? " ▲" : " ▼") + Math.Abs(it.Delta),
                        Foreground = it.Delta > 0 ? UpBrush : DownBrush,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(4, 1, 0, 1),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(name);
                _rows.Children.Add(row);
            }
        }

        // ── Win32 plumbing (no-activate + system move; mirrors FloatingCard) ─────────────────────────
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _src = (HwndSource)PresentationSource.FromVisual(this);
                if (_src != null)
                {
                    _src.AddHook(WndProc);
                    int ex = GetWindowLong(_src.Handle, GWL_EXSTYLE);
                    SetWindowLong(_src.Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    handled = true;
                    return (IntPtr)MA_NOACTIVATE;      // a click never steals foreground

                case WM_NCHITTEST:
                    handled = true;
                    return (IntPtr)HTCAPTION;          // whole body → system move (drag to reposition)

                case WM_EXITSIZEMOVE:
                    try { _onMoved?.Invoke(this); } catch { }   // move ended → persist position
                    break;
            }
            return IntPtr.Zero;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int MA_NOACTIVATE = 3;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
