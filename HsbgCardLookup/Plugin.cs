using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.Plugins;
using HsbgCardLookup.Config;
using HsbgCardLookup.Data;
using HsbgCardLookup.Hotkey;
using HsbgCardLookup.Ui;

namespace HsbgCardLookup
{
    /// <summary>
    /// A single full-browser overlay summoned by a configurable hotkey (default F3; rebindable via
    /// the Settings button). Golden and focus keys are configurable too (defaults G/S), as is the
    /// Show-Duos toggle. See <see cref="SettingsWindow"/>.
    /// </summary>
    public class Plugin : IPlugin
    {
        private PluginConfig _config;
        private HotkeyManager _hotkey;
        private CardStore _store;
        private Dispatcher _ui;                                  // HDT's UI dispatcher
        private OverlayLarge _overlayLarge;
        private SettingsWindow _settings;
        private Game.GameStateProbe _probe;                     // read-only BG state logger (diagnostics)
        private Dictionary<Key, OverlayBase> _overlays;         // hotkey -> overlay (rebuilt on rewire)
        private readonly Dictionary<Key, DateTime> _lastToggle = new Dictionary<Key, DateTime>();

        public string Name => "HSBG Card Lookup";

        public string Description =>
            "Quick in-game search for Hearthstone Battlegrounds cards. " +
            "Press the hotkey to summon a search overlay over the game.";

        public string ButtonText => "Settings";

        public string Author => "hsbg.cards";

        public Version Version => new Version(0, 1, 0);

        // Return null to skip adding an item to HDT's Plugins menu (for now).
        public MenuItem MenuItem => null;

        public void OnLoad()
        {
            // OnLoad runs on HDT's WPF UI thread — capture its dispatcher and build the
            // (hidden) overlay windows here, on the right thread.
            _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _config = PluginConfig.Load();

            _store = new CardStore();
            _store.Load();
            Log("CardStore: " + _store.LoadInfo);

            _overlayLarge = new OverlayLarge(_store, _config);
            _probe = new Game.GameStateProbe(_store);

            _hotkey = new HotkeyManager();
            _hotkey.HotkeyPressed += OnHotkeyPressed;
            RewireHotkeys();
            _hotkey.Install();

            Log($"OnLoad  (overlay={_config.BrowserKey}, hook installed = {_hotkey.IsInstalled})");
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
        public void OnUpdate() { _probe?.Poll(); }

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
