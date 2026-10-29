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
using HsbgCardLookup.Localization;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Resolves card art to frozen <see cref="BitmapSource"/>s. All art is the full WebP on disk (one
    /// file per card, from <see cref="ArtPack"/> or a lazy per-card fetch); the grid decodes it down,
    /// the detail pane decodes native. Order: memory cache → local dev PNG → disk → CDN download.
    ///
    /// Card language: when the active language has a render for the card, the site's snapshot points
    /// the card's image at /cards/production/locales/&lt;web&gt;/… (BgCard.LocalImage); that render is
    /// fetched per card as it is viewed and cached under art-cache\locales\&lt;web&gt;\, apart from the
    /// English files the art pack syncs. Memory keys carry the language, so a switch never serves a
    /// stale bitmap (CardLanguage clears the memory cache). A locale render that is not there falls
    /// back to the English one; a missing golden falls back to the base render.
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

        /// <summary>The English render's file (what the art pack fills).</summary>
        internal static string FullDiskPath(int id, bool golden) =>
            Path.Combine(CacheDir, id + "_full" + (golden ? "_g" : "") + ".webp");

        /// <summary>A card language's render, kept apart from the English pack (which ArtPack diffs by id).</summary>
        internal static string LocaleDiskPath(string web, int id, bool golden) =>
            Path.Combine(CacheDir, "locales", web, id + "_full" + (golden ? "_g" : "") + ".webp");

        private static string DiskPath(int id, bool golden, string web) =>
            web == null ? FullDiskPath(id, golden) : LocaleDiskPath(web, id, golden);

        // Drop decoded bitmaps so re-decodes pick up freshly-swapped disk files (after a pack update)
        // or the other language's renders (after a language switch).
        internal static void ClearMemory() => Mem.Clear();

        internal enum FetchResult { Ok, NotFound, Failed }

        /// <summary>Download one card's ENGLISH full WebP straight to the disk cache (no decode), capped.
        /// Used by <see cref="ArtPack"/> incremental updates, which sync the English pack only.
        /// <see cref="FetchResult.NotFound"/> is a definitive 404 (no such file on the CDN); everything
        /// else that isn't Ok is transient.</summary>
        internal static async Task<FetchResult> FetchToDiskAsync(BgCard c, bool golden)
        {
            try
            {
                string url = CdnUrl(Src(c, golden, localized: false));
                if (url == null) return FetchResult.Failed;
                byte[] bytes; int status;
                await Gate.WaitAsync().ConfigureAwait(false);
                try { (bytes, status) = await AssetClient.GetBytesWithStatusAsync(url).ConfigureAwait(false); }
                finally { Gate.Release(); }
                if (status == 404) return FetchResult.NotFound;
                if (bytes == null || bytes.Length == 0) return FetchResult.Failed;
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(FullDiskPath(c.Id, golden), bytes);
                return FetchResult.Ok;
            }
            catch { return FetchResult.Failed; }
        }

        // The image path to show: the card language's render when the site has one, else English.
        // Golden falls back to the base image when the card has no golden render; a card whose base
        // render is localized but whose golden is not shows the English golden (the site's own rule).
        private static string Src(BgCard c, bool golden, bool localized)
        {
            if (golden && !string.IsNullOrEmpty(c.ImageGold))
                return localized ? (c.LocalImageGold ?? c.ImageGold) : c.ImageGold;
            return localized ? (c.LocalImage ?? c.Image) : c.Image;
        }

        // Mem key includes decode width (grid downscaled vs detail native share one disk file) and the
        // render's language.
        private static string Key(BgCard c, bool golden, int decode) =>
            c.Id + "|" + (golden ? "g" : "n") + "|" + decode + "|" + (Locales.WebFromRenderPath(Src(c, golden, true)) ?? Locales.English);

        // Direct static-CDN thumb URL from an image path. Since the site retired PNGs (2026-09-03)
        // the API serves the thumb path itself (…/thumbs/full/….webp, or …/locales/<web>/….webp) —
        // use it as is. Older snapshots carry pngs/full → thumbs/full, .png → .webp. Hits the CDN,
        // not the rate-limited /api/v1 image route.
        private const string PngPrefix = "/cards/production/pngs/full/";

        private static string CdnUrl(string src)
        {
            if (string.IsNullOrEmpty(src)) return null;
            if (src.EndsWith(".webp")) return AssetClient.SiteBase + src;
            if (!src.EndsWith(".png")) return null;
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
                // The requested render in the card language (disk, else CDN) …
                string src = Src(c, golden, localized: true);
                string web = Locales.WebFromRenderPath(src);
                byte[] bytes = await ReadOrFetch(c.Id, golden, src, web).ConfigureAwait(false);

                // … else the English render of the same variant (a locale render that is not there) …
                if (Empty(bytes) && web != null)
                    bytes = await ReadOrFetch(c.Id, golden, Src(c, golden, localized: false), null).ConfigureAwait(false);

                // … else, for golden, the base render (same language first, then English).
                if (Empty(bytes) && golden)
                {
                    string baseSrc = Src(c, false, localized: true);
                    string baseWeb = Locales.WebFromRenderPath(baseSrc);
                    bytes = await ReadOrFetch(c.Id, false, baseSrc, baseWeb).ConfigureAwait(false);
                    if (Empty(bytes) && baseWeb != null)
                        bytes = await ReadOrFetch(c.Id, false, Src(c, false, localized: false), null).ConfigureAwait(false);
                }
                if (Empty(bytes)) return null;

                var bmp = DecodeAndTrim(bytes, decode);
                if (bmp != null) Mem[key] = bmp;
                return bmp;
            }
            catch { return null; }
            finally { InFlight.TryRemove(key, out _); }
        }

        private static bool Empty(byte[] b) => b == null || b.Length == 0;

        // One variant's bytes: its file on disk, else downloaded from the CDN and stored for next time.
        private static async Task<byte[]> ReadOrFetch(int id, bool golden, string src, string web)
        {
            var disk = DiskPath(id, golden, web);
            if (File.Exists(disk))
            {
                try
                {
                    var onDisk = File.ReadAllBytes(disk);
                    if (!Empty(onDisk)) return onDisk;
                }
                catch { }
            }

            string url = CdnUrl(src);
            if (url == null) return null;
            byte[] bytes;
            await Gate.WaitAsync().ConfigureAwait(false);
            try { bytes = await AssetClient.GetBytesAsync(url).ConfigureAwait(false); }
            finally { Gate.Release(); }
            if (Empty(bytes)) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(disk));
                File.WriteAllBytes(disk, bytes);
            }
            catch { }
            return bytes;
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
