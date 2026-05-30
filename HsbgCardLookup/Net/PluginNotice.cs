using Newtonsoft.Json;

namespace HsbgCardLookup.Net
{
    /// <summary>
    /// One manually-pushed in-app notification (e.g. "Patch notes for 35.4.3 are out"). Each lives in
    /// its own file at <c>hsbg.cards/plugin/notifications/{id}.json</c> (numeric, sequential names —
    /// no shared file to append to / corrupt). The plugin probes ids upward from the last-seen one and
    /// shows a bell for those that are unseen, <see cref="Active"/>, and dated within 7 days.
    /// </summary>
    public sealed class PluginNotice
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("date")] public string Date { get; set; }     // ISO-8601; older than 7 days is dropped
        [JsonProperty("active")] public bool Active { get; set; } = true;
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }   // optional sub-line in the popup
        [JsonProperty("url")] public string Url { get; set; }
    }
}
