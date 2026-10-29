using System;
using System.Collections.Generic;

namespace HsbgCardLookup.Data
{
    /// <summary>One of the languages hsbg.cards serves card data in.</summary>
    public sealed class Locale
    {
        public readonly string Web;     // the site's URL / API code ("de", "zh-tw")
        public readonly string Game;    // the Hearthstone client's locale code ("deDE", "zhTW")
        public readonly string Native;  // the language's own name, as the settings row shows it

        public Locale(string web, string game, string native) { Web = web; Game = game; Native = native; }
    }

    /// <summary>
    /// The locale registry — a port of the site's lib/locales.ts. English is the unprefixed base
    /// ("en" / enUS) and the twelve others are what hsbg.cards serves at /&lt;web&gt; and through
    /// /api/cards?locale=&lt;web&gt;. Russian (ruRU) is deliberately absent on the site and here: a
    /// Russian client gets English, like any client code the site does not know.
    /// </summary>
    public static class Locales
    {
        public const string English = "en";

        public static readonly IReadOnlyList<Locale> All = new[]
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

        private static readonly Dictionary<string, Locale> ByWebMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Locale> ByGameMap = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);

        static Locales()
        {
            foreach (var l in All) { ByWebMap[l.Web] = l; ByGameMap[l.Game] = l; }
        }

        /// <summary>True for one of the twelve site locales (not for "en").</summary>
        public static bool IsSite(string web) => !string.IsNullOrEmpty(web) && ByWebMap.ContainsKey(web);

        public static Locale ByWeb(string web) =>
            !string.IsNullOrEmpty(web) && ByWebMap.TryGetValue(web, out var l) ? l : null;

        /// <summary>The site locale for a client locale code ("deDE" → "de"); "en" for enUS/enGB, ruRU
        /// and anything unknown or null.</summary>
        public static string WebFromGame(string game) =>
            !string.IsNullOrEmpty(game) && ByGameMap.TryGetValue(game, out var l) ? l.Web : English;

        public static string NativeName(string web)
        {
            if (string.IsNullOrEmpty(web) || string.Equals(web, English, StringComparison.OrdinalIgnoreCase)) return "English";
            var l = ByWeb(web);
            return l != null ? l.Native : web;
        }
    }
}
