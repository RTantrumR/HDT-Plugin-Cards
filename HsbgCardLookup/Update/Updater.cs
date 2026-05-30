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
        public string Url;     // non-null => show a clickable link (manual install fallback)
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

        public static async Task<UpdateNotice> RunAsync(Version current, string pluginDir, PluginConfig config)
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
                        ? new UpdateNotice($"Update v{pending.Version} ready — restart HDT to finish.")
                        : new UpdateNotice($"Update v{pending.Version} couldn't be applied automatically. "
                            + "Please install it manually:", true, pending.Url ?? ReleasesUrl);
                }

                // 2. Throttle, then check the latest release.
                if (DateTime.TryParse(config.LastUpdateCheckUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                    && DateTime.UtcNow - last.ToUniversalTime() < CheckInterval)
                    return null;

                var rel = await AssetClient.GetJsonAsync<GhRelease>(ApiLatest).ConfigureAwait(false);
                config.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o");
                config.Save();
                if (rel == null) return null;

                var v = ParseVersion(rel.TagName);
                if (v == null || v <= current) return null;

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
                    ? new UpdateNotice($"Update v{v} downloaded — restart HDT to apply.")
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

        /// <summary>Copy the extracted files over the plugin folder. Copies the main DLL first so a
        /// file lock (HDT not shadow-copying after all) is detected before touching anything else.</summary>
        private static bool TryApply(string pluginDir)
        {
            try
            {
                var extractDir = Path.Combine(StagingDir, "files");
                var dll = ResolveExtractedDll(extractDir);
                if (!File.Exists(dll)) return false;
                var srcRoot = Path.GetDirectoryName(dll);

                File.Copy(dll, Path.Combine(pluginDir, "HsbgCardLookup.dll"), true);   // lock probe
                foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = src.Substring(srcRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (rel.Equals("HsbgCardLookup.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(pluginDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(src, dest, true);
                }
                return true;
            }
            catch { return false; }
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
