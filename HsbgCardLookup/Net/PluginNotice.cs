using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace HsbgCardLookup.Net
{
    /// <summary>
    /// One manually-pushed in-app notification (e.g. "Patch notes for 35.4.3 are out"). Authored as
    /// one file per notice at <c>hsbg.cards/plugin/notifications/{id}.json</c> (numeric, sequential
    /// names — no shared file to append to / corrupt); the site's build folds every active one into
    /// <c>/plugin/notifications/feed.json</c> (<see cref="PluginNoticeFeed"/>), which is what the plugin
    /// fetches: one cacheable request per launch instead of probing ids. The bell shows those that are
    /// unseen, <see cref="Active"/>, and dated within 7 days.
    ///
    /// The English fields are the record. <see cref="Locales"/> overlays title, description and url per
    /// site language; a field a language lacks falls back to English, so a zh-tw player reads the notice,
    /// and opens its link (the zh-tw patch-notes page), in zh-tw.
    /// </summary>
    public sealed class PluginNotice
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("date")] public string Date { get; set; }     // ISO-8601; older than 7 days is dropped
        [JsonProperty("active")] public bool Active { get; set; } = true;
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }   // optional sub-line in the popup
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("locales")] public Dictionary<string, PluginNoticeText> Locales { get; set; }

        public string TitleFor(string web) => Pick(web, t => t.Title) ?? Title;
        public string DescriptionFor(string web) => Pick(web, t => t.Description) ?? Description;
        public string UrlFor(string web) => Pick(web, t => t.Url) ?? Url;

        private string Pick(string web, Func<PluginNoticeText, string> field)
        {
            if (Locales == null || string.IsNullOrEmpty(web)) return null;
            foreach (var kv in Locales)   // the author's casing need not match the registry's
            {
                if (!string.Equals(kv.Key, web, StringComparison.OrdinalIgnoreCase) || kv.Value == null) continue;
                string v = field(kv.Value);
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            return null;
        }
    }

    /// <summary>A notice's text in one language — any field may be missing (English then).</summary>
    public sealed class PluginNoticeText
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
    }

    /// <summary>Shape of /plugin/notifications/feed.json: { format, generatedAt, notices }.</summary>
    public sealed class PluginNoticeFeed
    {
        [JsonProperty("format")] public int Format { get; set; }
        [JsonProperty("generatedAt")] public string GeneratedAt { get; set; }
        [JsonProperty("notices")] public List<PluginNotice> Notices { get; set; }
    }
}
