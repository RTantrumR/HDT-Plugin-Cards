using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HsbgCardLookup.Net
{
    /// <summary>
    /// Shared HTTP: one reused <see cref="HttpClient"/> (TLS 1.2 forced, User-Agent set, follows
    /// redirects). All calls are best-effort — they return null/false on failure rather than throw.
    /// </summary>
    internal static class AssetClient
    {
        public const string SiteBase = "https://hsbg.cards";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "HsbgCardLookup/" + (typeof(AssetClient).Assembly.GetName().Version?.ToString() ?? "0"));
            return client;
        }

        /// <summary>GET a URL as a string, or null on any failure.</summary>
        public static async Task<string> GetStringAsync(string url)
        {
            try
            {
                using (var resp = await Http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch { return null; }
        }

        /// <summary>GET a URL and deserialize the JSON body to T, or default(T) on any failure.</summary>
        public static async Task<T> GetJsonAsync<T>(string url)
        {
            var json = await GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return default(T);
            try { return JsonConvert.DeserializeObject<T>(json); }
            catch { return default(T); }
        }

        /// <summary>GET a URL as raw bytes, or null on any failure.</summary>
        public static async Task<byte[]> GetBytesAsync(string url)
        {
            try
            {
                using (var resp = await Http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch { return null; }
        }

        /// <summary>Stream a (potentially large) URL to a file without buffering it all in memory.
        /// Atomic via temp file + move. Used for the ~200MB art pack.</summary>
        public static async Task<bool> StreamToFileAsync(string url, string destPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                var tmp = destPath + ".part";
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                        await src.CopyToAsync(fs).ConfigureAwait(false);
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Download a URL straight to a file (atomic via a temp file + move). Returns
        /// true on success. Used for release zips and the card-data snapshot.</summary>
        public static async Task<bool> DownloadToFileAsync(string url, string destPath)
        {
            try
            {
                var bytes = await GetBytesAsync(url).ConfigureAwait(false);
                if (bytes == null) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                var tmp = destPath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                return true;
            }
            catch { return false; }
        }
    }
}
