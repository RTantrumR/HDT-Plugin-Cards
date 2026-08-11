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
        // Summon hotkey (WPF Key name). Name kept "BrowserKey" for config back-compat.
        public string BrowserKey { get; set; } = "F3";
        // Toggle golden art; acts only when the search box isn't focused.
        public string GoldenKey { get; set; } = "G";
        // Re-focus the search box; acts only when it isn't already focused.
        public string FocusKey { get; set; } = "S";
        // Whether duos-only cards appear in results/browse.
        public bool ShowDuos { get; set; } = true;
        // Floating cards dragged onto the screen hide together with the overlay (and reappear when
        // it reopens). When false, they live independently and survive the overlay closing/unfocusing.
        public bool HideDraggedWithApp { get; set; } = true;
        // Allow pulling a floating card out of the detail-pane art (the big portrait). On by default.
        public bool DragFromDetail { get; set; } = true;
        // Allow pulling a floating card straight out of a results-grid cell. Off by default (so a
        // grid click stays a plain select for most users).
        public bool DragFromGrid { get; set; } = false;
        // Last width (in DIPs) a floating card was scaled to, so the next dragged card matches it.
        // 0 = use the card's native size. Persisted for consistency across sessions.
        public double FloatingCardWidth { get; set; } = 0;
        // Dev shortcut: use local public/ PNGs instead of the pack/CDN WebP. Off by default.
        public bool UseLocalDevArt { get; set; } = false;
        // Custom folder for the (large ~200MB) card-art cache. Empty = default (DataDir\art-cache).
        // Lets users move the art off the system drive. Other small files stay in DataDir.
        public string ArtCacheDir { get; set; } = "";

        // Always-on HUD: read the player's current trinkets / lobby anomaly from HDT's live state and
        // show each as a floating card. Off by default (opt-in). Each slot's placement+size persists.
        public bool ShowTrinkets { get; set; } = false;
        // Opt-in to the two OVERFLOW trinket boxes (3rd/4th) on top of the usual lesser+greater pair —
        // only relevant in rare anomaly cases; off by default since most players only ever hold two.
        public bool ShowExtraTrinkets { get; set; } = false;
        public bool ShowAnomaly { get; set; } = false;
        public HudPlacement LesserTrinketHud { get; set; } = new HudPlacement();
        public HudPlacement GreaterTrinketHud { get; set; } = new HudPlacement();
        // Overflow trinket boxes: an anomaly can grant more than the usual lesser+greater pair, so the
        // HUD supports up to four independently-placed trinket boxes. Added after the original two →
        // old configs deserialize the first two and default these (XML back-compat).
        public HudPlacement Trinket3Hud { get; set; } = new HudPlacement();
        public HudPlacement Trinket4Hud { get; set; } = new HudPlacement();
        public HudPlacement AnomalyHud { get; set; } = new HudPlacement();

        // Opt-in: show opponents' Battlegrounds MMR (ladder rating) during a match as labels on the
        // leaderboard portraits — read from the lobby roster (HearthMirror) and matched against the
        // hsbg.cards leaderboard (only ~8000+ players are listed; others show 8000↓). Off by default.
        public bool ShowOpponentMmr { get; set; } = false;

        // Opt-in: the Dark Gift list panel, shown while hovering the in-game Dark Discovery button
        // (hover detected via HearthMirror's big-card/tooltip state — no screen geometry). Gifts
        // offerable this turn glow; future ones dim; expired ones hide. Off by default.
        public bool ShowDarkGifts { get; set; } = false;
        // Panel display mode: "Both" (gift list + minion pool), "Gifts" (list only), "Minions"
        // (pool only — panel hidden entirely when no pool applies). Right-click the panel cycles;
        // also settable in the Dark Gifts settings sub-menu.
        public string DarkGiftMode { get; set; } = "Both";
        public HudPlacement DarkGiftHud { get; set; } = new HudPlacement();

        // Opt-in match recorder: capture the player's board + context at each combat boundary and write a
        // CSV per match (no screenshots). Off by default. Output: DataDir\match-exports\.
        public bool ExportMatchBoards { get; set; } = false;

        [XmlIgnore] public Key BrowserKeyParsed => Enum.TryParse(BrowserKey, out Key k) ? k : Key.F3;
        [XmlIgnore] public Key GoldenKeyParsed => Enum.TryParse(GoldenKey, out Key k) ? k : Key.G;
        [XmlIgnore] public Key FocusKeyParsed => Enum.TryParse(FocusKey, out Key k) ? k : Key.S;

        // Content hash (patch-independent) of the last loaded card snapshot — drives the data refresh.
        public string LastDataHash { get; set; } = "";
        // Last GitHub update check (UTC "o"); throttles the check.
        public string LastUpdateCheckUtc { get; set; } = "";
        // Highest notification id already seen (bell); larger ids are unread.
        public int PatchNoticeLastSeenId { get; set; } = 0;
        // Aggregate manifest hash of the art pack already applied; a change triggers re-sync.
        public string ArtPackHash { get; set; } = "";

        /// <summary>Writable data folder in %APPDATA% (config, caches) — survives DLL redeploys.</summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "HsbgCardLookup");

        private static string Dir => DataDir;

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

    /// <summary>Persisted screen placement + size for one always-on HUD card (a trinket or the anomaly).
    /// <see cref="Set"/> = the user has moved/sized it at least once; until then a computed default
    /// position is used. X/Y are the window's top-left in DIPs; W is the art width (excl. the grab ring).</summary>
    public class HudPlacement
    {
        public bool Set { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
    }
}
