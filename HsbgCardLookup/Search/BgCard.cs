using System.Collections.Generic;
using Newtonsoft.Json;

namespace HsbgCardLookup.Search
{
    /// <summary>
    /// Faithful C# mirror of the web app's BgCard (extensions/shared/src/data/types.ts),
    /// plus imageGold which the production JSON carries. Newtonsoft matches JSON property
    /// names case-insensitively, so camelCase JSON binds to these PascalCase properties.
    /// </summary>
    public class BgCard
    {
        public int Id { get; set; }
        public string Slug { get; set; }
        // Blizzard card id string (e.g. "TB_BaconShop_HERO_16"). Matches HDT's entity.CardId —
        // the join key between a live in-game entity and our card record. (~2357/2569 cards carry it.)
        public string ExternalId { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public string Image { get; set; }
        public string ImageGold { get; set; }
        public int? Attack { get; set; }
        public int? Health { get; set; }
        public int? AttackGold { get; set; }
        public int? HealthGold { get; set; }
        public string TextGold { get; set; }
        public int? ManaCost { get; set; }
        public int? Armor { get; set; }
        public int? Tier { get; set; }
        public string CardType { get; set; }
        public List<string> MinionTypes { get; set; } = new List<string>();
        public List<string> Keywords { get; set; } = new List<string>();
        public string SpellSchool { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public bool IsHero { get; set; }
        public bool IsDuosOnly { get; set; }
        public bool IsSolosOnly { get; set; }
        public int? CompanionId { get; set; }
        public List<int> ChildIds { get; set; } = new List<int>();
        public int? ParentId { get; set; }
        public bool Pool { get; set; }
        public int? VersionOf { get; set; }
        public string TrinketTier { get; set; }

        [JsonIgnore]
        public string PrimaryTribe =>
            (MinionTypes != null && MinionTypes.Count > 0) ? MinionTypes[0] : null;

        /// <summary>True when a golden version differs (stats or text) — mirrors the website.</summary>
        [JsonIgnore]
        public bool HasGoldenDiff =>
            (!string.IsNullOrEmpty(TextGold) && TextGold != Text) ||
            (AttackGold.HasValue && AttackGold != Attack) ||
            (HealthGold.HasValue && HealthGold != Health) ||
            !string.IsNullOrEmpty(ImageGold);
    }

    /// <summary>Shape of data/production/cards.json: { patch, cardCount, cards: [...] }.</summary>
    public class CardsFile
    {
        public string Patch { get; set; }
        public int CardCount { get; set; }
        public List<BgCard> Cards { get; set; } = new List<BgCard>();
    }
}
