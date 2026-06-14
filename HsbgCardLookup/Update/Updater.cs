using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HsbgCardLookup.Config;
using HsbgCardLookup.Net;

namespace HsbgCardLookup.Update
{
    /// <summary>A message to surface to the user about an update (success / "restart to apply" /
    /// failed-with-link).</summary>
    internal sealed class UpdateNotice
    {
        public string Message;
        public bool IsError;
        public string Url;          // non-null => show a clickable link (manual install fallback)
        public bool RestartReady;   // true => an update is staged; restarting HDT applies it
        public UpdateNotice(string message, bool isError = false, string url = null)
        { Message = message; IsError = isError; Url = url; }
    }

    /// <summary>
    /// Self-update via GitHub Releases. Across restarts: download the newer release zip → extract to
    /// a staging folder → copy over the plugin folder (HDT shadow-copies to load, so source files are
    /// normally unlocked) → write a pending marker. Next launch: if the running version matches the
    /// pending one, show "Updated to N + changelog"; else retry / show a manual-install link.
    /// Best-effort — offline / missing repo / any failure = no update.
    /// </summary>
    internal static class Updater
    {
        public const string GitHubRepo = "RTantrumR/HDT-Plugin-Cards";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private static string StagingDir => Path.Combine(PluginConfig.DataDir, "update-staging");
        private static string MarkerPath => Path.Combine(PluginConfig.DataDir, "update-pending.json");
        private static string ReleasesUrl => "https://github.com/" + GitHubRepo + "/releases/latest";
        private static string ApiLatest => "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";

        /// <param name="manual">User-triggered ("Check for updates" button): skip the 6h throttle and
        /// always return a notice — including "you're up to date" / "couldn't check" — so the UI can
        /// give feedback. The background check (manual=false) stays silent in those cases (returns null).</param>
        public static async Task<UpdateNotice> RunAsync(Version current, string pluginDir, PluginConfig config, bool manual = false)
        {
            try
            {
                // 1. Resolve any update staged by a previous session.
                var pending = ReadMarker();
                if (pending != null)
                {
                    var pv = ParseVersion(pending.Version);
                    if (pv != null && current >= pv)
                    {
                        ClearMarker();
                        SafeDeleteDir(StagingDir);
                        return new UpdateNotice($"Updated to version {pending.Version}\nChangelog:\n"
                            + (string.IsNullOrWhiteSpace(pending.Changelog) ? "(no notes)" : pending.Changelog.Trim()));
                    }
                    // Staged but not yet running — try to apply again, then ask for a restart.
                    bool applied = TryApply(pluginDir);
                    return applied
                        ? new UpdateNotice($"Update v{pending.Version} ready — restart HDT to finish.") { RestartReady = true }
                        : new UpdateNotice($"Update v{pending.Version} couldn't be applied automatically. "
                            + "Please install it manually:", true, pending.Url ?? ReleasesUrl);
                }

                // 2. Throttle (background only), then check the latest release.
                if (!manual && DateTime.TryParse(config.LastUpdateCheckUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                    && DateTime.UtcNow - last.ToUniversalTime() < CheckInterval)
                    return null;

                var rel = await AssetClient.GetJsonAsync<GhRelease>(ApiLatest).ConfigureAwait(false);
                config.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o");
                config.Save();
                if (rel == null)
                    return manual
                        ? new UpdateNotice("Couldn't check for updates — you may be offline, or no release "
                            + "has been published yet. You can check manually:", true, ReleasesUrl)
                        : null;

                var v = ParseVersion(rel.TagName);
                if (v == null)
                    return manual ? new UpdateNotice("Couldn't read the latest release info.", true, ReleasesUrl) : null;
                if (v <= current)
                    return manual ? new UpdateNotice($"You're on the latest version (v{current}).") : null;

                var asset = rel.Assets?.FirstOrDefault(
                    a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
                    return new UpdateNotice($"Version {v} is available. Download it here:", true, ReleasesUrl);

                if (!await DownloadAndExtract(asset.DownloadUrl).ConfigureAwait(false))
                    return new UpdateNotice($"Version {v} is available but the download failed. "
                        + "Install it manually:", true, ReleasesUrl);

                WriteMarker(new Pending { Version = v.ToString(), Changelog = rel.Body, Url = ReleasesUrl });
                bool ok = TryApply(pluginDir);
                return ok
                    ? new UpdateNotice($"Update v{v} downloaded — restart HDT to apply.") { RestartReady = true }
                    : new UpdateNotice($"Update v{v} downloaded but couldn't be applied automatically. "
                        + "Install it manually:", true, ReleasesUrl);
            }
            catch { return null; }
        }

        private static async Task<bool> DownloadAndExtract(string zipUrl)
        {
            try
            {
                SafeDeleteDir(StagingDir);
                Directory.CreateDirectory(StagingDir);
                var zipPath = Path.Combine(StagingDir, "_update.zip");
                if (!await AssetClient.DownloadToFileAsync(zipUrl, zipPath).ConfigureAwait(false))
                    return false;

                var extractDir = Path.Combine(StagingDir, "files");
                SafeDeleteDir(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                try { File.Delete(zipPath); } catch { }
                return File.Exists(ResolveExtractedDll(extractDir));
            }
            catch { return false; }
        }

        /// <summary>Locate the plugin DLL inside the extracted tree, descending through a single
        /// wrapping folder if the zip nested everything under one directory.</summary>
        private static string ResolveExtractedDll(string extractDir)
        {
            var direct = Path.Combine(extractDir, "HsbgCardLookup.dll");
            if (File.Exists(direct)) return direct;
            var hit = Directory.GetFiles(extractDir, "HsbgCardLookup.dll", SearchOption.AllDirectories).FirstOrDefault();
            return hit ?? direct;
        }

        /// <summary>Copy the extracted files over the plugin folder. The running plugin DLL and our
        /// bundled deps (loaded via AssemblyResolve.LoadFrom) are LOCKED while HDT runs, so a plain
        /// in-place overwrite fails — <see cref="CopyOne"/> renames a locked file aside (.old, allowed
        /// even while loaded) and writes the new one, which loads on the next restart. Files already
        /// identical (unchanged deps) are skipped.</summary>
        private static bool TryApply(string pluginDir)
        {
            try
            {
                CleanupOldFiles(pluginDir);   // remove *.old left by a previous update (now unlocked)

                var extractDir = Path.Combine(StagingDir, "files");
                var dll = ResolveExtractedDll(extractDir);
                if (!File.Exists(dll)) return false;
                var srcRoot = Path.GetDirectoryName(dll);

                if (!CopyOne(dll, Path.Combine(pluginDir, "HsbgCardLookup.dll"))) return false;
                foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = src.Substring(srcRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (rel.Equals("HsbgCardLookup.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(pluginDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!CopyOne(src, dest)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // Copy src→dest, tolerating a locked destination (the loaded plugin DLL / bundled deps): if the
        // existing file is already identical, skip it; otherwise rename the locked file aside (.old —
        // Windows permits renaming an in-use module) and write the new one, which takes effect on the
        // next HDT restart. Returns false only if even the rename+copy fails.
        private static bool CopyOne(string src, string dest)
        {
            try { File.Copy(src, dest, true); return true; }
            catch
            {
                try
                {
                    if (File.Exists(dest) && SameFile(src, dest)) return true;   // unchanged, just locked
                    var aside = dest + ".old";
                    try { if (File.Exists(aside)) File.Delete(aside); } catch { }
                    File.Move(dest, aside);
                    File.Copy(src, dest, true);
                    return true;
                }
                catch { return false; }
            }
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a); var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
                const int N = 65536;
                using (var sa = File.OpenRead(a))
                using (var sb = File.OpenRead(b))
                {
                    var ba = new byte[N]; var bb = new byte[N];
                    int ra;
                    while ((ra = sa.Read(ba, 0, N)) > 0)
                    {
                        int off = 0, n;
                        while (off < ra && (n = sb.Read(bb, off, ra - off)) > 0) off += n;
                        if (off != ra) return false;
                        for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // Delete *.old left behind by a prior update (the old locked modules; freed after the restart).
        private static void CleanupOldFiles(string pluginDir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(pluginDir, "*.old", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(s, out var v) ? v : null;
        }

        private static Pending ReadMarker()
        {
            try { return File.Exists(MarkerPath) ? JsonConvert.DeserializeObject<Pending>(File.ReadAllText(MarkerPath)) : null; }
            catch { return null; }
        }

        private static void WriteMarker(Pending p)
        {
            try { Directory.CreateDirectory(PluginConfig.DataDir); File.WriteAllText(MarkerPath, JsonConvert.SerializeObject(p)); }
            catch { }
        }

        private static void ClearMarker() { try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { } }

        private static void SafeDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private sealed class Pending
        {
            public string Version;
            public string Changelog;
            public string Url;
        }

        private sealed class GhRelease
        {
            [JsonProperty("tag_name")] public string TagName { get; set; }
            [JsonProperty("body")] public string Body { get; set; }
            [JsonProperty("assets")] public List<GhAsset> Assets { get; set; }
        }

        private sealed class GhAsset
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("browser_download_url")] public string DownloadUrl { get; set; }
        }
    }
}
