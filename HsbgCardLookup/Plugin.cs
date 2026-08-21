using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using Hearthstone_Deck_Tracker.Plugins;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Hotkey;
using HsbgCardLookup.Net;
using HsbgCardLookup.Search;
using HsbgCardLookup.Ui;
using HsbgCardLookup.Update;

namespace HsbgCardLookup
{
    /// <summary>
    /// IPlugin entry point: a hotkey-summoned card-search overlay, plus background data/art refresh,
    /// GitHub auto-update, and the notifications bell.
    /// </summary>
    public class Plugin : IPlugin
    {
        private PluginConfig _config;
        private HotkeyManager _hotkey;
        private CardStore _store;
        private Dispatcher _ui;                                  // HDT's UI dispatcher
        private OverlayLarge _overlayLarge;
        private FloatingCardManager _floating;
        private Game.BgHud _bgHud;                               // always-on trinkets/anomaly HUD
        private Game.MatchRecorder _recorder;                    // opt-in per-match board CSV export
        private Game.BgMmr _bgMmr;                                // opt-in in-match opponent-MMR reader
        private Game.DarkGiftWatcher _darkGifts;                  // opt-in hover-summoned Dark Gift list
        private Ui.SearchButton _searchButton;                    // in-game 🔍 button by the card-list book
        private SettingsWindow _settings;
#if DEBUG
        private Game.GameStateProbe _probe;                     // read-only BG state logger (Debug-only diagnostics)
        private Game.DarkGiftProbe _giftProbe;                  // Dark Gift / Dark Discovery capture (Debug-only)
#endif
        private Dictionary<Key, OverlayBase> _overlays;         // hotkey -> overlay (rebuilt on rewire)
        private readonly Dictionary<Key, DateTime> _lastToggle = new Dictionary<Key, DateTime>();

        public string Name => "HSBG Card Lookup";

        public string Description =>
            "Quick in-game search for Hearthstone Battlegrounds cards. " +
            "Press the hotkey to summon a search overlay over the game.";

        public string ButtonText => "Settings";

        public string Author => "hsbg.cards";

        public Version Version => new Version(0, 3, 4, 1);

        // Shown under HDT's top-bar PLUGINS menu (returning null hides us there entirely — which is why
        // the menu read "EMPTY..."). A header named after the plugin with two actions; built lazily on the
        // UI thread (HDT reads this getter when it constructs the menu).
        private MenuItem _menuItem;
        public MenuItem MenuItem => _menuItem ?? (_menuItem = BuildMenuItem());

        private MenuItem BuildMenuItem()
        {
            var root = new MenuItem { Header = Name };

            var open = new MenuItem { Header = "Open overlay" };
            open.Click += (s, e) => _ui?.BeginInvoke(new Action(() =>
            {
                if (_overlays != null)
                    foreach (var w in _overlays.Values) { if (!ReferenceEquals(w, _overlayLarge)) w.HideIfOpen(); }
                _overlayLarge?.Toggle();
            }));

            var settings = new MenuItem { Header = "Settings" };
            settings.Click += (s, e) => _ui?.BeginInvoke(new Action(OpenSettings));

            var update = new MenuItem { Header = "Check for updates" };
            update.Click += (s, e) => _ui?.BeginInvoke(new Action(CheckForUpdatesInteractive));

            root.Items.Add(open);
            root.Items.Add(settings);
            root.Items.Add(update);
            return root;
        }

        public void OnLoad()
        {
            // Must be first: lets ImageSharp + its System.* closure resolve from our plugin folder
            // (the CLR probes HDT's app dir, not our subfolder).
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledAssembly;

            // OnLoad runs on HDT's WPF UI thread.
            _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _config = PluginConfig.Load();

            _store = new CardStore();
            _store.Load();
            Log("CardStore: " + _store.LoadInfo);

            Ui.CardArt.UseLocalArt = _config.UseLocalDevArt;   // dev-only local-PNG shortcut, off by default
            if (!string.IsNullOrWhiteSpace(_config.ArtCacheDir)) Ui.CardArt.CacheDir = _config.ArtCacheDir;  // user-relocated art cache
            Ui.WebpDecoder.Log = Log;

            _hotkey = new HotkeyManager();
            _hotkey.HotkeyPressed += OnHotkeyPressed;

            _floating = new FloatingCardManager(_config);
            _overlayLarge = new OverlayLarge(_store, _config, _hotkey, _floating, OpenSettings, CheckForUpdatesInteractive, Version.ToString());
            _bgHud = new Game.BgHud(_store, _config, _ui);
            _recorder = new Game.MatchRecorder(_store, _config, Log);
            _bgMmr = new Game.BgMmr(_config, _ui, Log);
            _darkGifts = new Game.DarkGiftWatcher(_store, _config, _ui, Log);
            _searchButton = new Ui.SearchButton(_config, ToggleOverlayFromButton, SkipVersion, Log);
            // Pre-realize the HWND so the first F3 summons in one press (no handle-creation race).
            new System.Windows.Interop.WindowInteropHelper(_overlayLarge).EnsureHandle();

#if DEBUG
            _probe = new Game.GameStateProbe(_store);
            _giftProbe = new Game.DarkGiftProbe(_store);
#endif

            RewireHotkeys();
            _hotkey.Install();

            Log($"OnLoad  (overlay={_config.BrowserKey}, hook installed = {_hotkey.IsInstalled})");

            // Background, best-effort (offline / failures are no-ops): data refresh, plugin
            // auto-update, notifications, and the card-art sync.
            Task.Run(() => RefreshDataAsync());
            Task.Run(() => CheckForUpdateAsync());
            Task.Run(() => CheckNoticesAsync());
            Task.Run(() => RefreshArtPackAsync());
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
                var found = new List<PluginNotice>();
                int id = _config.PatchNoticeLastSeenId + 1;
                int misses = 0, scanned = 0;
                while (misses < NoticeMaxGap && scanned < NoticeMaxScan)
                {
                    string url = AssetClient.SiteBase + "/plugin/notifications/" + id + ".json?_=" + DateTime.UtcNow.Ticks;
                    var n = await AssetClient.GetJsonAsync<PluginNotice>(url);
                    scanned++;
                    if (n == null) { misses++; id++; continue; }
                    misses = 0;
                    n.Id = id;                 // the filename is the authoritative id
                    found.Add(n);
                    id++;
                }
                if (found.Count == 0) return;
                _ui?.BeginInvoke(new Action(() => _overlayLarge?.SetNotices(found)));
                Log($"Notices: found {found.Count} (probed from id {_config.PatchNoticeLastSeenId + 1})");
            }
            catch (Exception ex) { Log("CheckNoticesAsync error: " + ex.Message); }
        }

        // Plugin folder via CodeBase (the real Plugins dir, surviving HDT's shadow-copy).
        private static string PluginDir()
        {
            try
            {
                var cb = System.Reflection.Assembly.GetExecutingAssembly().CodeBase;
                if (!string.IsNullOrEmpty(cb)) return Path.GetDirectoryName(new Uri(cb).LocalPath);
            }
            catch { }
            return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        // The exact assemblies we bundle; the resolver serves ONLY these (never HDT's own deps).
        private static readonly string[] BundledDeps =
        {
            "SixLabors.ImageSharp", "System.Buffers", "System.Memory", "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe", "System.Text.Encoding.CodePages"
        };
        private static readonly Dictionary<string, System.Reflection.Assembly> _resolvedDeps =
            new Dictionary<string, System.Reflection.Assembly>(StringComparer.OrdinalIgnoreCase);

        // Serve a bundled dep from the plugin folder, version-agnostically (AssemblyResolve return
        // values bypass the version check — required since a plugin can't add binding redirects).
        // Scoped to BundledDeps; only fires after normal resolution failed, so it never overrides HDT.
        private static System.Reflection.Assembly ResolveBundledAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new System.Reflection.AssemblyName(args.Name).Name;
                if (Array.IndexOf(BundledDeps, name) < 0) return null;   // only the names we bundle
                lock (_resolvedDeps)
                {
                    if (_resolvedDeps.TryGetValue(name, out var cached)) return cached;
                    var path = Path.Combine(PluginDir(), name + ".dll");
                    if (!File.Exists(path)) return null;
                    var asm = System.Reflection.Assembly.LoadFrom(path);
                    _resolvedDeps[name] = asm;
                    return asm;
                }
            }
            catch { return null; }
        }

        // ── Update state: one source of truth, pushed to every surface that can show it ──────────
        // (the F3 banner, the Settings "Updates" page, and the in-game badge by the search button —
        // see PushUpdateUi). A plain background check never pops a dialog; only a result that traces
        // back to something the user explicitly clicked (userInitiated) is allowed to.
        private Update.UpdateNotice _lastUpdateNotice;
        private double? _downloadProgress;   // non-null while a user-initiated download is in flight

        private async Task CheckForUpdateAsync()
        {
            try
            {
                var notice = await Updater.RunAsync(Version, _config);
                if (notice == null) return;
                Log("Update: " + (notice.AvailableForDownload
                    ? $"v{notice.AvailableVersion} available"
                    : notice.Message.Replace("\n", " | ")));
                _ui?.BeginInvoke(new Action(() => SetUpdateState(notice, userInitiated: false)));
            }
            catch (Exception ex) { Log("CheckForUpdateAsync error: " + ex.Message); }
        }

        // OnLoad checks exactly once, at launch — with no periodic re-check, a session that outlives
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

        // Set the current known state and push it to every surface. userInitiated gates the
        // restart-confirmation prompt: only a result that traces back to an explicit click (manual
        // check, or a Download that just finished) is allowed to pop it — a silent background check
        // finding a still-staged update from last session must never pop an unprompted dialog (that
        // can steal foreground from Hearthstone, which hides HDT's entire in-game overlay).
        private void SetUpdateState(Update.UpdateNotice notice, bool userInitiated)
        {
            _lastUpdateNotice = notice;
            _downloadProgress = null;
            PushUpdateUi();
            if (userInitiated && notice != null && notice.RestartReady) PromptRestart(notice);
        }

        private void SetDownloadProgress(double fraction)
        {
            _downloadProgress = fraction;
            PushUpdateUi();
        }

        // Renders _lastUpdateNotice/_downloadProgress onto every live surface. UI-thread only.
        private void PushUpdateUi()
        {
            var notice = _lastUpdateNotice;
            var progress = _downloadProgress;

            if (progress.HasValue)
                _overlayLarge?.SetUpdateProgressNotice(notice?.AvailableVersion, notice?.Url, progress.Value);
            else if (notice != null && notice.AvailableForDownload)
                _overlayLarge?.SetUpdateOfferNotice(notice.AvailableVersion, notice.Url,
                    () => StartDownload(notice), () => SkipVersion(notice.AvailableVersion));
            else if (notice != null)
                _overlayLarge?.SetUpdateNotice(notice.Message, notice.IsError, notice.Url, notice.LinkLabel);
            else
                _overlayLarge?.SetUpdateNotice(null, false, null);

            _settings?.RefreshUpdateStatus(notice, progress);
            _searchButton?.SetUpdateState(notice, progress);
        }

        private void PromptRestart(Update.UpdateNotice notice)
        {
            var ans = MessageBox.Show(notice.Message + "\n\nRestart HDT now to finish installing?",
                "HSBG Card Lookup — Update ready", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes) RestartHost();
        }

        // User-triggered "Check for updates" — wired to the HDT plugins-menu item, the overlay
        // toolbar's ⟳ icon, and the Settings "Updates" page. Bypasses the 6h throttle and always
        // gives feedback (incl. "up to date" / "couldn't check"). Runs off the UI thread.
        internal void CheckForUpdatesInteractive()
        {
            if (_overlayLarge?.IsVisible == true)
                _overlayLarge.SetUpdateNotice("Checking for updates…", false, null);
            Task.Run(async () =>
            {
                Update.UpdateNotice notice = null;
                try { notice = await Update.Updater.RunAsync(Version, _config, manual: true); }
                catch (Exception ex) { Log("CheckForUpdatesInteractive error: " + ex.Message); }
                _ui?.BeginInvoke(new Action(() => HandleManualNotice(notice)));
            });
        }

        // User's "Download" click, from any of the three surfaces — stages the release with a live
        // progress bar. Always resolves through SetUpdateState directly, NEVER through
        // HandleManualNotice's MessageBox fallback: a failure here must stay passive-only, since this
        // can be triggered from the in-game badge mid-match, where a MessageBox would steal foreground
        // from Hearthstone and hide HDT's whole overlay for something as minor as a failed download.
        // (A RestartReady success is still allowed to prompt — that's a direct, expected consequence of
        // the click, and proportionate: an actual restart is about to tear the overlay down anyway.)
        private void StartDownload(Update.UpdateNotice available)
        {
            SetDownloadProgress(0);
            var progress = new Progress<double>(f => _ui?.BeginInvoke(new Action(() => SetDownloadProgress(f))));
            Task.Run(async () =>
            {
                Update.UpdateNotice result;
                try { result = await Update.Updater.DownloadAsync(available, progress); }
                catch (Exception ex)
                {
                    Log("StartDownload error: " + ex.Message);
                    result = new Update.UpdateNotice($"Download failed. Install v{available.AvailableVersion} manually:",
                        true, available.Url);
                }
                _ui?.BeginInvoke(new Action(() => SetUpdateState(result, userInitiated: true)));
            });
        }

        private void SkipVersion(string version)
        {
            Update.Updater.Skip(_config, version);
            SetUpdateState(null, userInitiated: false);
        }

        // Show the manual-check result. Banner/Settings/badge always get it via SetUpdateState; a
        // MessageBox is ONLY added on top when the overlay is closed — reachable exclusively from
        // HDT's plugins-menu item (the toolbar ⟳ and Settings' button both require a window that's
        // already focused, so overlayOpen is true and this fallback never fires for them).
        private void HandleManualNotice(Update.UpdateNotice notice)
        {
            if (notice == null)
                notice = new Update.UpdateNotice("Couldn't check for updates right now — please try again later.", true);

            bool overlayOpen = _overlayLarge?.IsVisible == true;
            SetUpdateState(notice, userInitiated: true);

            if (notice.RestartReady) return;   // PromptRestart (inside SetUpdateState) already gave feedback
            if (overlayOpen) return;           // the banner already shows the result

            if (notice.AvailableForDownload)
            {
                var ans = MessageBox.Show(
                    $"Version {notice.AvailableVersion} is available.\n\nOpen the releases page in your browser?",
                    "HSBG Card Lookup — Updates", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                    try { System.Diagnostics.Process.Start(notice.Url); } catch { }
            }
            else if (!string.IsNullOrEmpty(notice.Url))
            {
                var ans = MessageBox.Show(notice.Message + "\n\nOpen the releases page in your browser?",
                    "HSBG Card Lookup — Updates", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ans == MessageBoxResult.Yes)
                    try { System.Diagnostics.Process.Start(notice.Url); } catch { }
            }
            else
            {
                MessageBox.Show(notice.Message, "HSBG Card Lookup — Updates",
                    MessageBoxButton.OK, notice.IsError ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }

        // Relaunch HDT to apply a staged update. HDT scans plugins only at startup, so finishing an
        // install needs a restart. We spawn a detached helper that waits for every HDT process to fully
        // exit, THEN copies the already-downloaded+staged files over the plugin folder (nothing is
        // locked anymore at that point — no live-swap of a loaded module), then starts the Squirrel
        // shim, then ask HDT to shut down (runs OnUnload). Mirrors restartHDT.ps1 plus the copy step.
        private void RestartHost()
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HearthstoneDeckTracker");
                var shim = Path.Combine(root, "HearthstoneDeckTracker.exe");
                if (!File.Exists(shim))   // fall back to the newest app-<ver> exe
                {
                    var newest = Directory.Exists(root)
                        ? Directory.GetDirectories(root, "app-*").OrderByDescending(d => d).FirstOrDefault()
                        : null;
                    if (newest != null) shim = Path.Combine(newest, "HearthstoneDeckTracker.exe");
                }
                if (!File.Exists(shim)) { Log("RestartHost: HDT exe not found"); return; }

                // Get-Process returning empty means the process object is gone, not that the OS has
                // necessarily finished releasing every file handle it held — observed live 2026-08-19:
                // Copy-Item hit a still-locked file right after the wait loop exited. By default that's
                // a NON-terminating PowerShell error, so it printed nothing (hidden window) and just
                // fell through to Start-Process, silently skipping the copy entirely. Fixed with a grace
                // period plus a bounded retry loop around the copy itself (~1s + up to 10×500ms ≈ 6s
                // worst case) rather than trusting the single wait-loop's timing.
                var waitClause = "while(Get-Process -Name HearthstoneDeckTracker -ErrorAction SilentlyContinue)"
                    + "{Start-Sleep -Milliseconds 300}; Start-Sleep -Milliseconds 1000; ";
                var stagedSrc = Update.Updater.ResolveStagedSourceRoot();
                var copyClause = (stagedSrc != null && Directory.Exists(stagedSrc))
                    ? "for($i=0; $i -lt 10; $i++) { try { Copy-Item -Path '" + stagedSrc
                        + "\\*' -Destination '" + PluginDir() + "' -Recurse -Force -ErrorAction Stop; break } "
                        + "catch { Start-Sleep -Milliseconds 500 } }; "
                    : "";

                // No "-WindowStyle Hidden" here — CreateNoWindow=true below already suppresses the
                // window at the OS process-creation level (CREATE_NO_WINDOW), before PowerShell's own
                // startup code would even run. Stacking both was flagged in a 2026-08-20 AV-heuristic
                // review as a specific "doubly-hidden window" signal some ML classifiers weight; dropping
                // the redundant one is a zero-behavior-change reduction (nothing was ever visible either way).
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \""
                        + waitClause + copyClause + "Start-Process '" + shim + "'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                Log("RestartHost: relauncher spawned" + (copyClause.Length > 0 ? " (will apply staged update)" : "")
                    + ", shutting down HDT");
                _ui?.BeginInvoke(new Action(() => Application.Current?.Shutdown()));
            }
            catch (Exception ex) { Log("RestartHost error: " + ex.Message); }
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
                    _store.Load();                 // now reads the freshly-written cache copy
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

        // The in-game 🔍 button's click — same behavior as the summon hotkey. Fires on the canvas
        // (HDT UI) thread; marshalled like every other overlay toggle for consistency.
        private void ToggleOverlayFromButton()
        {
            _ui?.BeginInvoke(new Action(() =>
            {
                if (_overlays != null)
                    foreach (var w in _overlays.Values) { if (!ReferenceEquals(w, _overlayLarge)) w.HideIfOpen(); }
                _overlayLarge?.Toggle();
            }));
        }

        private void ApplySettings()
        {
            _config.Save();
            RewireHotkeys();
            _overlayLarge?.RefreshPool();   // pick up a Show-Duos change live
            _floating?.OnSettingChanged();  // reconcile floating-card visibility (Hide-with-app toggle)
            _bgHud?.OnSettingsChanged();    // show/hide the trinkets/anomaly HUD per its toggles
            _bgMmr?.OnSettingsChanged();    // opponent-MMR reader on/off
            _darkGifts?.OnSettingsChanged(); // Dark Gift hover panel on/off
            _searchButton?.OnSettingsChanged(); // in-game search button on/off
        }

        public void OnUnload()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveBundledAssembly;
            _hotkey?.Uninstall();
            _ui?.Invoke(() =>
            {
                _settings?.Close(); _settings = null;
                _floating?.CloseAll();
                _bgHud?.CloseAll();
                _bgMmr?.CloseAll();
                _darkGifts?.CloseAll();
                _searchButton?.CloseAll();
                _overlayLarge?.Close();
                _overlays = null;
            });
            Log("OnUnload");
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
                target.Toggle();
            }));
        }

        public void OnButtonPress()
        {
            Log("OnButtonPress (settings)");
            _ui?.BeginInvoke(new Action(OpenSettings));
        }

        // Open (or focus) the settings window. Called from HDT's plugin button AND the overlay's gear
        // icon — both on the UI thread.
        private void OpenSettings()
        {
            if (_settings != null) { _settings.Activate(); return; }
            _settings = new SettingsWindow(_config, _hotkey, ApplySettings, on =>
            {
                _bgHud?.SetEditMode(on);
                _bgMmr?.SetEditMode(on);   // the MMR standings panel arranges alongside the HUD boxes
            }, Version.ToString(), CheckForUpdatesInteractive, StartDownload, SkipVersion,
            () => { if (_lastUpdateNotice != null) PromptRestart(_lastUpdateNotice); });
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
            _settings.RefreshUpdateStatus(_lastUpdateNotice, _downloadProgress);   // seed with what's already known
        }

        // Fires ~every 100 ms. Drives the read-only BG state probe (self-throttled to ~1.5s).
        public void OnUpdate()
        {
#if DEBUG
            _probe?.Poll();
            _giftProbe?.Poll();
#endif
            _bgHud?.Poll();      // throttled read of trinkets/anomaly → always-on HUD
            _recorder?.Poll();   // opt-in per-match board snapshots → CSV at match end
            _bgMmr?.Poll();      // opt-in in-match opponent-MMR reader
            _darkGifts?.Poll();  // opt-in Dark Gift list (shows while hovering the Dark Discovery button)
            _searchButton?.Poll(); // in-game 🔍 button by the card-list book (shows during a BG match)
            PollBackgroundUpdateCheck(); // re-attempt every 20 min so a long session isn't frozen at launch-time state
        }

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup");

        private static void Log(string message)
        {
            // A plugin lifecycle method must never throw — that can destabilize HDT.
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(
                    Path.Combine(LogDir, "spike.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // swallow
            }
        }
    }
}
