using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HsbgCardLookup.Config;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Ui
{
    /// <summary>
    /// Keeps the local art cache in sync with the website. First install: stream + unpack the full
    /// bulk zip (all-cards.zip, ~200MB). Updates: diff the per-card hash manifest
    /// (all-cards-hashes.json) vs the last-applied hashes and re-fetch only changed cards from the CDN.
    /// Falls back to whole-zip-on-aggregate-change if the manifest isn't published.
    /// </summary>
    internal static class ArtPack
    {
        private static readonly string HashesPath = Path.Combine(PluginConfig.DataDir, "art-hashes.json");

        /// <returns>true if art changed on disk — caller should drop caches + refresh.</returns>
        public static async Task<bool> EnsureAsync(CardStore store, PluginConfig config)
        {
            try
            {
                var manifest = await AssetClient.GetJsonAsync<HashManifest>(
                    AssetClient.SiteBase + "/all-cards-hashes.json?_=" + DateTime.UtcNow.Ticks).ConfigureAwait(false);

                if (manifest?.Cards == null || manifest.Cards.Count == 0)
                    return await EnsureFullPackByAggregate(store, config).ConfigureAwait(false);

                // Early-out: aggregate unchanged. Seed the per-card baseline if an old full-pack
                // install never had one, so the next change goes incremental (not a full re-pull).
                if (!string.IsNullOrEmpty(manifest.Hash) && manifest.Hash == config.ArtPackHash)
                {
                    if (LoadLocalHashes().Count == 0 && HasAnyArt())
                        SaveLocalHashes(manifest.Cards);
                    return false;
                }

                var local = LoadLocalHashes();
                bool haveArt = local.Count > 0 && HasAnyArt();

                if (!haveArt)   // first install (or wiped cache): full pack, then adopt the manifest
                {
                    if (!await DownloadAndUnpackFullPack(store).ConfigureAwait(false)) return false;
                    SaveLocalHashes(manifest.Cards);
                    config.ArtPackHash = manifest.Hash ?? "";
                    config.Save();
                    CardArt.ClearMemory();
                    return true;
                }

                // Incremental: cards whose art hash changed or is new.
                var changed = manifest.Cards
                    .Where(kv => !local.TryGetValue(kv.Key, out var h) || h != kv.Value)
                    .Select(kv => kv.Key).ToList();
                if (changed.Count == 0)
                {
                    config.ArtPackHash = manifest.Hash ?? "";
                    config.Save();
                    return false;
                }

                int ok = await FetchChanged(changed, store, local, manifest.Cards).ConfigureAwait(false);
                SaveLocalHashes(local);                 // persist successes; failed ones retry next launch
                if (ok == changed.Count)                // adopt aggregate only when fully caught up
                {
                    config.ArtPackHash = manifest.Hash ?? "";
                    config.Save();
                }
                if (ok > 0) { CardArt.ClearMemory(); return true; }
                return false;
            }
            catch { return false; }
        }

        // Fetch each changed card's base (+ golden) art; mark `local` for fully-succeeded cards.
        private static async Task<int> FetchChanged(List<string> changedIds, CardStore store,
            Dictionary<string, string> local, Dictionary<string, string> manifest)
        {
            var tasks = changedIds.Select(async idStr =>
            {
                if (!int.TryParse(idStr, out int id) || !store.ById.TryGetValue(id, out var card))
                    return (idStr, false);
                bool okBase = await CardArt.FetchToDiskAsync(card, false).ConfigureAwait(false);
                bool okGold = string.IsNullOrEmpty(card.ImageGold)
                    ? true
                    : await CardArt.FetchToDiskAsync(card, true).ConfigureAwait(false);
                return (idStr, okBase && okGold);
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            int ok = 0;
            foreach (var (idStr, success) in results)
                if (success) { local[idStr] = manifest[idStr]; ok++; }
            return ok;
        }

        // Full-pack path (first install + no-manifest fallback): re-pull the whole zip when the
        // single aggregate hash (all-cards.json) changes.
        private static async Task<bool> EnsureFullPackByAggregate(CardStore store, PluginConfig config)
        {
            var agg = await AssetClient.GetJsonAsync<HashManifest>(
                AssetClient.SiteBase + "/all-cards.json?_=" + DateTime.UtcNow.Ticks).ConfigureAwait(false);
            if (agg == null || string.IsNullOrEmpty(agg.Hash)) return false;
            if (agg.Hash == config.ArtPackHash) return false;

            if (!await DownloadAndUnpackFullPack(store).ConfigureAwait(false)) return false;
            config.ArtPackHash = agg.Hash;
            config.Save();
            CardArt.ClearMemory();
            return true;
        }

        private static async Task<bool> DownloadAndUnpackFullPack(CardStore store)
        {
            Directory.CreateDirectory(CardArt.CacheDir);
            var zipPath = Path.Combine(CardArt.CacheDir, "all-cards.zip");
            if (!await AssetClient.StreamToFileAsync(AssetClient.SiteBase + "/all-cards.zip", zipPath).ConfigureAwait(false))
                return false;
            int written = Unpack(zipPath, store);
            try { File.Delete(zipPath); } catch { }
            return written > 0;
        }

        private static int Unpack(string zipPath, CardStore store)
        {
            var byId = store.ById ?? new Dictionary<int, BgCard>();
            var bySlug = new Dictionary<string, BgCard>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in store.All)
                if (!string.IsNullOrEmpty(c.Slug) && !bySlug.ContainsKey(c.Slug)) bySlug[c.Slug] = c;

            int written = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (!entry.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) continue;

                    string name = Path.GetFileNameWithoutExtension(entry.Name);
                    bool golden = false;
                    if (name.EndsWith("-golden", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_golden", StringComparison.OrdinalIgnoreCase))
                    {
                        golden = true;
                        name = name.Substring(0, name.Length - "-golden".Length);
                    }

                    int id = ResolveId(name, byId, bySlug);
                    if (id < 0) continue;

                    try { entry.ExtractToFile(CardArt.FullDiskPath(id, golden), overwrite: true); written++; }
                    catch { }
                }
            }
            return written;
        }

        private static int ResolveId(string name, Dictionary<int, BgCard> byId, Dictionary<string, BgCard> bySlug)
        {
            int dash = name.IndexOf('-');
            if (dash > 0 && int.TryParse(name.Substring(0, dash), out int pid) && byId.ContainsKey(pid))
                return pid;
            return bySlug.TryGetValue(name, out var card) ? card.Id : -1;
        }

        private static bool HasAnyArt()
        {
            try
            {
                return Directory.Exists(CardArt.CacheDir) &&
                       Directory.EnumerateFiles(CardArt.CacheDir, "*.webp").Any();
            }
            catch { return false; }
        }

        private static Dictionary<string, string> LoadLocalHashes()
        {
            try
            {
                if (File.Exists(HashesPath))
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(HashesPath))
                           ?? new Dictionary<string, string>();
            }
            catch { }
            return new Dictionary<string, string>();
        }

        private static void SaveLocalHashes(Dictionary<string, string> hashes)
        {
            try
            {
                Directory.CreateDirectory(PluginConfig.DataDir);
                File.WriteAllText(HashesPath, JsonConvert.SerializeObject(hashes));
            }
            catch { }
        }

        private sealed class HashManifest
        {
            [JsonProperty("hash")] public string Hash { get; set; }
            [JsonProperty("cards")] public Dictionary<string, string> Cards { get; set; }
        }
    }
}
