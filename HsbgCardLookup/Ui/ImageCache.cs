using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Caches decoded BitmapImages by path+width so re-rendering result lists (every keystroke)
    /// doesn't re-decode PNGs. UI-thread only. Frozen images are safe to reuse.
    /// </summary>
    internal static class ImageCache
    {
        private static readonly Dictionary<string, BitmapImage> Cache = new Dictionary<string, BitmapImage>();
        private static readonly Dictionary<string, BitmapSource> TrimCache = new Dictionary<string, BitmapSource>();

        /// <summary>Load and cache a decoded image; null path or failure returns null.</summary>
        public static BitmapImage Load(string path, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + decodePixelWidth;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;     // decode now, don't lock the file
                if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                Cache[key] = bmp;
                return bmp;
            }
            catch
            {
                Cache[key] = null;   // remember the miss so we don't retry every keystroke
                return null;
            }
        }

        /// <summary>
        /// Like <see cref="Load"/> but trims fully-transparent edges (hero/spell PNGs have a lot of
        /// transparent padding, which otherwise shows as empty space around the card). Cached.
        /// </summary>
        public static BitmapSource LoadTrimmed(string path, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|trim|" + decodePixelWidth;
            if (TrimCache.TryGetValue(key, out var cached)) return cached;

            var src = Load(path, decodePixelWidth);
            BitmapSource result = src;
            try { if (src != null) result = Trim(src); }
            catch { result = src; }
            TrimCache[key] = result;
            return result;
        }

        internal static BitmapSource Trim(BitmapSource src)
        {
            var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            bgra.CopyPixels(px, stride, 0);

            int left = w, right = -1, top = h, bottom = -1;
            const int step = 2, aMin = 12;
            for (int y = 0; y < h; y += step)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    if (px[rowBase + x * 4 + 3] > aMin)   // alpha
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            }

            if (right < left || bottom < top) return src;                 // fully transparent — leave it
            // Small padding back so we don't shave the very edge of the art.
            left = Math.Max(0, left - step); top = Math.Max(0, top - step);
            right = Math.Min(w - 1, right + step); bottom = Math.Min(h - 1, bottom + step);
            int cw = right - left + 1, ch = bottom - top + 1;
            if (cw >= w && ch >= h) return src;                           // nothing to trim

            var cropped = new CroppedBitmap(bgra, new Int32Rect(left, top, cw, ch));
            cropped.Freeze();
            return cropped;
        }
    }
}
