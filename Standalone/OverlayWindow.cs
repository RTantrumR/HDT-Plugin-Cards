using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Toolkit.Standalone
{
    /// <summary>
    /// A transparent, click-through, never-activated window kept exactly over Hearthstone's client
    /// area, shown only while Hearthstone is the foreground window. Its <see cref="Surface"/> canvas
    /// is what the plugin's own features draw on — the standalone counterpart of HDT's overlay canvas.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        public readonly Canvas Surface = new Canvas();

        /// <summary>Test switch (--ignore-focus): draw even when Hearthstone isn't the foreground window.</summary>
        public bool IgnoreFocus;
        public Action<string> Log;

        private int _lastX = int.MinValue, _lastY, _lastW, _lastH;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000; Top = -10000; Width = 10; Height = 10;
            Content = Surface;

            SourceInitialized += (s, e) => MakeClickThrough();

            // 60 Hz glue: follow the HS window, hide when it isn't in front.
            var glue = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            glue.Tick += (s, e) => Glue();
            glue.Start();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        [DllImport("user32")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

        private void MakeClickThrough()
        {
            var h = Hwnd;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        }

        private void Glue()
        {
            int x, y, w, h; bool fg;
            bool hs = HsWindow.ClientArea(out x, out y, out w, out h, out fg);
            bool want = hs && (fg || IgnoreFocus);
            if (!want) { if (Visibility == Visibility.Visible) { Hide(); Log?.Invoke("overlay hidden (hs=" + hs + " foreground=" + fg + ")"); } return; }
            if (x != _lastX || y != _lastY || w != _lastW || h != _lastH || Visibility != Visibility.Visible)
            {
                if (Visibility != Visibility.Visible) { Show(); Log?.Invoke("overlay shown (foreground=" + fg + ")"); }
                SetWindowPos(Hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                _lastX = x; _lastY = y; _lastW = w; _lastH = h;
            }
        }

        /// <summary>Test aid: Hearthstone's own frame (PrintWindow) with this overlay drawn on top, as a PNG.</summary>
        public string SaveComposite(string path)
        {
            int x, y, w, h; bool fg;
            if (!HsWindow.ClientArea(out x, out y, out w, out h, out fg)) return "no HS window";
            using (var bmp = new System.Drawing.Bitmap(w, h))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok = PrintWindow(HsWindow.Find(), dc, 0x1 | 0x2);   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                    g.ReleaseHdc(dc);
                    if (!ok) return "PrintWindow failed";
                    if (ActualWidth > 0 && Visibility == Visibility.Visible)
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                        rtb.Render(Surface);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var ms = new MemoryStream())
                        {
                            enc.Save(ms); ms.Position = 0;
                            using (var over = System.Drawing.Image.FromStream(ms)) g.DrawImage(over, 0, 0, w, h);
                        }
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return "saved " + w + "x" + h + (Visibility == Visibility.Visible ? "" : " (overlay hidden)");
        }
    }
}
