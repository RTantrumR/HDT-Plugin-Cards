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
        // Magnifying-glass button on the in-game overlay (left of the game's card-list book,
        // bottom-right) that toggles the search — a mouse path for players without the hotkey.
        public bool ShowSearchButton { get; set; } = true;
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

        // Opt-in master toggle: opponents' Battlegrounds MMR / tiers during a match — read from the
        // lobby roster (HearthMirror) and matched against the hsbg.cards leaderboard (only ~8000+
        // players are listed; others show 8000↓). Off by default. The feature is ULTRA-CONFIGURABLE:
        // two independent display surfaces (labels over the leaderboard portraits, and/or a separate
        // draggable list panel) crossed with per-part content toggles below — any combination goes
        // (e.g. tavern tiers only, names-only panel, rating without names, …).
        public bool ShowOpponentMmr { get; set; } = false;
        // Surface: labels stuck to the leaderboard portraits (the original display).
        public bool ShowMmrLabels { get; set; } = true;
        // Surface: a separate draggable/resizable standings panel on the overlay (like a HUD card).
        public bool ShowMmrPanel { get; set; } = false;
        // Content: player names. Off by default — streamers often don't want opponent names on screen.
        public bool ShowOpponentNames { get; set; } = false;
        // Content: the MMR rating itself (turn off for e.g. a names-only or tiers-only setup).
        public bool ShowMmrRating { get; set; } = true;
        // Content: today's ▲/▼ rating change next to the rating.
        public bool ShowMmrDeltas { get; set; } = true;
        // Where tavern-tier icons show: "Off", "Portraits" (right of each leaderboard portrait),
        // "Panel" (inside the separate list only), "Both" (default). Its own location axis so e.g.
        // a panel-only setup doesn't force icons onto the portraits.
        public string TavernTierMode { get; set; } = "Both";
        // Content: the ⚔ marker on the previously-fought opponent.
        public bool ShowLastOpponent { get; set; } = true;
        // Content: gray out players who are already dead.
        public bool DimDeadPlayers { get; set; } = true;
        // The standings panel's placement (canvas fractions, like the trinket boxes).
        public HudPlacement MmrPanelHud { get; set; } = new HudPlacement();

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
        // Version the user explicitly chose "Skip" for (background checks won't re-offer it or older;
        // the manual "Check for updates" button always reports regardless).
        public string SkippedUpdateVersion { get; set; } = "";
        // Plugin version of the previous run — a bump means the user just updated, driving the
        // one-time "Updated to vN" notice (replaced the retired staging-marker mechanism).
        public string LastRunVersion { get; set; } = "";

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

    /// <summary>Persisted placement + size for one always-on HUD card (a trinket or the anomaly).
    /// <see cref="Set"/> = the user has moved/sized it at least once; until then a computed default
    /// position is used. Since the HUD moved onto HDT's overlay canvas (0.3.2), the authoritative
    /// placement is the CANVAS FRACTIONS <see cref="XF"/>/<see cref="YF"/> (top-left) +
    /// <see cref="WF"/> (art width), all relative to the canvas size — WF &gt; 0 marks them valid.
    /// X/Y/W are the legacy screen-DIP window placement, kept so an old config converts once
    /// (BgHud → HudCanvasCard.LegacyToFrac) on the first show with a live canvas.</summary>
    public class HudPlacement
    {
        public bool Set { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double XF { get; set; }
        public double YF { get; set; }
        public double WF { get; set; }
    }
}
