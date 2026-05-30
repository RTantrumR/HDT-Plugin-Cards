using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Decodes WebP bytes to a frozen WPF <see cref="BitmapSource"/> via SixLabors.ImageSharp
    /// (pure-managed; WPF has no native WebP codec). Frozen → safe to build off the UI thread.
    /// </summary>
    internal static class WebpDecoder
    {
        // Diagnostics hook (set from Plugin): logs the first decode success + first failure.
        internal static Action<string> Log;
        private static bool _loggedError, _loggedOk;

        // maxWidth > 0 downscales wider images (grid thumbnails); 0 keeps native size.
        public static BitmapSource Decode(byte[] data, int maxWidth = 0)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                using (var img = Image.Load<Bgra32>(data))
                {
                    if (maxWidth > 0 && img.Width > maxWidth)
                    {
                        int rh = (int)Math.Round((double)img.Height * maxWidth / img.Width);
                        img.Mutate(x => x.Resize(maxWidth, rh));
                    }
                    int w = img.Width, h = img.Height, stride = w * 4;
                    var px = new byte[h * stride];
                    img.CopyPixelDataTo(px);
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                    bmp.Freeze();
                    if (!_loggedOk) { _loggedOk = true; try { Log?.Invoke($"WebpDecoder OK ({w}x{h})"); } catch { } }
                    return bmp;
                }
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; try { Log?.Invoke("WebpDecoder FAILED: " + ex); } catch { } }
                return null;
            }
        }
    }
}
