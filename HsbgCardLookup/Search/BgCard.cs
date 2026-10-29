using System.Collections.Generic;
using Newtonsoft.Json;

namespace HsbgCardLookup.Search
{
    /// <summary>
    /// Faithful C# mirror of the web app's BgCard (extensions/shared/src/data/types.ts),
    /// plus imageGold which the production JSON carries. Newtonsoft matches JSON property
    /// names case-insensitively, so camelCase JSON binds to these PascalCase properties.
    ///
    /// Name / Text / Image are always the ENGLISH record: the gameplay logic (Dark Gift
    /// requirements, the static gift table, HDT joins, text heuristics) reads those. When a
    /// card language is in use, <see cref="Data.CardStore"/> fills the Local* fields from the
    /// site's /api/cards?locale= payload and the UI and the name/text search read the Display*
    /// forms, which fall back to English card by card (the site localizes only what Blizzard did).
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
        // Season-14 "Timewarped" variants exist only in their anomaly lobby — excluded from normal pools.
        public bool IsTimewarped { get; set; }
        public int? CompanionId { get; set; }
        public List<int> ChildIds { get; set; } = new List<int>();
        public int? ParentId { get; set; }
        public bool Pool { get; set; }
        public int? VersionOf { get; set; }
        public string TrinketTier { get; set; }
        // Dark Gifts (Season 14): the offering turn window, carried by the site data for cards with
        // categories:["darkgift"]. Null max = no upper bound; both null = data predates the fields.
        public int? DarkGiftMinTurn { get; set; }
        public int? DarkGiftMaxTurn { get; set; }

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

        // ── Card language (set by CardStore.SetLocale; null = no own value, or English in use) ──
        // Every member below is [JsonIgnore]: Plugin hashes the serialized snapshot to detect data
        // changes, and the overlay must not churn that hash.
        [JsonIgnore] public string LocalName { get; set; }
        [JsonIgnore] public string LocalText { get; set; }
        [JsonIgnore] public string LocalTextGold { get; set; }
        // The locale render paths (/cards/production/locales/<web>/…) when the site has them.
        [JsonIgnore] public string LocalImage { get; set; }
        [JsonIgnore] public string LocalImageGold { get; set; }

        [JsonIgnore] public string DisplayName => LocalName ?? Name;
        [JsonIgnore] public string DisplayText => LocalText ?? Text;
        [JsonIgnore] public string DisplayTextGold => LocalTextGold ?? TextGold;

        // Folded (lower-case, Latin diacritics stripped) display name and text for matching, and the
        // English forms beside them while a locale is applied — so a German player still finds
        // "Murloc Tidecaller" by its English name, and English keyword logic keeps reading English.
        private string _searchName, _searchText, _searchNameEn, _searchTextEn;
        [JsonIgnore] public string SearchName => _searchName ?? (_searchName = SearchEngine.Fold(DisplayName));
        [JsonIgnore] public string SearchText => _searchText ?? (_searchText = SearchEngine.Fold(DisplayText));
        /// <summary>Folded English name — null while no locale is applied (SearchName is English then).</summary>
        [JsonIgnore] public string SearchNameEn => LocalName == null ? null : (_searchNameEn ?? (_searchNameEn = SearchEngine.Fold(Name)));
        [JsonIgnore] public string SearchTextEn => LocalText == null ? null : (_searchTextEn ?? (_searchTextEn = SearchEngine.Fold(Text)));

        /// <summary>Drop the locale overlay (back to English) and the folded forms built on it.</summary>
        public void ClearLocale()
        {
            LocalName = LocalText = LocalTextGold = LocalImage = LocalImageGold = null;
            InvalidateSearchIndex();
        }

        public void InvalidateSearchIndex() { _searchName = _searchText = _searchNameEn = _searchTextEn = null; }
    }

    /// <summary>Shape of data/production/cards.json: { patch, cardCount, cards: [...] }.</summary>
    public class CardsFile
    {
        public string Patch { get; set; }
        public int CardCount { get; set; }
        public List<BgCard> Cards { get; set; } = new List<BgCard>();
    }
}
