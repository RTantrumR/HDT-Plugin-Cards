using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearthstone_Deck_Tracker.Plugins;
using HsbgCardLookup.Localization;

namespace HsbgCardLookup
{
    /// <summary>
    /// The HDT host: the IPlugin entry point. Installs the HDT seams, then hands everything else to
    /// <see cref="ToolkitApp"/> — the same toolkit the standalone exe runs. What stays here is HDT's
    /// alone: the plugin identity and its plugins.xml name shim, HDT's PLUGINS menu, the assembly
    /// resolver for our bundled DLLs, the spike.log location and the Debug-only HDT probes.
    /// </summary>
    public class Plugin : IPlugin
    {
        private Dispatcher _ui;                                  // HDT's UI dispatcher
        private ToolkitApp _app;
#if DEBUG
        private Host.Hdt.GameStateProbe _probe;                     // read-only BG state logger (Debug-only diagnostics)
        private Host.Hdt.DarkGiftProbe _giftProbe;                  // Dark Gift / Dark Discovery capture (Debug-only)
#endif
        private MenuItem _menuOpen, _menuSettings, _menuUpdate;  // HDT plugins-menu items, relabelled on a language change

        internal const string DisplayName = ToolkitApp.DisplayName;
        private const string LegacyName = "HSBG Card Lookup";            // the name up to v0.6.1

        // HDT keys a plugin's enabled state in plugins.xml by file path + Name, and a plugin it can't
        // match starts DISABLED — so a plain rename would switch us off for everyone updating. Answer
        // to the old name while plugins.xml still has us enabled under it; OnLoad then switches to the
        // new one, and HDT's next settings save writes that.
        private string _name = EnabledUnderLegacyName() ? LegacyName : DisplayName;
        public string Name => _name;

        private static bool EnabledUnderLegacyName()
        {
            try
            {
                var path = Path.Combine(Hearthstone_Deck_Tracker.Config.Instance.ConfigDir, "plugins.xml");
                if (!File.Exists(path)) return false;
                return System.Xml.Linq.XDocument.Load(path).Descendants("PluginSettings").Any(p =>
                    (string)p.Element("Name") == LegacyName && (string)p.Element("IsEnabled") == "true");
            }
            catch { return false; }
        }

        public string Description => Strings.T("plugin.description");

        public string ButtonText => Strings.T("plugin.settings");

        public string Author => "hsbg.cards";

        public Version Version => new Version(0, 8, 2);

        // Shown under HDT's top-bar PLUGINS menu (returning null hides us there entirely — which is why
        // the menu read "EMPTY..."). A header named after the plugin with three actions; built lazily on
        // the UI thread (HDT reads this getter when it constructs the menu).
        private MenuItem _menuItem;
        public MenuItem MenuItem => _menuItem ?? (_menuItem = BuildMenuItem());

        private MenuItem BuildMenuItem()
        {
            var root = new MenuItem { Header = DisplayName };

            _menuOpen = new MenuItem { Header = Strings.T("plugin.menu.openOverlay") };
            _menuOpen.Click += (s, e) => _app?.ToggleSearch();

            _menuSettings = new MenuItem { Header = Strings.T("plugin.settings") };
            _menuSettings.Click += (s, e) => _ui?.BeginInvoke(new Action(() => _app?.OpenSettings()));

            _menuUpdate = new MenuItem { Header = Strings.T("plugin.menu.checkForUpdates") };
            _menuUpdate.Click += (s, e) => _ui?.BeginInvoke(new Action(() => _app?.CheckForUpdatesInteractive()));

            root.Items.Add(_menuOpen);
            root.Items.Add(_menuSettings);
            root.Items.Add(_menuUpdate);
            return root;
        }

        private void RelabelMenu()
        {
            if (_menuOpen != null) _menuOpen.Header = Strings.T("plugin.menu.openOverlay");
            if (_menuSettings != null) _menuSettings.Header = Strings.T("plugin.settings");
            if (_menuUpdate != null) _menuUpdate.Header = Strings.T("plugin.menu.checkForUpdates");
        }

        public void OnLoad()
        {
            // Must be first: lets ImageSharp + its System.* closure resolve from our plugin folder
            // (the CLR probes HDT's app dir, not our subfolder).
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledAssembly;

            // The features reach HDT only through the host seams (Host\Seams.cs); install them before
            // anything that reads game state, the overlay canvas or HDT's language is built.
            Host.Hdt.HdtHost.Install();

            _name = DisplayName;   // HDT has matched plugins.xml by now (see Name)

            // OnLoad runs on HDT's WPF UI thread.
            _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _app = new ToolkitApp(Version, Log);
            _app.LanguageChanged += RelabelMenu;
            _app.Start(_ui);

#if DEBUG
            _probe = new Host.Hdt.GameStateProbe(_app.Store);
            _giftProbe = new Host.Hdt.DarkGiftProbe(_app.Store);
#endif
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

        public void OnUnload()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveBundledAssembly;
            _app?.Stop();
            Log("OnUnload");
        }

        public void OnButtonPress()
        {
            Log("OnButtonPress (settings)");
            _ui?.BeginInvoke(new Action(() => _app?.OpenSettings()));
        }

        // Fires ~every 100 ms.
        public void OnUpdate()
        {
#if DEBUG
            _probe?.Poll();
            _giftProbe?.Poll();
#endif
            _app?.Update();
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
