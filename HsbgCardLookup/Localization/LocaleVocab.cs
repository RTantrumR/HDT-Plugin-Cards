using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// One language's words for the values the cards carry: English value → the client's label
    /// ("Beast" → "Wildtier", "hero_power" → "Heldenfähigkeit", "Taunt" → "Spott"). The maps only
    /// hold labels that differ from English, so a value with no entry keeps its English word. The
    /// six groups the plugin reads today are typed; any further group the generator emits is kept
    /// in <see cref="Extra"/> and reachable through <see cref="Label"/> without a code change.
    /// </summary>
    public sealed class LocaleVocab
    {
        public static readonly LocaleVocab Empty = new LocaleVocab();

        public string Web = Locales.English;
        public Dictionary<string, string> Keyword = Insensitive(null);
        public Dictionary<string, string> Tribe = Insensitive(null);
        public Dictionary<string, string> CardType = Insensitive(null);
        public Dictionary<string, string> SpellSchool = Insensitive(null);
        public Dictionary<string, string> TrinketTier = Insensitive(null);
        public Dictionary<string, string> Category = Insensitive(null);
        [JsonExtensionData] public IDictionary<string, JToken> Extra;

        [JsonIgnore] public bool IsEnglish => string.Equals(Web, Locales.English, StringComparison.OrdinalIgnoreCase);

        /// <summary>The label for a value, or null when the language has none (show the English value).</summary>
        public string TribeLabel(string value) => Pick(Tribe, value);
        public string CardTypeLabel(string value) => Pick(CardType, value);
        public string SpellSchoolLabel(string value) => Pick(SpellSchool, value);
        public string TrinketTierLabel(string value) => Pick(TrinketTier, value);
        public string KeywordLabel(string value) => Pick(Keyword, value);

        /// <summary>Any group by its JSON name ("keyword", "tribe", …, or one only the data knows).</summary>
        public string Label(string group, string value)
        {
            switch ((group ?? "").ToLowerInvariant())
            {
                case "keyword": return Pick(Keyword, value);
                case "tribe": return Pick(Tribe, value);
                case "cardtype": return Pick(CardType, value);
                case "spellschool": return Pick(SpellSchool, value);
                case "trinkettier": return Pick(TrinketTier, value);
                case "category": return Pick(Category, value);
            }
            try
            {
                if (Extra == null || value == null) return null;
                foreach (var kv in Extra)
                {
                    if (!string.Equals(kv.Key, group, StringComparison.OrdinalIgnoreCase)) continue;
                    var obj = kv.Value as JObject;
                    if (obj == null) return null;
                    foreach (var p in obj.Properties())
                        if (string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase))
                        {
                            string s = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                            return string.IsNullOrWhiteSpace(s) ? null : s;
                        }
                    return null;
                }
            }
            catch { }
            return null;
        }

        private static string Pick(Dictionary<string, string> map, string value) =>
            value != null && map != null && map.TryGetValue(value, out var label) && !string.IsNullOrWhiteSpace(label) ? label : null;

        // Newtonsoft replaces the field initializers with plain (case-sensitive) dictionaries; the
        // generator writes the values the cards carry verbatim, but a case-insensitive map costs
        // nothing and forgives a hand edit.
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
}
