namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// The language HDT's own interface is set to (Options → Appearance → Language), as HDT's
    /// Language enum name ("deDE", "ptPT", "ukUA"), or null. What "Automatic" follows: the plugin's
    /// menus live inside HDT's, and HDT already asks the player this question once.
    /// </summary>
    internal static class HdtLanguage
    {
        public static string Detect() =>
            HdtProbe.CleanLocaleCode(HdtProbe.Member(HdtProbe.ConfigInstance(), "Localization"));
    }
}
