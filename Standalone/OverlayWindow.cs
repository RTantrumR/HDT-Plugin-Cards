using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HsbgCardLookup.Data;

namespace Toolkit.Standalone
{
    /// <summary>
    /// A transparent, click-through, never-activated window kept exactly over Hearthstone's client
    /// area. Shown only while Hearthstone is the foreground window and a Battlegrounds board exists.
    /// Slot coordinates are the HDT plugin's own (1920×1080 reference, 16:9 content box).
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        private const double RefW = 1920.0, RefH = 1080.0;
        private static readonly double[] RefSlotLeft = { 255.00, 252.14, 249.29, 246.43, 243.57, 240.71, 237.86, 235.00 };
        private static readonly double[] RefSlotTop = { 168.0, 260.0, 355.0, 445.0, 540.0, 633.0, 727.0, 822.0 };
        private const int Slots = 8;

        private readonly Canvas _canvas = new Canvas();
        private readonly Border[] _plates = new Border[Slots];
        private readonly TextBlock[] _names = new TextBlock[Slots], _mmrs = new TextBlock[Slots], _counts = new TextBlock[Slots];
        private readonly Image[] _tiers = new Image[Slots];
        private readonly Border[] _tribes = new Border[Slots];
        private readonly StackPanel _lobby = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly string _assets;
        private readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>();

        private Snapshot _snap;
        private MmrTable _mmr;
        private string _lobbyKey;
        private int _lastX = int.MinValue, _lastY, _lastW, _lastH;

        /// <summary>Test switch (--ignore-focus): draw even when Hearthstone isn't the foreground window.</summary>
        public bool IgnoreFocus;

        public OverlayWindow(string assetsDir)
        {
            _assets = assetsDir;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000; Top = -10000; Width = 10; Height = 10;
            Content = _canvas;

            for (int i = 0; i < Slots; i++)
            {
                _names[i] = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                _mmrs[i] = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)), FontWeight = FontWeights.Bold };
                var stack = new StackPanel();
                stack.Children.Add(_names[i]);
                stack.Children.Add(_mmrs[i]);
                _plates[i] = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x10, 0x0C)), Child = stack, Visibility = Visibility.Collapsed };
                _tiers[i] = new Image { Visibility = Visibility.Collapsed };
                _tribes[i] = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x50)), Visibility = Visibility.Collapsed };
                _counts[i] = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, Visibility = Visibility.Collapsed };
                _canvas.Children.Add(_plates[i]);
                _canvas.Children.Add(_tiers[i]);
                _canvas.Children.Add(_tribes[i]);
                _canvas.Children.Add(_counts[i]);
            }
            _canvas.Children.Add(_lobby);

            SourceInitialized += (s, e) => MakeClickThrough();
            SizeChanged += (s, e) => Layout();

            // 60 Hz glue: follow the HS window, hide when it isn't in front.
            var glue = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            glue.Tick += (s, e) => Glue();
            glue.Start();
        }

        // ── window plumbing ────────────────────────────────────────────────────────────────────────
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        [DllImport("user32")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);

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
            bool want = hs && (fg || IgnoreFocus) && BoardShown(_snap);
            if (!want) { if (Visibility == Visibility.Visible) Hide(); return; }
            if (x != _lastX || y != _lastY || w != _lastW || h != _lastH || Visibility != Visibility.Visible)
            {
                if (Visibility != Visibility.Visible) Show();
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
                        rtb.Render(_canvas);
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

        [DllImport("user32")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        // The leaderboard is on screen once tiers are dealt; during hero select (the VS screen) every
        // tile still reads tier 0 and there is nothing to sit next to.
        private static bool BoardShown(Snapshot s)
        {
            if (s == null) return false;
            foreach (var t in s.Tiles) if (t.Tier > 0) return true;
            return false;
        }

        // ── content ────────────────────────────────────────────────────────────────────────────────
        public void Update(Snapshot snap, MmrTable mmr)
        {
            _snap = snap; _mmr = mmr;
            var tiles = snap?.Tiles ?? new List<Tile>();
            for (int i = 0; i < Slots; i++)
            {
                var t = i < tiles.Count ? tiles[i] : null;
                if (t == null) { Collapse(i); continue; }

                bool plate = !t.IsMe && !string.IsNullOrEmpty(t.Name);
                _plates[i].Visibility = plate ? Visibility.Visible : Visibility.Collapsed;
                if (plate)
                {
                    _names[i].Text = t.Name;
                    int? r = mmr?.Lookup(t.Name);
                    _mmrs[i].Text = r.HasValue ? r.Value.ToString("N0") : (mmr != null && mmr.Loaded ? "—" : "…");
                }

                var tier = t.Tier >= 1 && t.Tier <= 7 ? Img("tiers", "Tier" + t.Tier + ".png") : null;
                _tiers[i].Source = tier;
                _tiers[i].Visibility = tier != null ? Visibility.Visible : Visibility.Collapsed;

                int count;
                string tribe = TribeTally.Dominant(t.Races, out count);
                var icon = tribe == TribeTally.None ? null : Img("tribes", tribe + ".jpg");
                _tribes[i].Background = icon != null ? new ImageBrush(icon) { Stretch = Stretch.UniformToFill } : null;
                _tribes[i].Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
                _counts[i].Text = count > 0 ? count.ToString() : "";
                _counts[i].Visibility = icon != null && count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // Lobby tribes: rebuild only when the set changes.
            string key = snap == null ? "" : string.Join(",", snap.LobbyTribes);
            if (key != _lobbyKey)
            {
                _lobbyKey = key;
                _lobby.Children.Clear();
                if (snap != null)
                    foreach (int race in snap.LobbyTribes)
                    {
                        string name = TribeName(race);
                        var img = name == null ? null : Img("tribes", name + ".jpg");
                        if (img != null) _lobby.Children.Add(new Border { Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, ToolTip = name, Tag = name });
                    }
            }
            Layout();
        }

        private void Collapse(int i)
        {
            _plates[i].Visibility = _tiers[i].Visibility = _tribes[i].Visibility = _counts[i].Visibility = Visibility.Collapsed;
        }

        private void Layout()
        {
            double cw = ActualWidth, ch = ActualHeight;
            if (cw <= 20 || ch <= 20) return;
            double contentW = Math.Min(cw, ch * (16.0 / 9.0));
            double contentH = Math.Min(ch, contentW * (9.0 / 16.0));
            double left0 = (cw - contentW) / 2.0, top0 = (ch - contentH) / 2.0;
            double s = Math.Max(0.6, Math.Min(contentH / RefH, 2.0));
            Func<double, double> X = rx => left0 + rx / RefW * contentW;
            Func<double, double> Y = ry => top0 + ry / RefH * contentH;

            for (int i = 0; i < Slots; i++)
            {
                double left = X(RefSlotLeft[i]), top = Y(RefSlotTop[i] - 5);
                var p = _plates[i];
                p.Width = 104 * s; p.Height = 38 * s;
                p.CornerRadius = new CornerRadius(4 * s);
                p.Padding = new Thickness(4 * s, 1 * s, 4 * s, 1 * s);
                _names[i].FontSize = 12 * s; _mmrs[i].FontSize = 12.5 * s;
                Canvas.SetLeft(p, left); Canvas.SetTop(p, top);

                double tierLeft = left + 106 * s;
                _tiers[i].Height = 32 * s;
                Canvas.SetLeft(_tiers[i], tierLeft); Canvas.SetTop(_tiers[i], top);

                var tb = _tribes[i];
                tb.Width = tb.Height = 28 * s; tb.CornerRadius = new CornerRadius(14 * s); tb.BorderThickness = new Thickness(1.5 * s);
                double tribeLeft = tierLeft + 34 * s;
                Canvas.SetLeft(tb, tribeLeft); Canvas.SetTop(tb, top + 2 * s);
                _counts[i].FontSize = 16 * s;
                Canvas.SetLeft(_counts[i], tribeLeft + 31 * s); Canvas.SetTop(_counts[i], top + 5 * s);
            }

            foreach (UIElement c in _lobby.Children)
            {
                var b = (Border)c;
                b.Width = b.Height = 34 * s; b.CornerRadius = new CornerRadius(17 * s); b.Margin = new Thickness(0, 0, 4 * s, 0);
                b.BorderThickness = new Thickness(1.5 * s); b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x50));
            }
            Canvas.SetLeft(_lobby, X(40)); Canvas.SetTop(_lobby, Y(100));   // under the game's own name line top-left
        }

        private ImageSource Img(string folder, string file)
        {
            string key = folder + "/" + file;
            ImageSource img;
            if (_images.TryGetValue(key, out img)) return img;
            string path = Path.Combine(_assets, folder, file);
            img = null;
            if (File.Exists(path))
            {
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                img = b;
            }
            _images[key] = img;
            return img;
        }

        private static string TribeName(int race)
        {
            switch (race)
            {
                case 11: return "Undead"; case 14: return "Murloc"; case 15: return "Demon"; case 17: return "Mech";
                case 18: return "Elemental"; case 20: return "Beast"; case 23: return "Pirate"; case 24: return "Dragon";
                case 43: return "Quilboar"; case 92: return "Naga"; case 126: return "Aberration";
                default: return null;
            }
        }
    }
}
