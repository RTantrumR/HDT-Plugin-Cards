using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HsbgCardLookup.Config;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// Renders a stored match to a PNG, off-screen, without a match running.
    ///
    /// This is why the feature stores records as text instead of snapshotting one image when the
    /// match ends: the picture can be produced later, as many times as wanted, at whatever size —
    /// and the same records can feed richer reports that no single screenshot could.
    ///
    /// The catch is that HDT's card controls load their art ASYNCHRONOUSLY, so a panel rendered the
    /// instant it is built comes out with holes where the cards should be. <see cref="RenderAsync"/>
    /// therefore lays the panel out, lets WPF pump, and only then captures — and if it still is not
    /// ready it gives up rather than writing a half-empty image (a missing file is honest; a picture
    /// with blank cards looks like a broken feature).
    /// </summary>
    internal static class FinalBoardExport
    {
        private const int DefaultSettleMs = 2500;

        /// <summary>Captured at twice the on-screen size: a panel scaled down to fit a 1080p canvas
        /// would otherwise be shared at that reduced size, and a picture is read at whatever size
        /// the person it was sent to opens it at.</summary>
        private const double ShareScale = 2.0;

        /// <summary>Where shared pictures land unless the player has chosen somewhere else. Our own
        /// folder, not the game's — this plugin is not the only thing that writes final boards, and
        /// two of them in one directory is a mess.</summary>
        public static string DefaultShareDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HSBG Card Lookup");

        /// <summary>The folder in force: the player's, or the default when they have not picked one.</summary>
        public static string ShareDirFor(PluginConfig config) =>
            config != null && !string.IsNullOrWhiteSpace(config.FinalBoardShotDir)
                ? config.FinalBoardShotDir
                : DefaultShareDir;

        /// <summary>
        /// Capture the panel AS SHOWN — the view that is open, the blocks that are switched on, the
        /// scale it is being read at — then put it on the clipboard and write it to
        /// <paramref name="dir"/>. The chrome comes off first: the tabs, the camera and the ✕ are how
        /// the player operates the panel, and nobody they send the picture to can press them.
        ///
        /// The two sinks are independent on purpose. The clipboard can be held by another process
        /// and refuse us; the file can fail on a full or redirected Pictures folder. Either one
        /// alone still gets the picture out, so the caller is told which of them worked.
        /// </summary>
        public static bool CaptureAndShare(FinalBoardPanel panel, string dir, out string path, out bool copied)
        {
            path = null;
            copied = false;
            if (panel == null) return false;

            bool chrome = panel.ChromeVisible;
            try
            {
                panel.ChromeVisible = false;
                panel.Root.UpdateLayout();

                var bmp = Snapshot(panel.Root);
                if (bmp == null) return false;

                // Clipboard first: it is the one the player is usually after, and it is the one that
                // can be lost to another process — so it should not wait behind a disk write.
                copied = TryCopy(bmp);
                try { path = WritePng(bmp, dir); } catch { path = null; }
                return path != null || copied;
            }
            catch { return false; }
            finally
            {
                panel.ChromeVisible = chrome;
            }
        }

        /// <summary>
        /// Render the element on its own. It must sit at the origin of its parent — the surface
        /// keeps the panel inside a wrapper for exactly this reason, because RenderTargetBitmap
        /// honours a visual's offset from its parent and a panel dragged to the middle of the
        /// screen would otherwise come out with that much blank space down its top and left.
        /// </summary>
        private static RenderTargetBitmap Snapshot(FrameworkElement el)
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            var st = el.RenderTransform as ScaleTransform;
            if (st != null) { w *= st.ScaleX; h *= st.ScaleY; }
            if (w <= 1 || h <= 1) return null;

            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(w * ShareScale), (int)Math.Ceiling(h * ShareScale),
                96 * ShareScale, 96 * ShareScale, PixelFormats.Pbgra32);
            bmp.Render(el);
            bmp.Freeze();
            return bmp;
        }

        private static string WritePng(BitmapSource bmp, string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultShareDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir,
                "final-board " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) encoder.Save(fs);
            return file;
        }

        /// <summary>The clipboard is a single system-wide resource and any other process can hold it
        /// mid-copy, so a first failure means "busy", not "broken" — hence the retries.</summary>
        private static bool TryCopy(BitmapSource bmp)
        {
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetImage(bmp); return true; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }

        /// <summary>
        /// Build, settle and save. Runs on the caller's dispatcher (must be a UI thread), and calls
        /// back with the written path, or null if it failed.
        /// </summary>
        public static void RenderAsync(FinalBoardRecord rec, string path, Action<string> done,
                                       int settleMs = DefaultSettleMs, double scale = 1.0,
                                       FinalBoardOptions options = null, bool chrome = false)
        {
            if (rec == null || string.IsNullOrEmpty(path)) { done?.Invoke(null); return; }

            var panel = new FinalBoardPanel(options);
            panel.Show(rec);
            panel.Scale = scale;
            panel.ChromeVisible = chrome;   // an exported picture has nothing to press

            // A host canvas gives the controls a layout pass to run in. It is never shown; the
            // controls only need to be measured and arranged for their art to start loading.
            var host = new Canvas { Width = 4000, Height = 4000, Background = Brushes.Transparent };
            host.Children.Add(panel.Root);
            host.Measure(new Size(4000, 4000));
            host.Arrange(new Rect(0, 0, 4000, 4000));
            host.UpdateLayout();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(settleMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                string written = null;
                try { written = Capture(panel.Root, path); }
                catch { written = null; }
                try { host.Children.Clear(); } catch { }
                done?.Invoke(written);
            };
            timer.Start();
        }

        private static string Capture(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            int w = (int)Math.Ceiling(element.ActualWidth * element.LayoutTransform.Value.M11);
            int h = (int)Math.Ceiling(element.ActualHeight * element.LayoutTransform.Value.M22);
            if (element.RenderTransform is ScaleTransform st)
            {
                w = (int)Math.Ceiling(element.ActualWidth * st.ScaleX);
                h = (int)Math.Ceiling(element.ActualHeight * st.ScaleY);
            }
            if (w <= 0 || h <= 0) return null;

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return path;
        }
    }
}
