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
    /// <summary>A message to surface to the user about an update: a newer version found (offer
    /// Download/Skip), "downloaded — restart to apply", a plain status ("up to date" / "couldn't
    /// check"), or an error with a manual-install link.</summary>
    internal sealed class UpdateNotice
    {
        public string Message;
        public bool IsError;
        public string Url;                 // releases page — manual-install fallback, or "read more"
        public string LinkLabel;           // custom text for Url's link, instead of the raw URL
        public bool RestartReady;          // true => staged; restarting HDT applies it
        public bool AvailableForDownload;  // true => a newer version was found, not yet downloaded
        public string AvailableVersion;    // set when AvailableForDownload
        public string DownloadUrl;         // the release zip asset URL, for DownloadAsync
        public UpdateNotice(string message, bool isError = false, string url = null)
        { Message = message; IsError = isError; Url = url; }
    }

    /// <summary>
    /// Self-update via GitHub Releases, gated on explicit user consent. Checking never downloads —
    /// finding a newer version returns an <see cref="UpdateNotice"/> with
    /// <see cref="UpdateNotice.AvailableForDownload"/> set, and the UI offers Download/Skip. Only
    /// <see cref="DownloadAsync"/> (fired by the user's "Download" click) fetches the zip and extracts
    /// it to a staging folder + writes a pending marker. The actual file swap never touches the live,
    /// loaded plugin — it happens in the relauncher's PowerShell script (<see cref="Plugin.RestartHost"/>)
    /// only after HDT has fully exited, via <see cref="ResolveStagedSourceRoot"/>. Next launch: if the
    /// running version matches the pending one, show "Updated to vN" with a link to the release notes
    /// (never the raw release body — that's Markdown, and none of our banners render it); if it's still staged
    /// (the user hasn't restarted through our prompt yet), re-offer the restart with no re-download.
    /// Best-effort — offline / missing repo / any failure = no update.
    /// </summary>
    internal static class Updater
    {
        public const string GitHubRepo = "RTantrumR/HDT-Plugin-Cards";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private static string StagingDir => Path.Combine(PluginConfig.DataDir, "update-staging");
        internal static string StagedFilesDir => Path.Combine(StagingDir, "files");
        private static string MarkerPath => Path.Combine(PluginConfig.DataDir, "update-pending.json");
        private static string ReleasesUrl => "https://github.com/" + GitHubRepo + "/releases/latest";
        private static string ApiLatest => "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";

        /// <param name="manual">User-triggered ("Check for updates" button): skip the 6h throttle and
        /// always return a notice — including "you're up to date" / "couldn't check" — so the UI can
        /// give feedback. The background check (manual=false) stays silent in those cases (returns null),
        /// and also stays silent about a version the user already chose to skip.</param>
        public static async Task<UpdateNotice> RunAsync(Version current, PluginConfig config, bool manual = false)
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
                        return new UpdateNotice($"Updated to version {pending.Version}.", false, pending.Url ?? ReleasesUrl)
                            { LinkLabel = "View release notes ↗" };
                    }
                    // Already downloaded, just not applied yet (the user hasn't restarted through our
                    // prompt). No re-download — the relauncher applies what's already staged.
                    return new UpdateNotice($"Update v{pending.Version} downloaded — restart HDT to apply.")
                        { RestartReady = true };
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

                if (!manual)
                {
                    var skipped = ParseVersion(config.SkippedUpdateVersion);
                    if (skipped != null && v <= skipped) return null;
                }

                var asset = rel.Assets?.FirstOrDefault(
                    a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
                    return new UpdateNotice($"Version {v} is available. Download it here:", true, ReleasesUrl);

                return new UpdateNotice($"Version {v} is available.")
                {
                    AvailableForDownload = true,
                    AvailableVersion = v.ToString(),
                    DownloadUrl = asset.DownloadUrl,
                    Url = ReleasesUrl,
                };
            }
            catch { return null; }
        }

        /// <summary>Fetch + extract the release the last <see cref="RunAsync"/> found (fired by the
        /// user's "Download" click), reporting progress 0..1 (or -1 once, if the server sent no
        /// Content-Length). Stages the files and writes the pending marker — never touches the live
        /// plugin folder; that happens in the relauncher after HDT exits. Returns RestartReady on
        /// success, or an error notice with the manual-install link on failure.</summary>
        public static async Task<UpdateNotice> DownloadAsync(UpdateNotice available, IProgress<double> progress)
        {
            try
            {
                SafeDeleteDir(StagingDir);
                Directory.CreateDirectory(StagingDir);
                var zipPath = Path.Combine(StagingDir, "_update.zip");
                if (!await AssetClient.DownloadToFileAsync(available.DownloadUrl, zipPath, progress).ConfigureAwait(false))
                    return new UpdateNotice($"Download failed. Install v{available.AvailableVersion} manually:",
                        true, ReleasesUrl);

                var extractDir = StagedFilesDir;
                SafeDeleteDir(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                try { File.Delete(zipPath); } catch { }
                if (!File.Exists(ResolveExtractedDll(extractDir)))
                    return new UpdateNotice($"The downloaded update looked incomplete. Install v{available.AvailableVersion} manually:",
                        true, ReleasesUrl);

                WriteMarker(new Pending { Version = available.AvailableVersion, Url = ReleasesUrl });
                return new UpdateNotice($"Update v{available.AvailableVersion} downloaded — restart HDT to apply.")
                    { RestartReady = true };
            }
            catch
            {
                return new UpdateNotice($"Download failed. Install v{available.AvailableVersion} manually:", true, ReleasesUrl);
            }
        }

        /// <summary>Remember a version the user explicitly chose not to install — background checks
        /// won't re-offer it (or older).</summary>
        public static void Skip(PluginConfig config, string version)
        {
            config.SkippedUpdateVersion = version;
            config.Save();
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

        /// <summary>The folder that should be copied wholesale over the live plugin folder — the one
        /// directly containing HsbgCardLookup.dll inside the staged extraction (skips the installer
        /// extras — install.bat/README — that sit alongside it at the zip root). Used by
        /// <see cref="Plugin.RestartHost"/> to build the relauncher's copy step; null if nothing is
        /// staged.</summary>
        public static string ResolveStagedSourceRoot()
        {
            if (!Directory.Exists(StagedFilesDir)) return null;
            var dll = ResolveExtractedDll(StagedFilesDir);
            return File.Exists(dll) ? Path.GetDirectoryName(dll) : null;
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
            public string Url;
        }

        private sealed class GhRelease
        {
            [JsonProperty("tag_name")] public string TagName { get; set; }
            [JsonProperty("assets")] public List<GhAsset> Assets { get; set; }
        }

        private sealed class GhAsset
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("browser_download_url")] public string DownloadUrl { get; set; }
        }
    }
}
