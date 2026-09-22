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

                // No aggregate early-out: the diff below is a few thousand dictionary lookups and
                // File.Exists calls, and it is the only thing that notices a file missing on disk
                // while the hashes say "applied". Seed the per-card baseline if an old full-pack
                // install never had one, so it goes incremental rather than re-pulling the pack.
                var local = LoadLocalHashes();
                if (local.Count == 0 && HasAnyArt())
                {
                    local = new Dictionary<string, string>(manifest.Cards);
                    SaveLocalHashes(local);
                }
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

                // Incremental: cards whose art hash changed or is new — among the cards the store
                // actually has. The manifest lists every card the site ever rendered (~2.9k), the
                // API serves only the current pool (~1.4k), and nothing outside the store can be
                // fetched or shown; counting it would make "fully caught up" unreachable.
                // A hash marked applied is not proof the file is on disk: a full-pack install adopts
                // the whole manifest, and the pack has lacked files the manifest lists (hero
                // goldens, 2026-09-23: 81 such cards). So a card whose file is missing counts as
                // changed too — the disk, not the hash file, is the truth.
                var known = store.ById ?? new Dictionary<int, BgCard>();
                var changed = manifest.Cards
                    .Where(kv => int.TryParse(kv.Key, out int id) && known.ContainsKey(id))
                    .Where(kv => !local.TryGetValue(kv.Key, out var h) || h != kv.Value
                                 || !OnDisk(known[int.Parse(kv.Key)]))
                    .Select(kv => kv.Key).ToList();
                if (changed.Count == 0)
                {
                    config.ArtPackHash = manifest.Hash ?? "";
                    config.Save();
                    return false;
                }

                var (ok, written) = await FetchChanged(changed, store, local, manifest.Cards).ConfigureAwait(false);
                SaveLocalHashes(local);                 // persist successes; failed ones retry next launch
                if (ok == changed.Count)                // adopt aggregate only when fully caught up
                {
                    config.ArtPackHash = manifest.Hash ?? "";
                    config.Save();
                }
                // "Updated" means a file landed on disk — a card resolved without a download (dead
                // golden, base already present) must not drop the decoded cache or log an update.
                if (written > 0) { CardArt.ClearMemory(); return true; }
                return false;
            }
            catch { return false; }
        }

        // Base art on disk, plus the golden when the card claims one. A golden the CDN 404s on is
        // never written, so such a card re-enters `changed` each launch — costing one golden GET
        // (a 404) per launch per dead golden, since an unchanged hash skips files already on disk.
        private static bool OnDisk(BgCard c) =>
            File.Exists(CardArt.FullDiskPath(c.Id, false))
            && (string.IsNullOrEmpty(c.ImageGold) || File.Exists(CardArt.FullDiskPath(c.Id, true)));

        // Fetch each changed card's base (+ golden) art; mark `local` for fully-succeeded cards.
        // When the hash is unchanged the card is here only because a file is missing, so files
        // already on disk are kept, not re-downloaded. A golden that 404s counts as done: the site
        // lists goldens it never rendered (spells, a hero sharing its name with a minion) and a 404
        // is a definitive answer, not a transient failure. A missing base is still a failure: that
        // art may yet appear, and the card shows blank.
        private static async Task<(int ok, int written)> FetchChanged(List<string> changedIds, CardStore store,
            Dictionary<string, string> local, Dictionary<string, string> manifest)
        {
            var tasks = changedIds.Select(async idStr =>
            {
                if (!int.TryParse(idStr, out int id) || !store.ById.TryGetValue(id, out var card))
                    return (idStr, success: false, wrote: false);
                bool hashSame = local.TryGetValue(idStr, out var lh) && lh == manifest[idStr];
                bool wrote = false;
                bool okBase = hashSame && File.Exists(CardArt.FullDiskPath(id, false));
                if (!okBase)
                {
                    okBase = await CardArt.FetchToDiskAsync(card, false).ConfigureAwait(false) == CardArt.FetchResult.Ok;
                    wrote |= okBase;
                }
                bool okGold = string.IsNullOrEmpty(card.ImageGold)
                    || (hashSame && File.Exists(CardArt.FullDiskPath(id, true)));
                if (!okGold)
                {
                    var g = await CardArt.FetchToDiskAsync(card, true).ConfigureAwait(false);
                    okGold = g != CardArt.FetchResult.Failed;
                    wrote |= g == CardArt.FetchResult.Ok;
                }
                return (idStr, success: okBase && okGold, wrote);
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            int ok = 0, written = 0;
            foreach (var (idStr, success, wrote) in results)
            {
                if (success) { local[idStr] = manifest[idStr]; ok++; }
                if (wrote) written++;
            }
            return (ok, written);
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
