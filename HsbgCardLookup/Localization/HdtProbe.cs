using System;
using System.Reflection;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// Reads HDT's own settings object without a compile-time reference: the members the language
    /// code wants (<c>Config.Instance.Localization</c>, <c>LastSeenHearthstoneLang</c>) postdate the
    /// HDT build this plugin compiles against, and a plugin must load on every HDT. A member that is
    /// missing or renamed reads as null — never a load failure, never a write.
    /// </summary>
    internal static class HdtProbe
    {
        public static object ConfigInstance()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type config;
                    try { config = asm.GetType("Hearthstone_Deck_Tracker.Config", false); }
                    catch { continue; }
                    if (config == null) continue;
                    var instance = config.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    return instance?.GetValue(null, null);
                }
            }
            catch { }
            return null;
        }

        /// <summary>A public field or property of an object as text (an enum reads as its name), or null.</summary>
        public static string Member(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var type = obj.GetType();
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                object value;
                if (field != null) value = field.GetValue(obj);
                else
                {
                    var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null) return null;
                    value = prop.GetValue(obj, null);
                }
                return value?.ToString();
            }
            catch { return null; }
        }

        /// <summary>A client locale code is two letters + two letters ("enUS"); HDT's own reader also
        /// drops a dash ("en-US"). Anything else is noise.</summary>
        public static string CleanLocaleCode(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().Replace("-", "");
            return v.Length == 4 && char.IsLetter(v[0]) && char.IsLetter(v[3]) ? v : null;
        }
    }
}
