using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// The plugin's own UI strings in the plugin-content language. <c>data\ui\en.json</c> is the key
    /// source; <c>data\ui\&lt;web&gt;.json</c> is a partial overlay — a key it lacks renders English, a
    /// key no catalog has renders as the key itself (visible in the UI, never an exception). All
    /// catalogs are bundled and loaded at start (a few hundred strings each); <see cref="SetLocale"/>
    /// is a reference swap on the UI thread, done by <see cref="CardLanguage"/>.
    ///
    /// Keys are dotted by surface ("settings.cardSearch.title", "overlay.search.placeholder").
    /// Format strings carry {0}-style placeholders and go through <see cref="F"/>; every catalog must
    /// keep the same placeholders as English (tools/check-ui-strings.js holds them to it).
    /// </summary>
    public static class Strings
    {
        private static Dictionary<string, string> _en = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _active = _en;
        private static readonly Dictionary<string, Dictionary<string, string>> Catalogs =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        public static string Web { get; private set; } = Locales.English;
        public static string LoadInfo { get; private set; } = "(not loaded)";

        /// <summary>(Re)load every bundled catalog. UI thread, once at start.</summary>
        public static bool Load()
        {
            Catalogs.Clear();
            var loaded = new List<string>();
            foreach (var web in new[] { Locales.English }.Concat(Locales.Cards.Select(l => l.Web)))
            {
                var cat = Read(CardStore.BundledDataPath(Path.Combine("ui", web + ".json")));
                if (cat == null) continue;
                Catalogs[web] = cat;
                loaded.Add(web + ":" + cat.Count);
            }
            _en = Catalogs.TryGetValue(Locales.English, out var en) ? en : new Dictionary<string, string>(StringComparer.Ordinal);
            _loaded = true;
            SetLocale(Web);
            LoadInfo = loaded.Count == 0 ? "no ui catalogs found (data\\ui\\*.json)" : string.Join(" ", loaded);
            return _en.Count > 0;
        }

        public static void SetLocale(string web)
        {
            Web = string.IsNullOrEmpty(web) ? Locales.English : web;
            _active = Catalogs.TryGetValue(Web, out var cat) ? cat : _en;
        }

        /// <summary>The string for a key in the active language, else English, else the key.</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            // HDT reads the plugin's Description, ButtonText and menu before OnLoad has run
            // CardLanguage.Initialize(): load the bundled catalogs on first use rather than show keys.
            if (!_loaded) Load();
            string s;
            if (_active.TryGetValue(key, out s) && s != null) return s;
            if (_en.TryGetValue(key, out s) && s != null) return s;
            return key;
        }

        /// <summary>T(key) with {0}-style placeholders filled; a malformed template renders unfilled.</summary>
        public static string F(string key, params object[] args)
        {
            string format = T(key);
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch { return format; }
        }

        /// <summary>English keys a catalog lacks — a diagnostic for the log and the check tool.</summary>
        public static IEnumerable<string> MissingKeys(string web)
        {
            if (!Catalogs.TryGetValue(web ?? "", out var cat)) return _en.Keys;
            return _en.Keys.Where(k => !cat.ContainsKey(k));
        }

        private static Dictionary<string, string> Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (raw == null) return null;
                var cat = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in raw)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) cat[kv.Key] = kv.Value;
                return cat;
            }
            catch { return null; }
        }
    }
}
