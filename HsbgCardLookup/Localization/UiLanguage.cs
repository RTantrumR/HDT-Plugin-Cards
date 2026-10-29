using System;
using System.Windows.Threading;
using HsbgCardLookup.Config;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// The PLUGIN-CONTENT language: the plugin's own menus, tooltips, descriptions and messages.
    /// The setting (<see cref="PluginConfig.UiLanguage"/>), else HDT's own interface language, else
    /// English. Applying it swaps the string catalog (<see cref="Strings"/>) and raises
    /// <see cref="Changed"/>; surfaces rebuild their labels. The catalogs are bundled, so a switch
    /// never touches the network. UI thread.
    /// </summary>
    public sealed class UiLanguage
    {
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;
        private DateTime _lastHdtPoll = DateTime.MinValue;
        private bool _initialized;

        public UiLanguage(PluginConfig config, Dispatcher ui, Action<string> log)
        {
            _config = config; _ui = ui; _log = log;
        }

        /// <summary>The setting: "" = follow HDT's language, "en", or a UI-language web code.</summary>
        public string Preference => _config.UiLanguage ?? "";
        /// <summary>HDT's language setting as last read ("deDE", "ukUA"), or null.</summary>
        public string HdtCode { get; private set; }
        /// <summary>The language in use: "en" or a UI-language web code.</summary>
        public string Web { get; private set; } = Locales.English;

        /// <summary>Raised on the UI thread after the catalog switched — once per real change.</summary>
        public event Action Changed;

        /// <summary>OnLoad, before the UI is built: load the catalogs and pick the language.</summary>
        public void Initialize()
        {
            Strings.Load();
            _log?.Invoke("Strings: " + Strings.LoadInfo);
            HdtCode = HdtLanguage.Detect();
            Apply(Resolve(Preference, HdtCode), force: true);
            _initialized = true;
            _log?.Invoke($"UI language: {Web} ({(string.IsNullOrEmpty(Preference) ? "HDT " + (HdtCode ?? "?") : "setting")})");
        }

        /// <summary>The setting may have changed (ApplySettings): re-resolve and apply if different.</summary>
        public void Refresh()
        {
            if (string.IsNullOrEmpty(Preference)) HdtCode = HdtLanguage.Detect();
            Apply(Resolve(Preference, HdtCode), force: false);
        }

        /// <summary>From Plugin.OnUpdate: while following HDT, re-read its language once a minute so a
        /// change in HDT's options is picked up without a restart. Any thread.</summary>
        public void PollHdt()
        {
            if (!_initialized || !string.IsNullOrEmpty(Preference)) return;
            var now = DateTime.UtcNow;
            if ((now - _lastHdtPoll).TotalSeconds < 60) return;
            _lastHdtPoll = now;
            string code = HdtLanguage.Detect();
            if (string.Equals(code, HdtCode, StringComparison.Ordinal)) return;
            HdtCode = code;
            _ui.BeginInvoke(new Action(() => Apply(Resolve(Preference, HdtCode), force: false)));
        }

        /// <summary>Pure: the effective language for a setting and HDT's code.</summary>
        public static string Resolve(string preference, string hdtCode)
        {
            string pick = (preference ?? "").Trim();
            if (pick.Length == 0) return Locales.UiWebFromHdt(hdtCode);
            if (string.Equals(pick, Locales.English, StringComparison.OrdinalIgnoreCase)) return Locales.English;
            var l = Locales.ByWeb(pick);
            return l != null ? l.Web : Locales.English;
        }

        private void Apply(string web, bool force)
        {
            if (!force && web == Web) return;
            Web = web;
            Strings.SetLocale(web);
            try { Changed?.Invoke(); }
            catch (Exception ex) { _log?.Invoke("UiLanguage.Changed handler error: " + ex.Message); }
        }
    }
}
