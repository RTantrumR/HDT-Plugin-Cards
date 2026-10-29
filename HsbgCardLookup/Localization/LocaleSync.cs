using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HsbgCardLookup.Config;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// Downloads and caches the language data the plugin does not ship: a card language's snapshot
    /// (/api/cards?locale=&lt;web&gt; — the English payload with the client's names, text and render
    /// paths swapped in) and the site's copy of the vocabulary table. Best-effort like
    /// <see cref="Ui.ArtPack"/>: every failure is a quiet "not now". Files live beside the English
    /// cache — cache\cards.&lt;web&gt;.json, cache\locale-terms.json — with a small ledger
    /// (cache\locale-data.json) that remembers each snapshot's content hash and the English
    /// snapshot it was fetched against. Snapshots are kept per language, so a language used before
    /// costs no second download. Nothing here runs on a language switch: <see cref="CardLanguage"/>
    /// calls in only when the language is actually used.
    /// </summary>
    internal static class LocaleSync
    {
        public enum Outcome { Unchanged, Updated, Unavailable }

        private static string CacheDir => Path.Combine(PluginConfig.DataDir, "cache");
        public static string SnapshotPath(string web) => Path.Combine(CacheDir, "cards." + web + ".json");
        public static string TermsPath => Path.Combine(CacheDir, "locale-terms.json");
        private static string LedgerPath => Path.Combine(CacheDir, "locale-data.json");

        public sealed class Entry
        {
            public string Hash;       // content hash of the cached snapshot ({patch, cards}, as Plugin hashes the English one)
            public string EnHash;     // PluginConfig.LastDataHash at fetch time — which English snapshot this one matches
            public string Patch;
            public string FetchedAt;
        }

        private static readonly object LedgerLock = new object();

        public static Entry Ledger(string web)
        {
            lock (LedgerLock)
            {
                var ledger = LoadLedger();
                return !string.IsNullOrEmpty(web) && ledger.TryGetValue(web, out var e) ? e : null;
            }
        }

        /// <summary>Fetch a language's snapshot unless the cached one is current. Writes the file and
        /// the ledger; the caller applies it to the store (on the UI thread).</summary>
        public static async Task<Outcome> EnsureSnapshotAsync(string web, string englishHash)
        {
            try
            {
                var json = await AssetClient.GetStringAsync(AssetClient.SiteBase + "/api/cards?locale=" + web).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) return Outcome.Unavailable;
                CardsFile file;
                try { file = JsonConvert.DeserializeObject<CardsFile>(json); }
                catch { return Outcome.Unavailable; }
                if (file?.Cards == null || file.Cards.Count == 0) return Outcome.Unavailable;

                string hash = ContentHash(file);
                string path = SnapshotPath(web);
                var known = Ledger(web);
                if (known != null && known.Hash == hash && File.Exists(path))
                {
                    if (known.EnHash != (englishHash ?? "")) Remember(web, hash, englishHash, file.Patch);   // same data, newer English baseline
                    return Outcome.Unchanged;
                }
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(path, json);
                Remember(web, hash, englishHash, file.Patch);
                return Outcome.Updated;
            }
            catch { return Outcome.Unavailable; }
        }

        /// <summary>Fetch the site's vocabulary table if it publishes one (a 404 today) and it differs
        /// from the cached copy. True when the cache changed — reload <see cref="LocaleTerms"/>.</summary>
        public static async Task<bool> EnsureTermsAsync()
        {
            try
            {
                var json = await AssetClient.GetStringAsync(AssetClient.SiteBase + "/plugin/locale-terms.json?_=" + DateTime.UtcNow.Ticks).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json) || !LocaleTerms.LooksValid(json)) return false;
                string path = TermsPath;
                if (File.Exists(path) && File.ReadAllText(path) == json) return false;
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>The hash Plugin computes for the English snapshot: {patch, cards} re-serialized,
        /// so the API's volatile fetchedAt cannot churn it.</summary>
        public static string ContentHash(CardsFile file)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(file))));
        }

        private static void Remember(string web, string hash, string enHash, string patch)
        {
            lock (LedgerLock)
            {
                var ledger = LoadLedger();
                ledger[web] = new Entry { Hash = hash, EnHash = enHash ?? "", Patch = patch ?? "", FetchedAt = DateTime.UtcNow.ToString("o") };
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(LedgerPath, JsonConvert.SerializeObject(ledger, Formatting.Indented));
                }
                catch { }
            }
        }

        private static Dictionary<string, Entry> LoadLedger()
        {
            try
            {
                if (File.Exists(LedgerPath))
                    return JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(LedgerPath)) ?? new Dictionary<string, Entry>();
            }
            catch { }
            return new Dictionary<string, Entry>();
        }
    }
}
