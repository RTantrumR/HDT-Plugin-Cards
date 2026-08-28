using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.API;                     // Core.OverlayCanvas
using Hearthstone_Deck_Tracker.Utility.Extensions;      // OverlayExtensions
using HsbgCardLookup.Config;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// Puts one <see cref="FinalBoardPanel"/> on HDT's overlay canvas and gives the player the three
    /// things a panel over a running game needs: somewhere to move it, a way to take a picture of it
    /// and a way to make it go away.
    ///
    /// It is deliberately not tied to any trigger. The real surfaces the plan calls for — the click
    /// on HDT's session row, the post-match screen — differ only in WHEN they call
    /// <see cref="Show"/>, so they reuse this host rather than each growing their own copy of the
    /// drag, the screenshot and the ✕.
    ///
    /// The panel sits inside a WRAPPER that carries the canvas position, and the panel itself stays
    /// at the wrapper's origin. That is what lets the screenshot render the panel alone: a visual is
    /// rendered together with its offset from its parent, so capturing something positioned directly
    /// on the canvas would bake in however far across the screen it had been dragged. Registering
    /// the PANEL (not the wrapper) as hit-test-visible is the other half — the panel's scale lives
    /// in its own RenderTransform, and HDT derives its bounds through that transform, whereas the
    /// wrapper's layout size ignores it and would claim the cursor well outside the visible panel.
    /// </summary>
    internal sealed class FinalBoardSurface
    {
        private readonly PluginConfig _config;
        private readonly Action<string> _log;

        private readonly FinalBoardPanel _panel;
        private readonly Grid _wrap;
        private readonly Border _toast;
        private readonly TextBlock _toastText;
        private readonly DispatcherTimer _toastHide;

        private Canvas Host => Core.OverlayCanvas;
        private bool _attached;
        private FinalBoardRecord _current;

        /// <summary>
        /// The player dismissed the panel. Whoever opened it gets to decide what comes back --
        /// the settings window, for one, hid itself to get out of the way and has to be restored, or
        /// it is stranded with no visible route back to it.
        /// </summary>
        public Action Closed;

        // Gesture state; canvas thread only (the low-level hook posts back onto it).
        private bool _dragging, _moved;
        private Point _startCursor;
        private double _startLeft, _startTop;

        public FinalBoardSurface(PluginConfig config, Action<string> log)
        {
            _config = config;
            _log = log;

            _panel = new FinalBoardPanel(config.FinalBoardDisplay);
            _panel.CloseRequested = Hide;
            _panel.ScreenshotRequested = Capture;

            _toastText = new TextBlock
            {
                FontSize = 12,
                Foreground = UiKit.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
            };
            _toast = new Border
            {
                Background = UiKit.Br(UiKit.PanelActive),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, -34),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = _toastText,
            };

            _wrap = new Grid { Visibility = Visibility.Collapsed };
            _wrap.Children.Add(_panel.Root);
            _wrap.Children.Add(_toast);

            _panel.Root.MouseLeftButtonDown += (s, e) => { e.Handled = true; BeginDrag(e); };
            try { OverlayExtensions.SetIsOverlayHitTestVisible(_panel.Root, true); } catch { }

            _toastHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastHide.Tick += (s, e) => { _toastHide.Stop(); _toast.Visibility = Visibility.Collapsed; };
        }

        public bool IsVisible => _attached && _wrap.Visibility == Visibility.Visible;

        /// <summary>Render a match and show the panel. Canvas thread.</summary>
        public void Show(FinalBoardRecord rec)
        {
            if (rec == null) return;
            var canvas = Host;
            if (canvas == null) return;

            if (!_attached)
            {
                canvas.Children.Add(_wrap);
                canvas.SizeChanged += OnCanvasSizeChanged;
                _attached = true;
            }

            _current = rec;
            _panel.Show(rec);
            _panel.ChromeVisible = true;
            _toast.Visibility = Visibility.Collapsed;
            _wrap.Visibility = Visibility.Visible;

            // The panel's height depends on the record and on which view is open, so it is not known
            // until WPF has measured it — fit and place on the pass after this one.
            canvas.Dispatcher.BeginInvoke(new Action(FitAndPlace), DispatcherPriority.Loaded);
        }

        public void Hide()
        {
            bool was = IsVisible;
            HideCore();
            if (was) { try { Closed?.Invoke(); } catch { } }
        }

        private void HideCore()
        {
            EndDrag(persist: false);
            _toastHide.Stop();
            _toast.Visibility = Visibility.Collapsed;
            _wrap.Visibility = Visibility.Collapsed;
        }

        /// <summary>Remove from the canvas entirely (plugin unload).</summary>
        public void Close()
        {
            HideCore();
            try
            {
                var canvas = Host;
                if (canvas != null)
                {
                    canvas.SizeChanged -= OnCanvasSizeChanged;
                    canvas.Children.Remove(_wrap);
                }
            }
            catch { /* HDT may already be tearing down */ }
            _attached = false;
        }

        /// <summary>
        /// Re-render what is already on screen. The panel holds the display switches BY REFERENCE, so
        /// a toggle flipped in the settings window is picked up here without anything being passed
        /// along — which is the point of a settings page you can watch take effect.
        /// </summary>
        public void Refresh()
        {
            if (IsVisible && _current != null) Show(_current);
        }

        // ── screenshot ──────────────────────────────────────────────────────────────────────────

        private void Capture()
        {
            string dir = FinalBoardExport.ShareDirFor(_config);
            string path;
            bool copied;
            bool ok = FinalBoardExport.CaptureAndShare(_panel, dir, out path, out copied);

            // Named in the order they matter: the clipboard is what the player is about to paste, the
            // file is the copy they keep. Saying "saved" first would bury the useful half.
            string msg;
            if (!ok) msg = "Couldn't take the picture.";
            else if (copied && path != null) msg = "Copied to the clipboard, and saved in " + dir;
            else if (copied) msg = "Copied to the clipboard. Couldn't write the file.";
            else msg = "Saved in " + dir + ". Couldn't reach the clipboard.";

            _log?.Invoke("[FinalBoard] screenshot: " + (path ?? "(no file)") + ", clipboard=" + copied);

            _toastText.Text = msg;
            _toast.Visibility = Visibility.Visible;
            _toastHide.Stop();
            _toastHide.Start();
        }

        // ── fit + placement ─────────────────────────────────────────────────────────────────────

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_wrap.Visibility == Visibility.Visible) FitAndPlace();
        }

        /// <summary>
        /// The panel is authored at a fixed reference size and the stats table makes it tall, so on
        /// anything smaller than the resolution it was drawn for it is scaled down to fit rather
        /// than allowed to run off the canvas. Scaling down is why it is readable at all on a
        /// 1280×720 client; it never scales UP, which would only make it blurry.
        /// </summary>
        private void FitAndPlace()
        {
            var canvas = Host;
            if (canvas == null) return;
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double w = _panel.Root.ActualWidth, h = _panel.Root.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _panel.Scale = Math.Min(1.0, Math.Min(cw * 0.96 / w, ch * 0.94 / h));

            var size = _panel.RenderedSize;
            double left, top;
            var saved = _config.FinalBoardHud;
            if (saved != null && saved.Set)
            {
                left = saved.XF * cw;
                top = saved.YF * ch;
            }
            else
            {
                left = (cw - size.Width) / 2;
                top = (ch - size.Height) / 2;
            }

            Canvas.SetLeft(_wrap, Clamp(left, 0, Math.Max(0, cw - size.Width)));
            Canvas.SetTop(_wrap, Clamp(top, 0, Math.Max(0, ch - size.Height)));
        }

        // ── drag (low-level mouse hook, installed only for the gesture) ─────────────────────────
        // WPF capture cannot be trusted across HDT's overlay: the moment the cursor outruns the
        // panel, HDT's 60 Hz loop re-enables click-through and the window stops receiving input.
        // The hook sees the cursor wherever it goes, and never swallows anything.

        private void BeginDrag(MouseButtonEventArgs e)
        {
            if (!_attached || _dragging) return;
            var canvas = Host;
            if (canvas == null) return;
            try { _startCursor = e.GetPosition(canvas); } catch { return; }
            _startLeft = Canvas.GetLeft(_wrap);
            _startTop = Canvas.GetTop(_wrap);
            if (double.IsNaN(_startLeft)) _startLeft = 0;
            if (double.IsNaN(_startTop)) _startTop = 0;
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
                POINT p;
                GetCursorPos(out p);
                cur = canvas.PointFromScreen(new Point(p.X, p.Y));
            }
            catch { return; }

            double dx = cur.X - _startCursor.X, dy = cur.Y - _startCursor.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 1) _moved = true;

            var size = _panel.RenderedSize;
            Canvas.SetLeft(_wrap, Clamp(_startLeft + dx, 0, Math.Max(0, cw - size.Width)));
            Canvas.SetTop(_wrap, Clamp(_startTop + dy, 0, Math.Max(0, ch - size.Height)));
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

            var hud = _config.FinalBoardHud ?? (_config.FinalBoardHud = new HudPlacement());
            hud.XF = Canvas.GetLeft(_wrap) / cw;
            hud.YF = Canvas.GetTop(_wrap) / ch;
            hud.Set = true;
            try { _config.Save(); } catch { }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // ── hook plumbing ───────────────────────────────────────────────────────────────────────

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelMouseProc _proc;   // keep the delegate alive while hooked

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
