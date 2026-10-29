namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// The language HDT's own interface is set to (Options → Appearance → Language), as HDT's
    /// Language enum name ("deDE", "ukUA", "ptPT"), or null. The default for the plugin-content
    /// language: the plugin's menus live inside HDT's.
    /// </summary>
    internal static class HdtLanguage
    {
        public static string Detect() =>
            HdtProbe.CleanLocaleCode(HdtProbe.Member(HdtProbe.ConfigInstance(), "Localization"));
    }
}
