using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HsbgCardLookup.Search;
using Newtonsoft.Json;

namespace HsbgCardLookup.Data
{
    /// <summary>Loads the card snapshot (API cache → bundled → dev path) and exposes searchable
    /// pools, derived filter lists, and id/externalId lookups.</summary>
    public sealed class CardStore
    {
        public List<BgCard> All { get; private set; } = new List<BgCard>();
        public List<BgCard> Current { get; private set; } = new List<BgCard>();
        public List<BgCard> Legacy { get; private set; } = new List<BgCard>();
        public string Patch { get; private set; } = "";
        public string LoadInfo { get; private set; } = "(not loaded)";

        // Real filter lists, derived from the current pool at load (not hardcoded placeholders).
        public List<string> Types { get; private set; } = new List<string>();
        public List<string> Tribes { get; private set; } = new List<string>();
        public List<int> Tiers { get; private set; } = new List<int>();
        public List<string> SpellSchools { get; private set; } = new List<string>();
        public Dictionary<int, BgCard> ById { get; private set; } = new Dictionary<int, BgCard>();
        // Join from an HDT entity's CardId string to our card (see BgCard.ExternalId).
        public Dictionary<string, BgCard> ByExternalId { get; private set; } = new Dictionary<string, BgCard>();

        // Display order, matching the website: minions, spells, heroes, hero powers, quests,
        // rewards, trinkets, then anomalies (out of pool) last. Anything else falls to the end.
        private static readonly string[] TypeOrder =
            { "minion", "spell", "hero", "hero_power", "quest", "reward", "trinket", "anomaly" };

        /// <summary>Card-type sort rank (lower = earlier); unlisted types sort last.</summary>
        public static int TypeRank(string cardType)
        {
            int i = Array.IndexOf(TypeOrder, cardType);
            return i < 0 ? int.MaxValue : i;
        }

        /// <summary>
        /// Default browse grouping (lower = earlier). Like <see cref="TypeRank"/> but pulls the
        /// "derived" cards of a type after the regular ones: regular minions, then token / hero-power
        /// minions; regular (tiered) spells, then spell tokens / spellcraft (tierless); then heroes,
        /// hero powers, quests, rewards, trinkets, anomalies. Within a group, sort by tier then name.
        /// </summary>
        public static int BrowseRank(BgCard c)
        {
            switch (c.CardType)
            {
                case "minion":
                    bool derived = (c.Categories ?? new List<string>())
                        .Any(x => x == "token" || x == "heroPower");
                    return derived ? 1 : 0;
                case "spell":
                    return c.Tier.HasValue ? 2 : 3;   // tierless spells (token/spellcraft/other) last
                case "hero": return 4;
                case "hero_power": return 5;
                case "quest": return 6;
                case "reward": return 7;
                case "trinket": return 8;
                case "anomaly": return 9;
                default: return 10;
            }
        }
        private static readonly string[] TribeOrder =
            { "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead", "All" };

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

                All = file.Cards;
                Patch = file.Patch ?? "";
                ById = All.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
                ByExternalId = All.Where(c => !string.IsNullOrEmpty(c.ExternalId))
                    .GroupBy(c => c.ExternalId)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Excluded everywhere: alternate versions + developer cards.
                var searchable = All.Where(c => !c.VersionOf.HasValue && c.CardType != "developer").ToList();
                Current = searchable.Where(c => c.Pool).ToList();
                Legacy = searchable.Where(c => !c.Pool).ToList();

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

        private static string FindCardsJson()
        {
            // 1. API-refreshed snapshot (newest patch we've fetched). Takes precedence so a new
            //    Blizzard patch shows up without re-installing the plugin.
            if (File.Exists(CachePath)) return CachePath;

            // 2. Bundled next to the DLL (production layout) — the offline floor.
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var bundled = Path.Combine(asmDir, "data", "cards.json");
            if (File.Exists(bundled)) return bundled;

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
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var bundled = Path.Combine(asmDir, "data", subfolder, file);
                if (File.Exists(bundled)) return bundled;
            }
            catch { /* fall through to dev path */ }
            var dev = Path.Combine(WebAppPublicDir, subfolder, file);
            return File.Exists(dev) ? dev : null;
        }
    }
}
