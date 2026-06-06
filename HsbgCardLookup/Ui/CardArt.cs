using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Resolves card art to frozen <see cref="BitmapSource"/>s. All art is the full WebP on disk (one
    /// file per card, from <see cref="ArtPack"/> or a lazy per-card fetch); the grid decodes it down,
    /// the detail pane decodes native. Order: memory cache → local dev PNG → disk → CDN download.
    /// </summary>
    internal static class CardArt
    {
        private static readonly ConcurrentDictionary<string, BitmapSource> Mem =
            new ConcurrentDictionary<string, BitmapSource>();
        private static readonly ConcurrentDictionary<string, Task<BitmapSource>> InFlight =
            new ConcurrentDictionary<string, Task<BitmapSource>>();
        // Cap concurrent downloads (matches the website's zip export).
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(6);

        // Card-art cache dir. Settable (Plugin.OnLoad applies PluginConfig.ArtCacheDir) so users can
        // relocate the ~200MB off the system drive. Default = DataDir\art-cache.
        internal static readonly string DefaultCacheDir = Path.Combine(PluginConfig.DataDir, "art-cache");
        internal static string CacheDir = DefaultCacheDir;

        // Dev-only opt-in: use local public/ PNGs instead of pack/CDN WebP. Off by default.
        internal static bool UseLocalArt = false;

        internal static string FullDiskPath(int id, bool golden) =>
            Path.Combine(CacheDir, id + "_full" + (golden ? "_g" : "") + ".webp");

        // Drop decoded bitmaps so re-decodes pick up freshly-swapped disk files (after a pack update).
        internal static void ClearMemory() => Mem.Clear();

        /// <summary>Download one card's full WebP straight to the disk cache (no decode), capped.
        /// Used by <see cref="ArtPack"/> incremental updates.</summary>
        internal static async Task<bool> FetchToDiskAsync(BgCard c, bool golden)
        {
            try
            {
                string url = CdnUrl(c, golden);
                if (url == null) return false;
                byte[] bytes;
                await Gate.WaitAsync().ConfigureAwait(false);
                try { bytes = await AssetClient.GetBytesAsync(url).ConfigureAwait(false); }
                finally { Gate.Release(); }
                if (bytes == null || bytes.Length == 0) return false;
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(FullDiskPath(c.Id, golden), bytes);
                return true;
            }
            catch { return false; }
        }

        // Mem key includes decode width (grid downscaled vs detail native share one disk file).
        private static string Key(BgCard c, bool golden, int decode) =>
            c.Id + "|" + (golden ? "g" : "n") + "|" + decode;

        // Direct static-CDN thumb URL from the card's image path (pngs/full → thumbs/full, .png →
        // .webp). Hits the CDN, not the rate-limited /api/v1 image route. golden falls back to base.
        private const string PngPrefix = "/cards/production/pngs/full/";

        private static string CdnUrl(BgCard c, bool golden)
        {
            string src = golden && !string.IsNullOrEmpty(c.ImageGold) ? c.ImageGold : c.Image;
            if (string.IsNullOrEmpty(src) || !src.EndsWith(".png")) return null;
            string body = src.Substring(0, src.Length - 4);   // strip .png
            string thumb = body.StartsWith(PngPrefix)
                ? "/cards/production/thumbs/full/" + body.Substring(PngPrefix.Length) + ".webp"
                : body + ".webp";
            return AssetClient.SiteBase + thumb;
        }

        /// <summary>UI-thread resolution: memory → local dev PNG → disk. Returns null when a network
        /// fetch is needed (use <see cref="LoadAsync"/>). decode &gt; 0 downscales; 0 = native.</summary>
        public static BitmapSource GetSync(BgCard c, bool golden, int decode)
        {
            if (c == null) return null;
            var key = Key(c, golden, decode);
            if (Mem.TryGetValue(key, out var hit)) return hit;

            if (UseLocalArt)
            {
                var localPng = CardStore.ResolveImagePath(c, golden) ?? CardStore.ResolveImagePath(c, false);
                if (localPng != null)
                {
                    var png = ImageCache.LoadTrimmed(localPng, decode);
                    if (png != null) { Mem[key] = png; return png; }
                }
            }

            // Disk read + decode are off-thread (LoadAsync) — doing them here per cell lagged scrolling.
            return null;
        }

        /// <summary>Async resolution: memory hit, else off-thread disk read or CDN download (deduped,
        /// capped), decode (+ downscale), cache. The frozen result is safe to assign on the UI thread.</summary>
        public static Task<BitmapSource> LoadAsync(BgCard c, bool golden, int decode)
        {
            if (c == null) return Task.FromResult<BitmapSource>(null);
            var key = Key(c, golden, decode);
            if (Mem.TryGetValue(key, out var hit)) return Task.FromResult(hit);
            return InFlight.GetOrAdd(key, _ => Task.Run(() => Resolve(c, golden, decode, key)));
        }

        private static async Task<BitmapSource> Resolve(BgCard c, bool golden, int decode, string key)
        {
            try
            {
                byte[] bytes = null;

                var disk = FullDiskPath(c.Id, golden);
                if (!File.Exists(disk) && golden) disk = FullDiskPath(c.Id, false);   // no golden → base
                if (File.Exists(disk))
                {
                    try { bytes = File.ReadAllBytes(disk); } catch { bytes = null; }
                }

                if (bytes == null || bytes.Length == 0)   // not cached → download + store
                {
                    string url = CdnUrl(c, golden);
                    if (url == null) return null;
                    await Gate.WaitAsync().ConfigureAwait(false);
                    try { bytes = await AssetClient.GetBytesAsync(url).ConfigureAwait(false); }
                    finally { Gate.Release(); }
                    if (bytes == null || bytes.Length == 0) return null;
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllBytes(FullDiskPath(c.Id, golden), bytes);
                    }
                    catch { }
                }

                var bmp = DecodeAndTrim(bytes, decode);
                if (bmp != null) Mem[key] = bmp;
                return bmp;
            }
            catch { return null; }
            finally { InFlight.TryRemove(key, out _); }
        }

        private static BitmapSource DecodeAndTrim(byte[] webp, int decode)
        {
            var bmp = WebpDecoder.Decode(webp, decode);
            if (bmp == null) return null;
            try { return ImageCache.Trim(bmp); }
            catch { return bmp; }
        }
    }

    /// <summary>
    /// Virtualization-safe attached behavior: binds card art into an <see cref="Image"/> async. Set
    /// <see cref="CardProperty"/> (+ <see cref="DecodeProperty"/> = thumbnail width). A recycled cell
    /// restarts its load; a late download is dropped if the element moved to a different card.
    /// </summary>
    internal static class ArtImage
    {
        public static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
            "Card", typeof(BgCard), typeof(ArtImage), new PropertyMetadata(null, OnCardChanged));
        public static void SetCard(DependencyObject d, BgCard v) => d.SetValue(CardProperty, v);
        public static BgCard GetCard(DependencyObject d) => (BgCard)d.GetValue(CardProperty);

        public static readonly DependencyProperty DecodeProperty = DependencyProperty.RegisterAttached(
            "Decode", typeof(int), typeof(ArtImage), new PropertyMetadata(256));
        public static void SetDecode(DependencyObject d, int v) => d.SetValue(DecodeProperty, v);
        public static int GetDecode(DependencyObject d) => (int)d.GetValue(DecodeProperty);

        private static void OnCardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var img = d as Image;
            if (img == null) return;
            var card = e.NewValue as BgCard;
            img.Source = null;
            if (card == null) return;

            int decode = GetDecode(img);
            var sync = CardArt.GetSync(card, false, decode);
            if (sync != null) { img.Source = sync; return; }

            var token = card;
            CardArt.LoadAsync(card, false, decode).ContinueWith(t =>
            {
                var bmp = t.Result;
                if (bmp == null) return;
                img.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(GetCard(img), token)) img.Source = bmp;
                }));
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }
}
