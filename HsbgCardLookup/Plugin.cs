using System;
using System.Collections.Generic;
using System.IO;
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
        private SettingsWindow _settings;
#if DEBUG
        private Game.GameStateProbe _probe;                     // read-only BG state logger (Debug-only diagnostics)
#endif
        private Dictionary<Key, OverlayBase> _overlays;         // hotkey -> overlay (rebuilt on rewire)
        private readonly Dictionary<Key, DateTime> _lastToggle = new Dictionary<Key, DateTime>();

        public string Name => "HSBG Card Lookup";

        public string Description =>
            "Quick in-game search for Hearthstone Battlegrounds cards. " +
            "Press the hotkey to summon a search overlay over the game.";

        public string ButtonText => "Settings";

        public string Author => "hsbg.cards";

        public Version Version => new Version(0, 1, 1);

        public MenuItem MenuItem => null;

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
            Ui.WebpDecoder.Log = Log;

            _hotkey = new HotkeyManager();
            _hotkey.HotkeyPressed += OnHotkeyPressed;

            _overlayLarge = new OverlayLarge(_store, _config, _hotkey);
            // Pre-realize the HWND so the first F3 summons in one press (no handle-creation race).
            new System.Windows.Interop.WindowInteropHelper(_overlayLarge).EnsureHandle();

#if DEBUG
            _probe = new Game.GameStateProbe(_store);
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

        private async Task CheckForUpdateAsync()
        {
            try
            {
                var notice = await Updater.RunAsync(Version, PluginDir(), _config);
                if (notice == null) return;
                Log("Update: " + notice.Message.Replace("\n", " | "));
                _ui?.BeginInvoke(new Action(() =>
                    _overlayLarge?.SetUpdateNotice(notice.Message, notice.IsError, notice.Url)));
            }
            catch (Exception ex) { Log("CheckForUpdateAsync error: " + ex.Message); }
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

        private void ApplySettings()
        {
            _config.Save();
            RewireHotkeys();
            _overlayLarge?.RefreshPool();   // pick up a Show-Duos change live
        }

        public void OnUnload()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveBundledAssembly;
            _hotkey?.Uninstall();
            _ui?.Invoke(() =>
            {
                _settings?.Close(); _settings = null;
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
            _ui?.BeginInvoke(new Action(() =>
            {
                if (_settings != null) { _settings.Activate(); return; }
                _settings = new SettingsWindow(_config, _hotkey, ApplySettings);
                _settings.Closed += (s, e) => _settings = null;
                _settings.Show();
            }));
        }

        // Fires ~every 100 ms. Drives the read-only BG state probe (self-throttled to ~1.5s).
        public void OnUpdate()
        {
#if DEBUG
            _probe?.Poll();
#endif
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
