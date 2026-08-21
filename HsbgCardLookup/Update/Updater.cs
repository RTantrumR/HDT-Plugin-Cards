using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HsbgCardLookup.Config;
using HsbgCardLookup.Net;

namespace HsbgCardLookup.Update
{
    /// <summary>A message to surface to the user about updates: a newer version found (offer a
    /// browser link + Skip), a one-time "just updated" confirmation, a plain status ("up to date" /
    /// "couldn't check"), or an error with a manual-install link.</summary>
    internal sealed class UpdateNotice
    {
        public string Message;
        public bool IsError;
        public string Url;                 // release page — where the download and the notes live
        public string LinkLabel;           // custom text for Url's link, instead of the raw URL
        public bool AvailableForDownload;  // true => a newer version was found
        public string AvailableVersion;    // set when AvailableForDownload
        public UpdateNotice(string message, bool isError = false, string url = null)
        { Message = message; IsError = isError; Url = url; }
    }

    /// <summary>
    /// Update NOTIFICATION via GitHub Releases — check-and-link only. This class never downloads,
    /// writes, copies or executes anything: finding a newer version returns an
    /// <see cref="UpdateNotice"/> whose Url is the release page, and installing is the user's own
    /// browser + install.bat (the same flow as first install). The in-plugin download/stage/apply
    /// pipeline that existed through v0.3.4.1 was removed deliberately: its freshly-written staged
    /// DLL — an unsigned, zero-reputation PE written to disk by a non-browser process — is exactly
    /// the artifact Defender's cloud ML flagged in two independent false-positive waves (Sabsik.TE.A!ml
    /// and Wacatac.H!ml, both on update-staging\files\...\HsbgCardLookup.dll), and the 2026-08-21
    /// research pass found no unsigned auto-update architecture that avoids this — every peer
    /// plugin/mod ecosystem delivers unsigned DLLs through the user's browser instead. Do not
    /// reintroduce a downloader here. Best-effort — offline / missing repo / any failure = no update.
    /// </summary>
    internal static class Updater
    {
        public const string GitHubRepo = "RTantrumR/HDT-Plugin-Cards";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

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
                CleanupLegacyStaging();

                // 1. First run of a new version → one-time "Updated to vN" confirmation. (Replaces the
                // old staging-marker resolve; a fresh install — no recorded previous version — stays
                // silent, as does a downgrade.)
                var prev = config.LastRunVersion;
                if (prev != current.ToString())
                {
                    config.LastRunVersion = current.ToString();
                    config.Save();
                    var pv = ParseVersion(prev);
                    if (pv != null && pv < current)
                        return new UpdateNotice($"Updated to version {current}.", false, ReleasesUrl)
                            { LinkLabel = "View release notes ↗" };
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

                return new UpdateNotice($"Version {v} is available.")
                {
                    AvailableForDownload = true,
                    AvailableVersion = v.ToString(),
                    Url = string.IsNullOrEmpty(rel.HtmlUrl) ? ReleasesUrl : rel.HtmlUrl,
                };
            }
            catch { return null; }
        }

        /// <summary>Remember a version the user explicitly chose not to install — background checks
        /// won't re-offer it (or older).</summary>
        public static void Skip(PluginConfig config, string version)
        {
            config.SkippedUpdateVersion = version;
            config.Save();
        }

        /// <summary>Best-effort removal of what the retired (≤ v0.3.4.1) in-plugin download pipeline
        /// left on disk: the update-staging folder and the pending-update marker. Beyond hygiene this
        /// is remedial — on machines where Defender flagged the staged DLL, the still-present file
        /// kept re-triggering "threat found" until it was deleted. Runs on every check.</summary>
        private static void CleanupLegacyStaging()
        {
            try
            {
                var staging = Path.Combine(PluginConfig.DataDir, "update-staging");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            catch { }
            try
            {
                var marker = Path.Combine(PluginConfig.DataDir, "update-pending.json");
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch { }
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(s, out var v) ? v : null;
        }

        private sealed class GhRelease
        {
            [JsonProperty("tag_name")] public string TagName { get; set; }
            [JsonProperty("html_url")] public string HtmlUrl { get; set; }
        }
    }
}
