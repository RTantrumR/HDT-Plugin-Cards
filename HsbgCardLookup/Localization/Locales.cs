using System;
using System.Collections.Generic;

namespace HsbgCardLookup.Localization
{
    /// <summary>One language the plugin can show something in.</summary>
    public sealed class Locale
    {
        public readonly string Web;     // the site's URL / API code ("de", "zh-tw", "ua")
        public readonly string Game;    // the Hearthstone client's locale code ("deDE"); null when the game has no client in it
        public readonly string Native;  // the language's own name, as the settings rows show it

        /// <summary>The site serves card data (and renders) in this language.</summary>
        public bool HasCards => Game != null;

        public Locale(string web, string game, string native) { Web = web; Game = game; Native = native; }
    }

    /// <summary>
    /// The locale registry — a port of the site's lib/locales.ts. English is the unprefixed base
    /// ("en" / enUS). <see cref="Cards"/> are the twelve languages hsbg.cards serves card data in,
    /// at /&lt;web&gt; and through /api/cards?locale=&lt;web&gt;; <see cref="Ui"/> adds Ukrainian,
    /// which the game has no client for but HDT has a UI in and this plugin a README — the plugin's
    /// own texts exist in it, the cards stay English. Russian (ruRU) is deliberately absent on the
    /// site and here: a Russian client or HDT gets English, like any code the registry does not know.
    /// </summary>
    public static class Locales
    {
        public const string English = "en";
        public const string Ukrainian = "ua";

        /// <summary>The languages the site serves CARD data in (the game's locales minus Russian).</summary>
        public static readonly IReadOnlyList<Locale> Cards = new[]
        {
            new Locale("de",    "deDE", "Deutsch"),
            new Locale("es",    "esES", "Español (EU)"),
            new Locale("es-mx", "esMX", "Español (AL)"),
            new Locale("fr",    "frFR", "Français"),
            new Locale("it",    "itIT", "Italiano"),
            new Locale("ja",    "jaJP", "日本語"),
            new Locale("ko",    "koKR", "한국어"),
            new Locale("pl",    "plPL", "Polski"),
            new Locale("pt",    "ptBR", "Português"),
            new Locale("th",    "thTH", "ไทย"),
            new Locale("zh-cn", "zhCN", "简体中文"),
            new Locale("zh-tw", "zhTW", "繁體中文"),
        };

        /// <summary>The languages the plugin's own UI exists in: the card languages plus Ukrainian.</summary>
        public static readonly IReadOnlyList<Locale> Ui = BuildUi();

        private static IReadOnlyList<Locale> BuildUi()
        {
            var list = new List<Locale>(Cards) { new Locale(Ukrainian, null, "Українська") };
            return list;
        }

        private static readonly Dictionary<string, Locale> ByWebMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Locale> ByGameMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);

        static Locales()
        {
            foreach (var l in Ui)
            {
                ByWebMap[l.Web] = l;
                if (l.Game != null) ByGameMap[l.Game] = l;
            }
        }

        /// <summary>True for one of the twelve card languages (not for "en").</summary>
        public static bool IsCardLocale(string web) => ByWeb(web)?.HasCards == true;

        /// <summary>True for a language the plugin's UI exists in (not for "en").</summary>
        public static bool IsUiLocale(string web) => !string.IsNullOrEmpty(web) && ByWebMap.ContainsKey(web);

        public static Locale ByWeb(string web) =>
            !string.IsNullOrEmpty(web) && ByWebMap.TryGetValue(web, out var l) ? l : null;

        /// <summary>The card language for a client locale code ("deDE" → "de"); "en" for enUS/enGB,
        /// ruRU and anything unknown or null.</summary>
        public static string WebFromGame(string game) =>
            !string.IsNullOrEmpty(game) && ByGameMap.TryGetValue(game, out var l) ? l.Web : English;

        /// <summary>The UI language for HDT's own language setting (its Language enum name): the game
        /// codes as above, plus ukUA → Ukrainian and ptPT → Portuguese; ruRU and unknown → English.</summary>
        public static string UiWebFromHdt(string code)
        {
            if (string.IsNullOrEmpty(code)) return English;
            if (string.Equals(code, "ukUA", StringComparison.OrdinalIgnoreCase)) return Ukrainian;
            if (string.Equals(code, "ptPT", StringComparison.OrdinalIgnoreCase)) return "pt";
            return WebFromGame(code);
        }

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
