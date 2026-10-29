using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using HsbgCardLookup.Data;

namespace HsbgCardLookup.Localization
{
    /// <summary>Shape of data\locale-terms.json: { format, patch, generated, source, locales: { web: LocaleVocab } }.</summary>
    public sealed class LocaleTermsFile
    {
        public int Format { get; set; }
        public string Patch { get; set; }
        public string Generated { get; set; }
        public Dictionary<string, LocaleVocab> Locales { get; set; }
    }

    /// <summary>
    /// The per-language vocabulary: the words the smart search understands and the filter dropdowns
    /// show in each card language. Generated from the site's terms table and UI catalogs by
    /// tools/gen-locale-terms.js and shipped next to the DLL (data\locale-terms.json). A copy the
    /// site publishes at /plugin/locale-terms.json is cached beside the card data by
    /// <see cref="LocaleSync"/> and wins when it is newer (its "generated" date), so new words need
    /// no plugin release. Read-only after <see cref="Load"/>; <see cref="CardLanguage.Vocab"/> is the
    /// live handle consumers read.
    /// </summary>
    public static class LocaleTerms
    {
        /// <summary>The newest file format this build understands; a cached copy that is newer is ignored.</summary>
        public const int KnownFormat = 1;

        private static readonly Dictionary<string, LocaleVocab> Table = new Dictionary<string, LocaleVocab>(StringComparer.OrdinalIgnoreCase);

        public static string LoadInfo { get; private set; } = "(not loaded)";
        /// <summary>The loaded table's "generated" date — a change means the words changed.</summary>
        public static string Generated { get; private set; } = "";

        /// <summary>(Re)load the table: the newer of the bundled file and the site's cached copy
        /// (bundled wins a tie). UI thread.</summary>
        public static bool Load()
        {
            LocaleTermsFile best = null; string bestPath = null;
            foreach (var path in new[] { CardStore.BundledDataPath("locale-terms.json"), LocaleSync.TermsPath })
            {
                var file = Read(path);
                if (file == null) continue;
                if (best == null || string.CompareOrdinal(file.Generated ?? "", best.Generated ?? "") > 0) { best = file; bestPath = path; }
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
            !string.IsNullOrEmpty(web) && Table.TryGetValue(web, out var v) ? v : LocaleVocab.Empty;

        /// <summary>Does a downloaded copy parse as a table this build can use? (Guards the cache
        /// against an error page or a newer format.)</summary>
        public static bool LooksValid(string json)
        {
            try
            {
                var file = JsonConvert.DeserializeObject<LocaleTermsFile>(json);
                return file?.Locales != null && file.Locales.Count > 0 && file.Format <= KnownFormat;
            }
            catch { return false; }
        }

        private static LocaleTermsFile Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var file = JsonConvert.DeserializeObject<LocaleTermsFile>(File.ReadAllText(path));
                return file?.Locales != null && file.Locales.Count > 0 && file.Format <= KnownFormat ? file : null;
            }
            catch { return null; }
        }
    }
}
