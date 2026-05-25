using System;
using System.IO;
using System.Windows.Input;
using System.Xml.Serialization;

namespace HsbgCardLookup.Config
{
    /// <summary>
    /// Persisted plugin settings. Stored as XML under the plugin's data folder so it
    /// survives DLL redeploys (which only replace the bin output).
    /// </summary>
    public class PluginConfig
    {
        // Hotkey to summon the overlay (WPF Key enum name). Default F3.
        // (BrowserKey kept as the name for config back-compat with earlier builds.)
        public string BrowserKey { get; set; } = "F3";

        // Key to toggle golden art in the detail pane (default G).
        public string GoldenKey { get; set; } = "G";

        // Key to re-focus the search box (default S). Only acts when search isn't already focused.
        public string FocusKey { get; set; } = "S";

        // Whether duos-only cards appear in results/browse (default: shown).
        public bool ShowDuos { get; set; } = true;

        [XmlIgnore] public Key BrowserKeyParsed => Enum.TryParse(BrowserKey, out Key k) ? k : Key.F3;
        [XmlIgnore] public Key GoldenKeyParsed => Enum.TryParse(GoldenKey, out Key k) ? k : Key.G;
        [XmlIgnore] public Key FocusKeyParsed => Enum.TryParse(FocusKey, out Key k) ? k : Key.S;

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup");

        private static string FilePath => Path.Combine(Dir, "config.xml");

        public static PluginConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                        return (PluginConfig)new XmlSerializer(typeof(PluginConfig)).Deserialize(fs);
                }
            }
            catch { /* fall through to defaults */ }
            return new PluginConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                using (var fs = File.Create(FilePath))
                    new XmlSerializer(typeof(PluginConfig)).Serialize(fs, this);
            }
            catch { /* never throw from a plugin */ }
        }
    }
}
