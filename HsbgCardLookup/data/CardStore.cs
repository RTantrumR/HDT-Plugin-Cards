using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HsbgCardLookup.Localization;
using HsbgCardLookup.Search;
using Newtonsoft.Json;

namespace HsbgCardLookup.Data
{
    /// <summary>Loads the card snapshot (API cache → bundled → dev path) and exposes searchable
    /// pools, derived filter lists, and id/externalId lookups.
    ///
    /// The English snapshot is the structural truth; a card language is an overlay of names, text
    /// and render paths laid over it card by card (BgCard.Local*), from the site's
    /// /api/cards?locale= payload that <see cref="LocaleSync"/> caches. <see cref="Load"/> re-applies
    /// the current language, so a data refresh keeps it. Mutations happen on the UI thread; HDT's
    /// poll threads read card fields as plain references.</summary>
    public sealed class CardStore
    {
        public List<BgCard> All { get; private set; } = new List<BgCard>();
        public List<BgCard> Current { get; private set; } = new List<BgCard>();
        public List<BgCard> Legacy { get; private set; } = new List<BgCard>();
        public string Patch { get; private set; } = "";
        public string LoadInfo { get; private set; } = "(not loaded)";

        // ── Card language ──
        private string _localeWeb = Locales.English;
        /// <summary>The language in use: "en" or a card-language web code.</summary>
        public string LocaleWeb => _localeWeb;
        /// <summary>True while the language's snapshot is laid over the cards (false for English, and
        /// for a language whose file is not cached yet — the cards read English until it arrives).</summary>
        public bool LocaleApplied { get; private set; }
        /// <summary>Cards that received at least one localized field.</summary>
        public int LocaleCount { get; private set; }
        public string LocalePatch { get; private set; } = "";

        // Real filter lists, derived from the current pool at load (not hardcoded placeholders).
        public List<string> Types { get; private set; } = new List<string>();
        public List<string> Tribes { get; private set; } = new List<string>();
        public List<int> Tiers { get; private set; } = new List<int>();
        public List<string> SpellSchools { get; private set; } = new List<string>();
        public Dictionary<int, BgCard> ById { get; private set; } = new Dictionary<int, BgCard>();
        // Join from an HDT entity's CardId string to our card (see BgCard.ExternalId).
        public Dictionary<string, BgCard> ByExternalId { get; private set; } = new Dictionary<string, BgCard>();

        // ── Browse order: a port of the website's grid sections (lib/grid-sections.ts) ──
        // Tavern minions and tavern spells lead, tier by tier; then Dark Gifts by the turn they
        // unlock; heroes, hero powers, trinkets, anomalies, quests, rewards; and only then every
        // other minion and spell (buddies, tokens, hero-power minions, Timewarped, Darkmoon prizes,
        // Spellcraft, Battlecruiser…), again tier by tier, the tierless ones closing each group.
        // A card with no categories at all is a wiki-backfilled legacy tavern card: absence is tavern.
        private static bool IsTavern(BgCard c) =>
            c.Categories == null || c.Categories.Count == 0 || c.Categories.Contains("tavern");

        /// <summary>
        /// Default browse rank (lower = earlier): the website's section order with each section's own
        /// sub-order (the tier, or a Dark Gift's unlock turn) folded in. Sort by this, then by the
        /// English name, and the grid reads top to bottom like the site's.
        /// </summary>
        public static int BrowseRank(BgCard c)
        {
            const int Group = 100, Tierless = 99;
            int tier = c.Tier > 0 ? c.Tier.Value : Tierless;
            switch (c.CardType)
            {
                case "minion":
                    return (IsTavern(c) && tier != Tierless ? 0 : 9) * Group + tier;
                case "spell":
                    if (c.Categories != null && c.Categories.Contains("darkgift"))
                        return 2 * Group + (c.DarkGiftMinTurn > 0 ? c.DarkGiftMinTurn.Value : Tierless);
                    return (IsTavern(c) && tier != Tierless ? 1 : 10) * Group + tier;
                case "hero": return 3 * Group;
                case "hero_power": return 4 * Group;
                case "trinket": return 5 * Group;
                case "anomaly": return 6 * Group;
                case "quest": return 7 * Group;
                case "reward": return 8 * Group;
                default: return 11 * Group;
            }
        }
        // The card-type filter dropdown's order (the browse order is BrowseRank's, below).
        private static readonly string[] TypeOrder =
            { "minion", "spell", "hero", "hero_power", "quest", "reward", "trinket", "anomaly" };
        private static readonly string[] TribeOrder =
            { "Aberration", "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead", "All" };

        // Dev-only: local web-app public/ art folder (used only when PluginConfig.UseLocalDevArt is on).
        private static readonly string WebAppPublicDir =
            @"E:\RiderProjects\Testing\ConsoleApp1\public";

        public bool Load()
        {
            try
            {
                string path = FindCardsJson();
                if (path == null) { LoadInfo = "cards.json not found"; return false; }

                string json = File.ReadAllText(path);
                var file = JsonConvert.DeserializeObject<CardsFile>(json);
                if (file?.Cards == null) { LoadInfo = "parse returned no cards"; return false; }

                var all = file.Cards;
                var byId = all.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
                var byExternalId = all.Where(c => !string.IsNullOrEmpty(c.ExternalId))
                    .GroupBy(c => c.ExternalId)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Excluded everywhere: alternate versions + developer cards.
                var searchable = all.Where(c => !c.VersionOf.HasValue && c.CardType != "developer").ToList();
                var current = searchable.Where(c => c.Pool).ToList();
                var legacy = searchable.Where(c => !c.Pool).ToList();

                // Lay the card language over the fresh objects BEFORE publishing them: no reader ever
                // sees a half-switched store, and no folded search form is cached off English.
                var overlay = ApplyLocale(all, byId);

                All = all;
                Patch = file.Patch ?? "";
                ById = byId;
                ByExternalId = byExternalId;
                Current = current;
                Legacy = legacy;
                LocaleApplied = overlay.applied; LocaleCount = overlay.count; LocalePatch = overlay.patch;

                BuildFilterLists();

                LoadInfo = $"{All.Count} cards (patch {Patch}); current={Current.Count}, legacy={Legacy.Count}; "
                    + $"types={Types.Count}, tribes={Tribes.Count}, tiers=[{string.Join(",", Tiers)}]; from {path}";
                return true;
            }
            catch (Exception ex)
            {
                LoadInfo = "load error: " + ex.Message;
                return false;
            }
        }

        /// <summary>Find our card record for an in-game entity's CardId string, or null.</summary>
        public BgCard Lookup(string externalId) =>
            !string.IsNullOrEmpty(externalId) && ByExternalId.TryGetValue(externalId, out var c) ? c : null;

        /// <summary>Switch the card language. English clears the overlay; another language applies
        /// its cached snapshot when there is one. Returns false when that file is not cached yet — the
        /// cards read English until <see cref="CardLanguage.EnsureDataAsync"/> fetches it. UI thread.</summary>
        public bool SetLocale(string web)
        {
            _localeWeb = string.IsNullOrEmpty(web) ? Locales.English : web;
            var overlay = ApplyLocale(All, ById);
            LocaleApplied = overlay.applied; LocaleCount = overlay.count; LocalePatch = overlay.patch;
            return _localeWeb == Locales.English || overlay.applied;
        }

        // Lay the current language's cached snapshot over a set of cards, by id, in one pass per card.
        private (bool applied, int count, string patch) ApplyLocale(List<BgCard> all, Dictionary<int, BgCard> byId)
        {
            if (_localeWeb == Locales.English)
            {
                foreach (var c in all) c.ClearLocale();
                return (false, 0, "");
            }

            Dictionary<int, BgCard> localized = null; string patch = "";
            try
            {
                string path = LocaleSync.SnapshotPath(_localeWeb);
                if (File.Exists(path))
                {
                    var file = JsonConvert.DeserializeObject<CardsFile>(File.ReadAllText(path));
                    if (file?.Cards != null && file.Cards.Count > 0)
                    {
                        localized = file.Cards.Where(c => c != null).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
                        patch = file.Patch ?? "";
                    }
                }
            }
            catch { localized = null; }

            if (localized == null)
            {
                foreach (var c in all) c.ClearLocale();
                return (false, 0, "");
            }

            int n = 0;
            foreach (var c in all)
            {
                BgCard loc;
                if (localized.TryGetValue(c.Id, out loc) && Overlay(c, loc)) n++;
                else c.ClearLocale();
            }
            return (true, n, patch);
        }

        // Only what differs from English is laid over: the site keeps English for cards Blizzard did
        // not localize, and an equal value would only cost the search its "English form" shortcut.
        // One assignment per field, then the folded forms are dropped — a poll thread reading
        // meanwhile sees a whole old or a whole new value.
        private static bool Overlay(BgCard c, BgCard loc)
        {
            string name = Differs(loc.Name, c.Name) ? loc.Name : null;
            string text = Differs(loc.Text, c.Text) ? loc.Text : null;
            string textGold = Differs(loc.TextGold, c.TextGold) ? loc.TextGold : null;
            string image = Locales.IsLocaleRender(loc.Image) ? loc.Image : null;
            string imageGold = Locales.IsLocaleRender(loc.ImageGold) ? loc.ImageGold : null;
            c.LocalName = name; c.LocalText = text; c.LocalTextGold = textGold;
            c.LocalImage = image; c.LocalImageGold = imageGold;
            c.InvalidateSearchIndex();
            return name != null || text != null || textGold != null || image != null || imageGold != null;
        }

        private static bool Differs(string loc, string en) =>
            !string.IsNullOrEmpty(loc) && !string.Equals(loc, en, StringComparison.Ordinal);

        private void BuildFilterLists()
        {
            int OrderIndex(string[] order, string val)
            {
                int i = Array.IndexOf(order, val);
                return i < 0 ? int.MaxValue : i;
            }

            Types = Current.Select(c => c.CardType)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .OrderBy(t => OrderIndex(TypeOrder, t))
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Tribes = Current.SelectMany(c => c.MinionTypes ?? new List<string>())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .OrderBy(t => OrderIndex(TribeOrder, t))
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // "Neutral" is a virtual filter (minions with no tribe), supported by the engine.
            if (Current.Any(c => c.CardType == "minion" && (c.MinionTypes == null || c.MinionTypes.Count == 0)))
                Tribes.Add("Neutral");

            Tiers = Current.Where(c => c.Tier.HasValue).Select(c => c.Tier.Value)
                .Distinct().OrderBy(t => t).ToList();

            SpellSchools = Current.Where(c => c.CardType == "spell" && !string.IsNullOrEmpty(c.SpellSchool))
                .Select(c => c.SpellSchool).Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Writable cache for a fresher snapshot pulled from the API. Lives in %APPDATA%
        /// so it survives DLL redeploys (which only replace the Plugins-folder bin output).</summary>
        public static string CachePath =>
            Path.Combine(Config.PluginConfig.DataDir, "cache", "cards.json");

        /// <summary>A file shipped next to the DLL under data\ (cards.json, locale-terms.json,
        /// ui\en.json, the tier and tribe icons), or null when it is not there.</summary>
        public static string BundledDataPath(string relative)
        {
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var path = Path.Combine(asmDir, "data", relative);
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        private static string FindCardsJson()
        {
            // 1. API-refreshed snapshot (newest patch we've fetched). Takes precedence so a new
            //    Blizzard patch shows up without re-installing the plugin.
            if (File.Exists(CachePath)) return CachePath;

            // 2. Bundled next to the DLL (production layout) — the offline floor.
            var bundled = BundledDataPath("cards.json");
            if (bundled != null) return bundled;

            // 3. Dev fallback: the web-app source of truth.
            var dev = @"E:\RiderProjects\Testing\ConsoleApp1\data\production\cards.json";
            if (File.Exists(dev)) return dev;

            return null;
        }

        /// <summary>Absolute path to a card's PNG art, or null if not available locally.</summary>
        public static string ResolveImagePath(BgCard card, bool golden = false)
        {
            try
            {
                string rel = golden ? card.ImageGold : card.Image;
                if (string.IsNullOrEmpty(rel)) return null;
                string full = Path.Combine(WebAppPublicDir, rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>Buddy / token / hero-power / source cards related to this one (port of the
        /// twitch extension's getRelatedCards). Order preserved; duplicates removed.</summary>
        public List<BgCard> RelatedCards(BgCard card)
        {
            var outp = new List<BgCard>();
            var seen = new HashSet<int>();
            BgCard c;

            if (card.CompanionId.HasValue && ById.TryGetValue(card.CompanionId.Value, out c))
            {
                outp.Add(c);
                seen.Add(c.Id);
            }
            foreach (var childId in card.ChildIds ?? new List<int>())
            {
                if (seen.Contains(childId)) continue;
                if (!ById.TryGetValue(childId, out c)) continue;
                outp.Add(c);
                seen.Add(childId);
            }
            if (card.ParentId.HasValue && !seen.Contains(card.ParentId.Value) && ById.TryGetValue(card.ParentId.Value, out c))
                outp.Add(c);

            return outp;
        }

        /// <summary>Tier icon: bundled <c>data\tiers\Tier{n}.png</c> next to the DLL (shipped), or
        /// the dev <c>public/</c> folder, or null.</summary>
        public static string TierIconPath(int tier) => FindBundledAsset("tiers", "Tier" + tier + ".png");

        /// <summary>Tribe icon: bundled <c>data\tribes\{Tribe}.jpg</c> next to the DLL (shipped), or
        /// the dev <c>public/</c> folder, or null.</summary>
        public static string TribeIconPath(string tribe) =>
            string.IsNullOrEmpty(tribe) ? null : FindBundledAsset("tribes", tribe + ".jpg");

        private static string FindBundledAsset(string subfolder, string file)
        {
            var bundled = BundledDataPath(Path.Combine(subfolder, file));
            if (bundled != null) return bundled;
            var dev = Path.Combine(WebAppPublicDir, subfolder, file);
            return File.Exists(dev) ? dev : null;
        }
    }
}
