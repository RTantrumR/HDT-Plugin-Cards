using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Host;
using HsbgCardLookup.Hotkey;
using HsbgCardLookup.Localization;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;
using HsbgCardLookup.Ui;
using HsbgCardLookup.Update;

namespace HsbgCardLookup
{
    /// <summary>
    /// The toolkit itself, whatever hosts it: the hotkey-summoned card search, the in-match features,
    /// the settings window, background data/art refresh, the notify-only update check and the
    /// notifications bell. A host installs the seams (<see cref="HostServices"/>), then calls
    /// <see cref="Start"/> once on its UI thread, <see cref="Update"/> about every 100 ms and
    /// <see cref="Stop"/> on the way out. The HDT plugin (Plugin.cs) and the standalone exe are the two
    /// hosts today.
    /// </summary>
    public sealed class ToolkitApp
    {
        public const string DisplayName = "Tantrum's Battlegrounds Toolkit";

        private readonly Version _version;
        private readonly Action<string> _log;

        private PluginConfig _config;
        private HotkeyManager _hotkey;
        private CardStore _store;
        private Dispatcher _ui;
        private OverlayLarge _overlayLarge;
        private FloatingCardManager _floating;
        private Game.BgHud _bgHud;                               // always-on trinkets/anomaly HUD
        private Game.BgMmr _bgMmr;                                // opt-in in-match opponent-MMR reader
        private Game.DarkGiftWatcher _darkGifts;                  // opt-in Dark Gift list (“?” marker by the button)
        private Ui.ArrangeBanner _arrangeBanner;                  // in-game strip shown while positioning
        private Ui.SearchButton _searchButton;                    // in-game 🔍 button by the card-list book
        private SettingsWindow _settings;
        private CardLanguage _cardLanguage;                      // game content: cards, art, the game's words
        private Dictionary<Key, OverlayBase> _overlays;         // hotkey -> overlay (rebuilt on rewire)
        private readonly Dictionary<Key, DateTime> _lastToggle = new Dictionary<Key, DateTime>();

        /// <summary>The language was applied (UI thread) — a host relabels what it built itself.</summary>
        public event Action LanguageChanged;

        public ToolkitApp(Version version, Action<string> log)
        {
            _version = version;
            _log = log ?? (s => { });
        }

        public CardStore Store => _store;

        private void Log(string message) => _log(message);

        /// <summary>Once, on the host's WPF UI thread, after the seams are installed.</summary>
        public void Start(Dispatcher ui)
        {
            _ui = ui;
            _config = PluginConfig.Load();

            _store = new CardStore();
            _store.Load();
            Log("CardStore: " + _store.LoadInfo);

            // The language, before the UI is built so its first render has the right words: the
            // plugin's catalog, the store's overlay from cache, the search words, the art keys — no
            // network; the launch chain below pulls the snapshot once the English data has refreshed.
            _cardLanguage = new CardLanguage(_store, _config, _ui, Log);
            _cardLanguage.Initialize();
            Ui.DarkGiftPanel.SetKeywordHighlight(_cardLanguage.Vocab.Keyword.Values);
            Game.DarkGiftWatcher.TribeLabel = t => _cardLanguage.Vocab.TribeLabel(t) ?? t;   // reads the live vocabulary

            Ui.CardArt.UseLocalArt = _config.UseLocalDevArt;   // dev-only local-PNG shortcut, off by default
            if (!string.IsNullOrWhiteSpace(_config.ArtCacheDir)) Ui.CardArt.CacheDir = _config.ArtCacheDir;  // user-relocated art cache
            Ui.WebpDecoder.Log = Log;

            _hotkey = new HotkeyManager();
            _hotkey.HotkeyPressed += OnHotkeyPressed;

            _floating = new FloatingCardManager(_config);
            _overlayLarge = new OverlayLarge(_store, _config, _cardLanguage, _hotkey, _floating, OpenSettings, CheckForUpdatesInteractive, _version.ToString());
            _bgHud = new Game.BgHud(_store, _config, _ui);
            _bgMmr = new Game.BgMmr(_config, _ui, Log, HeroDisplayName);
            _darkGifts = new Game.DarkGiftWatcher(_store, _config, _ui, Log);
            _searchButton = new Ui.SearchButton(_config, ToggleSearch, SkipVersion, Log);
            // Pre-realize the HWND so the first F3 summons in one press (no handle-creation race).
            new System.Windows.Interop.WindowInteropHelper(_overlayLarge).EnsureHandle();

            RewireHotkeys();
            _hotkey.Install();

            // Every surface that shows card names, art or the plugin's own words follows a language
            // change through this event — the one place the fan-out lives.
            _cardLanguage.Changed += OnCardLanguageChanged;

            Log($"OnLoad  (overlay={_config.BrowserKey}, hook installed = {_hotkey.IsInstalled})");

            // Background, best-effort (offline / failures are no-ops): data refresh, the update
            // check (notify-only), notifications, and the card-art sync. The art sync runs AFTER
            // the data refresh, not beside it: it diffs the manifest against the cards the store
            // knows, so on the launch that brings new cards it must see the new list, or their art
            // waits for the next launch (or first view).
            Task.Run(async () => { await RefreshDataAsync(); await _cardLanguage.EnsureDataAsync(launch: true); await RefreshArtPackAsync(); });
            Task.Run(() => CheckForUpdateAsync());
            Task.Run(() => CheckNoticesAsync());
        }

        /// <summary>About every 100 ms, on the UI thread.</summary>
        public void Update()
        {
            _bgHud?.Poll();      // throttled read of trinkets/anomaly → always-on HUD
            _bgMmr?.Poll();      // opt-in in-match opponent-MMR reader
            _darkGifts?.Poll();  // opt-in Dark Gift list (“?” marker above the Dark Discovery button)
            _searchButton?.Poll(); // in-game 🔍 button by the card-list book (shows during a BG match)
            PollBackgroundUpdateCheck(); // re-attempt every 20 min so a long session isn't frozen at launch-time state
            _cardLanguage?.PollHdt();      // following the host's language setting: re-read it once a minute
            EnsureCardDataWhenInMatch();   // a match is on and the card language's data isn't here yet → fetch it
        }

        /// <summary>On the way out; any thread.</summary>
        public void Stop()
        {
            _hotkey?.Uninstall();
            _ui?.Invoke(() =>
            {
                _settings?.Close(); _settings = null;
                Ui.ArrangeSession.Set(Ui.ArrangeTarget.None);   // never leave a session latched
                _arrangeBanner?.Close(); _arrangeBanner = null;
                _floating?.CloseAll();
                _bgHud?.CloseAll();
                _bgMmr?.CloseAll();
                _darkGifts?.CloseAll();
                _searchButton?.CloseAll();
                _overlayLarge?.Close();
                _overlays = null;
            });
        }

        private async Task RefreshArtPackAsync()
        {
            try
            {
                bool updated = await Ui.ArtPack.EnsureAsync(_store, _config);
                if (updated)
                {
                    _ui?.BeginInvoke(new Action(() => _overlayLarge?.RefreshPool()));
                    Log("Art pack updated + unpacked");
                }
            }
            catch (Exception ex) { Log("RefreshArtPackAsync error: " + ex.Message); }
        }

        // Notices are one-per-file at /plugin/notifications/{id}.json; no directory listing, so probe
        // ids upward from the last-seen one, tolerating a few gaps (removed files).
        private const int NoticeMaxGap = 5;
        private const int NoticeMaxScan = 100;

        private async Task CheckNoticesAsync()
        {
            try
            {
                // One cacheable request: the site folds every active notice, in every language, into
                // feed.json. The per-id probe is the pre-feed path, kept only for a site that has not
                // shipped the feed yet (no feed → probe), never run beside it.
                var feed = await AssetClient.GetJsonAsync<PluginNoticeFeed>(AssetClient.SiteBase + "/plugin/notifications/feed.json");
                List<PluginNotice> found;
                if (feed?.Notices != null)
                {
                    found = feed.Notices.Where(n => n != null && n.Id > 0).OrderBy(n => n.Id).ToList();
                    Log($"Notices: feed has {found.Count} (generated {feed.GeneratedAt ?? "?"})");
                }
                else { Log("Notices: no feed yet (site not deployed, or offline) — probing ids"); found = await ProbeNoticesAsync(); }
                if (found.Count == 0) return;
                _ui?.BeginInvoke(new Action(() => _overlayLarge?.SetNotices(found)));
            }
            catch (Exception ex) { Log("CheckNoticesAsync error: " + ex.Message); }
        }

        // Pre-feed path: ids upward from the last-seen one, each its own file.
        private async Task<List<PluginNotice>> ProbeNoticesAsync()
        {
            var found = new List<PluginNotice>();
            int id = _config.PatchNoticeLastSeenId + 1;
            int misses = 0, scanned = 0;
            while (misses < NoticeMaxGap && scanned < NoticeMaxScan)
            {
                var n = await AssetClient.GetJsonAsync<PluginNotice>(AssetClient.SiteBase + "/plugin/notifications/" + id + ".json");
                scanned++;
                if (n == null) { misses++; id++; continue; }
                misses = 0;
                n.Id = id;                 // the filename is the authoritative id
                found.Add(n);
                id++;
            }
            if (found.Count > 0) Log($"Notices: probed {found.Count} (from id {_config.PatchNoticeLastSeenId + 1})");
            return found;
        }

        // ── Update state: one source of truth, pushed to every surface that can show it ──────────
        // (the F3 banner, the Settings "Updates" page, and the in-game badge by the search button —
        // see PushUpdateUi). Since v0.4.0 the updater only ever NOTIFIES — installing is the user's
        // own browser + the Setup wizard (see Updater's class comment for why the in-plugin
        // download/stage/apply pipeline was removed). A background check never pops a dialog; the
        // only MessageBox left is HandleManualNotice's plugins-menu fallback.
        private UpdateNotice _lastUpdateNotice;

        private async Task CheckForUpdateAsync()
        {
            try
            {
                var notice = await Updater.RunAsync(_version, _config);
                if (notice == null) return;
                Log("Update: " + (notice.AvailableForDownload
                    ? $"v{notice.AvailableVersion} available"
                    : notice.Message.Replace("\n", " | ")));
                _ui?.BeginInvoke(new Action(() => SetUpdateState(notice)));
            }
            catch (Exception ex) { Log("CheckForUpdateAsync error: " + ex.Message); }
        }

        // Start checks exactly once, at launch — with no periodic re-check, a session that outlives
        // that check's 6h throttle window (very common — BG sessions run 30min-4h, and plenty of
        // people leave HDT running for days) would never learn about a release that ships mid-session.
        // Re-attempt every 20 minutes of uptime; Updater.RunAsync's own persisted 6h throttle is the
        // real gate, so ~17 of every 18 attempts return null immediately with no network call at all.
        private DateTime _lastUpdateAttempt = DateTime.MinValue;
        private void PollBackgroundUpdateCheck()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUpdateAttempt).TotalMinutes < 20) return;
            _lastUpdateAttempt = now;
            Task.Run(() => CheckForUpdateAsync());
        }

        // Set the current known state and push it to every surface.
        private void SetUpdateState(UpdateNotice notice)
        {
            _lastUpdateNotice = notice;
            PushUpdateUi();
        }

        // Renders _lastUpdateNotice onto every live surface. UI-thread only.
        private void PushUpdateUi()
        {
            var notice = _lastUpdateNotice;

            if (notice != null && notice.AvailableForDownload)
                _overlayLarge?.SetUpdateOfferNotice(notice.AvailableVersion, notice.Url,
                    () => OpenDownloadPage(notice.DownloadUrl), () => SkipVersion(notice.AvailableVersion));
            else if (notice != null)
                _overlayLarge?.SetUpdateNotice(notice.Message, notice.IsError, notice.Url, notice.LinkLabel);
            else
                _overlayLarge?.SetUpdateNotice(null, false, null);

            _settings?.RefreshUpdateStatus(notice);
            _searchButton?.SetUpdateState(notice);
        }

        // The user's whole "get the update" action: open the release's Setup wizard download in the
        // browser (the release page when a release ships none) — the browser (with its own
        // MotW/SmartScreen handling) is the delivery channel. Deliberately does NOT clear the offer:
        // it stays until the user skips it or actually installs (the version bump clears it on the
        // next launch).
        private void OpenDownloadPage(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    url ?? "https://github.com/" + Updater.GitHubRepo + "/releases/latest");
            }
            catch (Exception ex) { Log("OpenDownloadPage error: " + ex.Message); }
        }

        // User-triggered "Check for updates" — wired to the host's menu, the overlay toolbar's ⟳
        // icon, and the Settings "Updates" page. Bypasses the 6h throttle and always gives feedback
        // (incl. "up to date" / "couldn't check"). Runs off the UI thread.
        public void CheckForUpdatesInteractive()
        {
            if (_overlayLarge?.IsVisible == true)
                _overlayLarge.SetUpdateNotice(Strings.T("plugin.checkingForUpdates"), false, null);
            Task.Run(async () =>
            {
                UpdateNotice notice = null;
                try { notice = await Updater.RunAsync(_version, _config, manual: true); }
                catch (Exception ex) { Log("CheckForUpdatesInteractive error: " + ex.Message); }
                _ui?.BeginInvoke(new Action(() => HandleManualNotice(notice)));
            });
        }

        private void SkipVersion(string version)
        {
            Updater.Skip(_config, version);
            SetUpdateState(null);
        }

        // Show the manual-check result. Banner/Settings/badge always get it via SetUpdateState; a
        // MessageBox is ONLY added on top when the overlay is closed — reachable exclusively from
        // the host's menu (the toolbar ⟳ and Settings' button both require a window that's
        // already focused, so overlayOpen is true and this fallback never fires for them).
        private void HandleManualNotice(UpdateNotice notice)
        {
            if (notice == null)
                notice = new UpdateNotice("plugin.updateCheckFailed", new object[0], true, null);

            bool overlayOpen = _overlayLarge?.IsVisible == true;
            SetUpdateState(notice);

            if (overlayOpen) return;           // the banner already shows the result

            string title = Strings.F("plugin.updatesTitle", DisplayName);
            if (notice.AvailableForDownload)
            {
                var ans = MessageBox.Show(
                    Strings.F("plugin.openReleasesPrompt", Strings.F("updates.versionAvailable", notice.AvailableVersion)),
                    title, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                    try { System.Diagnostics.Process.Start(notice.Url); } catch { }
            }
            else if (!string.IsNullOrEmpty(notice.Url))
            {
                var ans = MessageBox.Show(Strings.F("plugin.openReleasesPrompt", notice.Message),
                    title, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                    try { System.Diagnostics.Process.Start(notice.Url); } catch { }
            }
            else
            {
                MessageBox.Show(notice.Message, title,
                    MessageBoxButton.OK, notice.IsError ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }

        // Refresh card data from the API when its content hash changes (patch-independent, since the
        // site edits data between patches); persist to cache and reload the overlay.
        private async Task RefreshDataAsync()
        {
            try
            {
                var json = await AssetClient.GetStringAsync(AssetClient.SiteBase + "/api/cards");
                if (string.IsNullOrEmpty(json)) return;

                CardsFile file;
                try { file = JsonConvert.DeserializeObject<CardsFile>(json); }
                catch { return; }
                if (file?.Cards == null || file.Cards.Count == 0) return;

                // Hash {patch,cards} only — the API's volatile fetchedAt would otherwise churn it.
                string hash = ContentHash(JsonConvert.SerializeObject(file));
                if (string.Equals(hash, _config.LastDataHash, StringComparison.Ordinal))
                {
                    Log($"Data check: unchanged (patch {file.Patch})");
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(CardStore.CachePath));
                File.WriteAllText(CardStore.CachePath, json);
                _config.LastDataHash = hash;
                _config.Save();

                _ui?.Invoke(() =>
                {
                    _store.Load();                 // now reads the freshly-written cache copy (and re-lays the card language)
                    _cardLanguage?.OnEnglishReloaded();
                    _overlayLarge?.RefreshPool();
                });
                Log($"Data refreshed: patch {file.Patch} ({_store.LoadInfo})");
            }
            catch (Exception ex) { Log("RefreshDataAsync error: " + ex.Message); }
        }

        private static string ContentHash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
        }

        /// <summary>(Re)build the hotkey->overlay map and the hook's registered keys from config.</summary>
        private void RewireHotkeys()
        {
            var key = _config.BrowserKeyParsed;
            _overlays = new Dictionary<Key, OverlayBase>();
            if (key != Key.None) _overlays[key] = _overlayLarge;   // unbound = not registered
            _hotkey.ClearKeys();
            foreach (var k in _overlays.Keys) _hotkey.AddKey(k);
        }

        /// <summary>Open or close the card search — the in-game 🔍 button, a host's menu. Any thread.</summary>
        public void ToggleSearch()
        {
            _ui?.BeginInvoke(new Action(() =>
            {
                if (_overlays != null)
                    foreach (var w in _overlays.Values) { if (!ReferenceEquals(w, _overlayLarge)) w.HideIfOpen(); }
                NoteCardsNeeded();
                _overlayLarge?.Toggle();
            }));
        }

        // ── Languages ────────────────────────────────────────────────────────────────────────────

        // Hero names for the standings panel, in the card language (null → the host's English name).
        private string HeroDisplayName(string cardId) => _store?.Lookup(cardId)?.DisplayName;

        // The language switched (setting, client, or its snapshot arrived): everything that shows
        // card names, art, the game's words or the plugin's own texts follows — what is built once
        // relabels, what is cached is re-pushed. UI thread.
        private void OnCardLanguageChanged()
        {
            Ui.DarkGiftPanel.SetKeywordHighlight(_cardLanguage.Vocab.Keyword.Values);
            LanguageChanged?.Invoke();
            _overlayLarge?.OnLanguageChanged();
            _darkGifts?.OnLanguageChanged();
            _bgHud?.OnLanguageChanged();
            _bgMmr?.OnLanguageChanged();
            _searchButton?.Relabel();
            _arrangeBanner?.Relabel();
            _settings?.OnLanguageChanged();
            PushUpdateUi();   // the cached notice renders through its catalog key
            // The search is open while the language switched: that is the language being USED, so
            // pull its snapshot now — otherwise the open grid read English until the next open.
            if (_overlayLarge?.IsVisible == true) NoteCardsNeeded();
            Log("Language applied: " + _cardLanguage.Describe());
        }

        // The card language's snapshot is pulled only when the language is actually used — never on
        // a settings flip. "Used" = the card search is opened or already open while the language
        // changes, or a Battlegrounds match is on (Update). The service dedupes and backs off;
        // this is a cheap hint.
        private void NoteCardsNeeded()
        {
            if (_cardLanguage == null || _cardLanguage.DataApplied) return;
            Task.Run(() => _cardLanguage.EnsureDataAsync());
        }

        private DateTime _lastDataNeedCheck = DateTime.MinValue;
        private void EnsureCardDataWhenInMatch()
        {
            if (_cardLanguage == null || _cardLanguage.DataApplied) return;
            var now = DateTime.UtcNow;
            if ((now - _lastDataNeedCheck).TotalSeconds < 5) return;
            _lastDataNeedCheck = now;
            bool inBg = false;
            try { var g = HostServices.Game; inBg = g != null && g.IsBattlegroundsMatch; } catch { }
            if (inBg) Task.Run(() => _cardLanguage.EnsureDataAsync());
        }

        private void ApplySettings()
        {
            _config.Save();
            _cardLanguage?.Refresh();   // no-op unless the resolved language changed; a change applies from cache, downloads nothing
            RewireHotkeys();
            _overlayLarge?.RefreshPool();   // pick up a Show-Duos change live
            _floating?.OnSettingChanged();  // reconcile floating-card visibility (Hide-with-app toggle)
            ReapplyFeatures();
        }

        // Re-evaluate every canvas feature against the current settings AND the current arrange
        // session. Split out of ApplySettings so entering/leaving arrange can reuse it WITHOUT
        // _config.Save() — that separation is what keeps a temporary force-on out of config.xml.
        private void ReapplyFeatures()
        {
            _bgHud?.OnSettingsChanged();    // show/hide the trinkets/anomaly HUD per its toggles
            _bgMmr?.OnSettingsChanged();    // opponent-MMR reader on/off
            _darkGifts?.OnSettingsChanged(); // Dark Gift panel on/off
            _searchButton?.OnSettingsChanged(); // in-game search button on/off
        }

        // Enter/leave arrange mode for ONE feature. Everything else on the canvas stands down for the
        // duration (see ArrangeSession), and Hearthstone is brought forward because the overlay only
        // draws while the game has focus — otherwise the user would click Arrange and see nothing.
        private void SetArrangeMode(Ui.ArrangeTarget target)
        {
            Ui.ArrangeSession.Set(target);
            if (target != Ui.ArrangeTarget.None)
            {
                // The host's own helper: it restores a minimized window and satisfies Windows'
                // foreground lock, which a bare SetForegroundWindow from a background process does not.
                try { HostServices.Shell?.BringHearthstoneToForeground(); } catch { }
            }
            _bgHud?.SetArrange(target);
            _bgMmr?.SetArrange(target);
            ReapplyFeatures();

            // A Done button on the overlay itself: clicking back into the settings window would take
            // focus off Hearthstone, and the overlay hides the moment that happens.
            if (_arrangeBanner == null) _arrangeBanner = new Ui.ArrangeBanner(() => _settings?.EndArrangeFromOverlay());
            _arrangeBanner.Show(target);
        }

        private void OnHotkeyPressed(Key key, string foreground)
        {
            Log($"Hotkey {key} pressed  (foreground = {foreground})");

            // Debounce per key: auto-repeat sends repeated WM_KEYDOWNs while held.
            var now = DateTime.UtcNow;
            if (_lastToggle.TryGetValue(key, out DateTime last) && (now - last).TotalMilliseconds < 300)
                return;
            _lastToggle[key] = now;

            // Hook callback should return fast; marshal UI work asynchronously.
            _ui?.BeginInvoke(new Action(() =>
            {
                if (_overlays == null || !_overlays.TryGetValue(key, out OverlayBase target)) return;
                // Only one variant open at a time — hide the others, then toggle this one.
                foreach (var w in _overlays.Values)
                    if (!ReferenceEquals(w, target)) w.HideIfOpen();
                NoteCardsNeeded();
                target.Toggle();
            }));
        }

        /// <summary>Open (or focus) the settings window. UI thread.</summary>
        public void OpenSettings()
        {
            // Show(), not just Activate(): the window hides itself while a HUD is being arranged, and
            // Activate() alone would do nothing visible.
            if (_settings != null) { _settings.Show(); _settings.Activate(); return; }
            _settings = new SettingsWindow(_config, _store, _cardLanguage, _hotkey, ApplySettings, SetArrangeMode,
            _version.ToString(), CheckForUpdatesInteractive,
            n => OpenDownloadPage(n?.DownloadUrl), SkipVersion);
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
            _settings.RefreshUpdateStatus(_lastUpdateNotice);   // seed with what's already known
        }
    }
}
