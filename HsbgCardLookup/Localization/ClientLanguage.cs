using Microsoft.Win32;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// Which language the Hearthstone client runs in, as a client locale code ("deDE"), or null.
    /// First the registry key Battle.net launches the game with — the same key HDT reads — then the
    /// value HDT remembered from it. Both are plain reads: HDT's own <c>Helper.GetCardLanguage()</c>
    /// is deliberately not called, because on a miss it rewrites and saves HDT's config and reloads
    /// its card definitions, which is not a plugin's business.
    /// </summary>
    internal static class ClientLanguage
    {
        public static string Detect() => FromRegistry() ?? FromHdt();

        private static string FromRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\Battle.net\Launch Options\WTCG"))
                    return HdtProbe.CleanLocaleCode(key?.GetValue("LOCALE") as string);
            }
            catch { return null; }
        }

        private static string FromHdt() =>
            HdtProbe.CleanLocaleCode(HdtProbe.Member(HdtProbe.ConfigInstance(), "LastSeenHearthstoneLang"));
    }
}
