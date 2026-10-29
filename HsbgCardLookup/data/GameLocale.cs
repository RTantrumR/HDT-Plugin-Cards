using System;
using System.Reflection;
using Microsoft.Win32;

namespace HsbgCardLookup.Data
{
    /// <summary>
    /// Which language the Hearthstone client runs in, as a client locale code ("deDE"), or null.
    ///
    /// HDT already answers this: <c>Helper.GetCardLanguage()</c> is the locale Battle.net launches the
    /// game with (its registry key), falling back to HDT's own UI language. It is read through
    /// reflection on the HDT assembly already loaded in this process rather than called directly:
    /// the member postdates the HDT build this plugin compiles against, and a plugin must load on
    /// every HDT. The registry key HDT itself reads is the fallback.
    /// </summary>
    internal static class GameLocale
    {
        public static string Detect() => FromHdt() ?? FromRegistry();

        private static string FromHdt()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type helper;
                    try { helper = asm.GetType("Hearthstone_Deck_Tracker.Utility.Helper", false); }
                    catch { continue; }
                    if (helper == null) continue;
                    var m = helper.GetMethod("GetCardLanguage", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    return Clean(m?.Invoke(null, null) as string);
                }
            }
            catch { }
            return null;
        }

        private static string FromRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\Battle.net\Launch Options\WTCG"))
                    return Clean(key?.GetValue("LOCALE") as string);
            }
            catch { return null; }
        }

        // A client locale code is two letters + two letters ("enUS"); anything else is noise.
        private static string Clean(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            return v.Length == 4 && char.IsLetter(v[0]) && char.IsLetter(v[3]) ? v : null;
        }
    }
}
