using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        /// <summary>
        /// Build, settle and save. Runs on the caller's dispatcher (must be a UI thread), and calls
        /// back with the written path, or null if it failed.
        /// </summary>
        public static void RenderAsync(FinalBoardRecord rec, string path, Action<string> done,
                                       int settleMs = DefaultSettleMs, double scale = 1.0)
        {
            if (rec == null || string.IsNullOrEmpty(path)) { done?.Invoke(null); return; }

            var panel = new FinalBoardPanel();
            panel.Show(rec);
            panel.Scale = scale;

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
