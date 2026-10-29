using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace HsbgCardLookup.Data
{
    /// <summary>
    /// One language's words for the values the cards carry: English value → the client's label
    /// ("Beast" → "Wildtier", "hero_power" → "Heldenfähigkeit", "Taunt" → "Spott"). A value with no
    /// entry keeps its English word — the maps only hold labels that differ from English.
    /// </summary>
    public sealed class LocaleVocab
    {
        public string Web = Locales.English;
        public Dictionary<string, string> Keyword = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Tribe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CardType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SpellSchool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> TrinketTier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Category = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore] public bool IsEnglish => string.Equals(Web, Locales.English, StringComparison.OrdinalIgnoreCase);

        /// <summary>The label for a value, or null when the language has none (show the English value).</summary>
        public string TribeLabel(string value) => Pick(Tribe, value);
        public string CardTypeLabel(string value) => Pick(CardType, value);
        public string SpellSchoolLabel(string value) => Pick(SpellSchool, value);
        public string TrinketTierLabel(string value) => Pick(TrinketTier, value);
        public string KeywordLabel(string value) => Pick(Keyword, value);

        private static string Pick(Dictionary<string, string> map, string value) =>
            value != null && map != null && map.TryGetValue(value, out var label) && !string.IsNullOrWhiteSpace(label) ? label : null;

        // Newtonsoft replaces the field initializers with plain (case-sensitive) dictionaries; lookups
        // here are by the values the cards carry, which the generator writes verbatim, but a
        // case-insensitive map costs nothing and forgives a hand edit.
        internal void Normalize()
        {
            Keyword = Insensitive(Keyword); Tribe = Insensitive(Tribe); CardType = Insensitive(CardType);
            SpellSchool = Insensitive(SpellSchool); TrinketTier = Insensitive(TrinketTier); Category = Insensitive(Category);
        }

        private static Dictionary<string, string> Insensitive(Dictionary<string, string> src)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (src != null)
                foreach (var kv in src)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)) d[kv.Key.Trim()] = kv.Value.Trim();
            return d;
        }
    }

    /// <summary>Shape of data\locale-terms.json: { patch, generated, source, locales: { web: LocaleVocab } }.</summary>
    public sealed class LocaleTermsFile
    {
        public string Patch { get; set; }
        public string Generated { get; set; }
        public Dictionary<string, LocaleVocab> Locales { get; set; }
    }

    /// <summary>
    /// The per-language vocabulary: the words the smart search understands and the filter dropdowns
    /// show in each of the site's locales. Generated from the site's terms table and UI catalogs by
    /// tools/gen-locale-terms.js and shipped next to the DLL (data\locale-terms.json). A copy the
    /// site publishes at /plugin/locale-terms.json is cached beside the card data and wins when it is
    /// newer (its "generated" date), so new words need no plugin release.
    /// </summary>
    public static class LocaleTerms
    {
        private static readonly LocaleVocab Empty = new LocaleVocab();
        private static readonly Dictionary<string, LocaleVocab> Table = new Dictionary<string, LocaleVocab>(StringComparer.OrdinalIgnoreCase);

        public static string LoadInfo { get; private set; } = "(not loaded)";
        public static string Generated { get; private set; } = "";

        /// <summary>The vocabulary of the language in use — set by the plugin when the language is
        /// resolved, read by the UI for its labels. English = the empty vocabulary (every label null).</summary>
        public static LocaleVocab Active { get; set; } = Empty;

        public static string CachePath => Path.Combine(Config.PluginConfig.DataDir, "cache", "locale-terms.json");

        /// <summary>(Re)load the table: the newer of the bundled file and the site's cached copy.</summary>
        public static bool Load()
        {
            LocaleTermsFile best = null; string bestPath = null;
            foreach (var path in new[] { BundledPath(), CachePath })
            {
                var file = Read(path);
                if (file == null) continue;
                if (best == null || string.CompareOrdinal(file.Generated ?? "", best.Generated ?? "") >= 0) { best = file; bestPath = path; }
            }
            Table.Clear();
            if (best == null) { LoadInfo = "locale-terms.json not found"; Generated = ""; return false; }
            foreach (var kv in best.Locales)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                var v = kv.Value ?? new LocaleVocab();
                v.Web = kv.Key;
                v.Normalize();
                Table[kv.Key] = v;
            }
            Generated = best.Generated ?? "";
            LoadInfo = $"{Table.Count} locales (patch {best.Patch}, generated {Generated}) from {bestPath}";
            return true;
        }

        /// <summary>The vocabulary for a web code; the empty one for English or an unknown code.</summary>
        public static LocaleVocab For(string web) =>
            !string.IsNullOrEmpty(web) && Table.TryGetValue(web, out var v) ? v : Empty;

        /// <summary>Does a downloaded copy parse as a usable table? (Guards the cache against an error page.)</summary>
        public static bool LooksValid(string json)
        {
            try
            {
                var file = JsonConvert.DeserializeObject<LocaleTermsFile>(json);
                return file?.Locales != null && file.Locales.Count > 0;
            }
            catch { return false; }
        }

        private static LocaleTermsFile Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var file = JsonConvert.DeserializeObject<LocaleTermsFile>(File.ReadAllText(path));
                return file?.Locales != null && file.Locales.Count > 0 ? file : null;
            }
            catch { return null; }
        }

        private static string BundledPath()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "data", "locale-terms.json");
            }
            catch { return null; }
        }
    }
}
