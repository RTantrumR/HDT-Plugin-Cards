using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Search;

namespace HsbgCardLookup.Localization
{
    /// <summary>
    /// The GAME-CONTENT language: the one card names, text, renders and the game's own words
    /// (tribes, keywords, card types…) appear in. One instance, owned by Plugin, and the only place
    /// that decides it: the setting (<see cref="PluginConfig.Language"/>), else the Hearthstone
    /// client's language, else English. Applying it switches the store's overlay, the search alias
    /// table, the name sort and the art cache's memory, then raises <see cref="Changed"/> once — the
    /// seam every language-aware surface hangs on. UI thread, except the download bodies.
    ///
    /// Nothing here downloads on a switch: a snapshot is fetched only by <see cref="EnsureDataAsync"/>,
    /// which callers invoke when the language is actually used (the overlay opens, a match starts)
    /// and once per launch — so flipping through the settings pulls nothing from the site, and a
    /// language whose snapshot is not cached yet shows English until it lands.
    /// </summary>
    public sealed class CardLanguage
    {
        private readonly CardStore _store;
        private readonly PluginConfig _config;
        private readonly Dispatcher _ui;
        private readonly Action<string> _log;

        private string _appliedWeb;              // what Apply() last switched to (null = never)
        private string _appliedTerms;            // LocaleTerms.Generated at that time
        private DateTime _lastClientPoll = DateTime.MinValue;
        private DateTime _lastFetchFailure = DateTime.MinValue;
        private volatile bool _fetching;
        private bool _termsChecked;

        public CardLanguage(CardStore store, PluginConfig config, Dispatcher ui, Action<string> log)
        {
            _store = store; _config = config; _ui = ui; _log = log;
        }

        /// <summary>The setting: "" = follow the game client, "en", or a card-language web code.</summary>
        public string Preference => _config.Language ?? "";
        /// <summary>The client locale code last detected ("deDE"), or null.</summary>
        public string ClientCode { get; private set; }
        /// <summary>The language in use: "en" or a card-language web code.</summary>
        public string Web { get; private set; } = Locales.English;
        /// <summary>The game's words in <see cref="Web"/>; the empty vocabulary for English.</summary>
        public LocaleVocab Vocab { get; private set; } = LocaleVocab.Empty;
        /// <summary>How card names sort: ordinal for English (today's order), culture-aware otherwise.</summary>
        public StringComparer NameComparer =>
            Web == Locales.English ? StringComparer.OrdinalIgnoreCase : StringComparer.InvariantCultureIgnoreCase;
        /// <summary>False while a non-English language waits for its snapshot (cards read English meanwhile).</summary>
        public bool DataApplied => Web == Locales.English || (_store.LocaleApplied && _store.LocaleWeb == Web);
        /// <summary>The snapshot is applied but the site had no translation for it (served English).</summary>
        public bool DataEmpty => Web != Locales.English && _store.LocaleApplied && _store.LocaleCount == 0;

        /// <summary>Raised on the UI thread after the store, aliases, comparer and art memory were
        /// switched — once per real change.</summary>
        public event Action Changed;

        /// <summary>OnLoad, before the UI is built: load the vocabulary table, detect the client, apply
        /// from cache. No network.</summary>
        public void Initialize()
        {
            LocaleTerms.Load();
            _log?.Invoke("LocaleTerms: " + LocaleTerms.LoadInfo);
            ClientCode = ClientLanguage.Detect();
            Apply(Resolve(Preference, ClientCode), force: true);
            _log?.Invoke("Card language: " + Describe());
        }

        /// <summary>The setting may have changed (ApplySettings): re-resolve and apply if different.
        /// From cache only; a missing snapshot waits for <see cref="EnsureDataAsync"/>.</summary>
        public void Refresh()
        {
            if (string.IsNullOrEmpty(Preference)) ClientCode = ClientLanguage.Detect();
            Apply(Resolve(Preference, ClientCode), force: false);
        }

        /// <summary>From Plugin.OnUpdate: while following the client, re-read its language once a
        /// minute (a change needs a game restart anyway). Any thread.</summary>
        public void PollClient()
        {
            if (!string.IsNullOrEmpty(Preference)) return;
            var now = DateTime.UtcNow;
            if ((now - _lastClientPoll).TotalSeconds < 60) return;
            _lastClientPoll = now;
            string code = ClientLanguage.Detect();
            if (string.Equals(code, ClientCode, StringComparison.Ordinal)) return;
            ClientCode = code;
            _ui.BeginInvoke(new Action(() => Apply(Resolve(Preference, ClientCode), force: false)));
        }

        /// <summary>The store reloaded the English snapshot (and re-laid the overlay): tell the
        /// surfaces, since names may have changed.</summary>
        public void OnEnglishReloaded() => RaiseChanged();

        /// <summary>
        /// The language is about to be used (the overlay opens, a match starts, the plugin launched):
        /// fetch its snapshot if it is missing, stale, or — at launch — unchecked this session, and
        /// apply it. Idempotent and cheap to call often: one download in flight at a time, a failed
        /// attempt is not retried for five minutes, English needs nothing. Also picks up the site's
        /// vocabulary file once per launch. Any thread.
        /// </summary>
        public async Task EnsureDataAsync(bool launch = false)
        {
            try
            {
                if (!_termsChecked)
                {
                    _termsChecked = true;
                    if (await LocaleSync.EnsureTermsAsync().ConfigureAwait(false))
                        await _ui.InvokeAsync(() => { if (LocaleTerms.Load()) Apply(Web, force: true); });
                }

                string web = Web;
                if (web == Locales.English || _fetching) return;
                string englishHash = _config.LastDataHash ?? "";
                var known = LocaleSync.Ledger(web);
                bool stale = known == null || known.EnHash != englishHash;
                if (!launch && DataApplied && !stale) return;
                if (DateTime.UtcNow - _lastFetchFailure < TimeSpan.FromMinutes(5)) return;

                _fetching = true;
                try
                {
                    var outcome = await LocaleSync.EnsureSnapshotAsync(web, englishHash).ConfigureAwait(false);
                    if (outcome == LocaleSync.Outcome.Unavailable)
                    {
                        _lastFetchFailure = DateTime.UtcNow;
                        _log?.Invoke($"Card language: {web} snapshot unavailable (offline or no such locale)");
                        return;
                    }
                    if (outcome == LocaleSync.Outcome.Updated || !DataApplied)
                        await _ui.InvokeAsync(() => { if (Web == web) Apply(web, force: true); });
                    _log?.Invoke($"Card language: {web} snapshot {outcome.ToString().ToLowerInvariant()} — {_store.LocaleCount} cards translated (patch {_store.LocalePatch})");
                }
                finally { _fetching = false; }
            }
            catch (Exception ex) { _log?.Invoke("CardLanguage.EnsureDataAsync error: " + ex.Message); }
        }

        /// <summary>Pure: the effective language for a setting and a client code.</summary>
        public static string Resolve(string preference, string clientCode)
        {
            string pick = (preference ?? "").Trim();
            if (pick.Length == 0) return Locales.WebFromGame(clientCode);
            if (string.Equals(pick, Locales.English, StringComparison.OrdinalIgnoreCase)) return Locales.English;
            var l = Locales.ByWeb(pick);
            return l != null && l.HasCards ? l.Web : Locales.English;
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            string source = !string.IsNullOrEmpty(Preference) ? "setting" : ClientCode != null ? "client " + ClientCode : "default";
            string data = Web == Locales.English ? "English data"
                : DataApplied ? $"{_store.LocaleCount} cards translated (patch {_store.LocalePatch})"
                : "snapshot not downloaded yet";
            return $"{Web} ({source}) — {data}; vocabulary for {Vocab.Web}";
        }

        // Switch everything that depends on the language, in one place, then tell the surfaces.
        private void Apply(string web, bool force)
        {
            string terms = LocaleTerms.Generated;
            bool same = web == _appliedWeb && terms == _appliedTerms && (web == Locales.English || _store.LocaleWeb == web);
            if (!force && same) return;

            Web = web;
            _store.SetLocale(web);                 // the cached overlay, or English until the snapshot lands
            Vocab = LocaleTerms.For(web);
            Aliases.SetLocale(Vocab);
            SearchEngine.NameComparer = NameComparer;
            Ui.CardArt.ClearMemory();              // decoded bitmaps are keyed by language; disk caches stay
            _appliedWeb = web; _appliedTerms = terms;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { _log?.Invoke("CardLanguage.Changed handler error: " + ex.Message); }
        }
    }
}
