using System;
using System.Collections.Generic;

namespace HsbgCardLookup.Localization
{
    /// <summary>One language the plugin can show something in.</summary>
    public sealed class Locale
    {
        public readonly string Web;     // the site's URL / API code ("de", "zh-tw")
        public readonly string Game;    // the Hearthstone client's locale code ("deDE"); null when the game has no client in it
        public readonly string Native;  // the language's own name, as the language page shows it ("Português (Brasil)")
        public readonly string English; // its English name, the line under it ("Portuguese (Brazil)")
        public readonly string Tag;     // the IETF tag beside that ("pt-BR")

        /// <summary>The site serves card data (and renders) in this language.</summary>
        public bool HasCards => Game != null;

        public Locale(string web, string game, string native, string english, string tag)
        { Web = web; Game = game; Native = native; English = english; Tag = tag; }
    }

    /// <summary>
    /// The locale registry — a port of the site's lib/locales.ts. English is the unprefixed base
    /// ("en" / enUS). <see cref="Cards"/> are the twelve languages hsbg.cards serves card data in,
    /// at /&lt;web&gt; and through /api/cards?locale=&lt;web&gt;; the plugin's own texts exist in the
    /// same twelve. Any code the registry does not know resolves to English.
    /// </summary>
    public static class Locales
    {
        public const string English = "en";

        /// <summary>The languages the site serves CARD data in.</summary>
        public static readonly IReadOnlyList<Locale> Cards = new[]
        {
            new Locale("de",    "deDE", "Deutsch",                 "German",                "de-DE"),
            new Locale("es",    "esES", "Español (España)",        "Spanish (Spain)",       "es-ES"),
            new Locale("es-mx", "esMX", "Español (Latinoamérica)", "Spanish (Latin America)", "es-MX"),
            new Locale("fr",    "frFR", "Français",                "French",                "fr-FR"),
            new Locale("it",    "itIT", "Italiano",                "Italian",               "it-IT"),
            new Locale("ja",    "jaJP", "日本語",                   "Japanese",              "ja-JP"),
            new Locale("ko",    "koKR", "한국어",                   "Korean",                "ko-KR"),
            new Locale("pl",    "plPL", "Polski",                  "Polish",                "pl-PL"),
            new Locale("pt",    "ptBR", "Português (Brasil)",      "Portuguese (Brazil)",   "pt-BR"),
            new Locale("th",    "thTH", "ไทย",                     "Thai",                  "th-TH"),
            new Locale("zh-cn", "zhCN", "简体中文",                 "Chinese (Simplified)",  "zh-CN"),
            new Locale("zh-tw", "zhTW", "繁體中文",                 "Chinese (Traditional)", "zh-TW"),
        };

        private static readonly Dictionary<string, Locale> ByWebMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Locale> ByGameMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);

        static Locales()
        {
            foreach (var l in Cards)
            {
                ByWebMap[l.Web] = l;
                if (l.Game != null) ByGameMap[l.Game] = l;
            }
        }

        /// <summary>True for one of the twelve card languages (not for "en").</summary>
        public static bool IsCardLocale(string web) => ByWeb(web)?.HasCards == true;

        public static Locale ByWeb(string web) =>
            !string.IsNullOrEmpty(web) && ByWebMap.TryGetValue(web, out var l) ? l : null;

        /// <summary>The card language for a game locale code ("deDE" → "de"); "en" for enUS/enGB
        /// and anything unknown or null.</summary>
        public static string WebFromGame(string game) =>
            !string.IsNullOrEmpty(game) && ByGameMap.TryGetValue(game, out var l) ? l.Web : English;

        /// <summary>The language for HDT's own language setting (its Language enum name): the game
        /// codes as above, plus ptPT → Portuguese; anything else → English.</summary>
        public static string WebFromHdt(string code)
        {
            if (string.IsNullOrEmpty(code)) return English;
            if (string.Equals(code, "ptPT", StringComparison.OrdinalIgnoreCase)) return "pt";
            return WebFromGame(code);
        }

        /// <summary>The site's path for a language — its lib/locales.ts localeHref: "/de/patch-notes"
        /// for de, the path itself for English.</summary>
        public static string SitePath(string web, string path) =>
            string.IsNullOrEmpty(web) || string.Equals(web, English, StringComparison.OrdinalIgnoreCase) ? path : "/" + web + path;

        public static string NativeName(string web)
        {
            if (string.IsNullOrEmpty(web) || string.Equals(web, English, StringComparison.OrdinalIgnoreCase)) return "English";
            var l = ByWeb(web);
            return l != null ? l.Native : web;
        }

        // ── Locale renders ──
        // The site points a card's image at /cards/production/locales/<web>/… when a render exists in
        // that language; everything else keeps the English thumb path.
        public const string LocaleRenderPrefix = "/cards/production/locales/";

        public static bool IsLocaleRender(string image) =>
            !string.IsNullOrEmpty(image) && image.StartsWith(LocaleRenderPrefix, StringComparison.Ordinal);

        /// <summary>"/cards/production/locales/de/…" → "de"; null for an English path.</summary>
        public static string WebFromRenderPath(string image)
        {
            if (!IsLocaleRender(image)) return null;
            int start = LocaleRenderPrefix.Length;
            int end = image.IndexOf('/', start);
            return end > start ? image.Substring(start, end - start) : null;
        }
    }
}
