using System;
using System.Collections.Generic;

namespace HsbgCardLookup.Search
{
    /// <summary>Port of lib/search/aliases.ts (the website's table — includes the
    /// "category" type, Neutral tribe, and subcategories the bot table lacks).</summary>
    public sealed class AliasEntry
    {
        public string Canonical;
        public string Type;          // keyword | tribe | text_keyword | card_type | category
        public string[] Aliases;
        public string TextPattern;   // optional

        public AliasEntry(string canonical, string type, string[] aliases, string textPattern = null)
        {
            Canonical = canonical;
            Type = type;
            Aliases = aliases;
            TextPattern = textPattern;
        }
    }

    internal static class Aliases
    {
        public static readonly AliasEntry[] Table =
        {
            // Keywords
            new AliasEntry("Taunt", "keyword", new[] { "taunt", "провокація", "таунт" }),
            new AliasEntry("Divine Shield", "keyword", new[] { "divine shield", "божественний щит", "дівайн шілд", "бублик", "бабл" }),
            new AliasEntry("Battlecry", "keyword", new[] { "battlecry", "бойовий клич", "батлкрай" }),
            new AliasEntry("Deathrattle", "keyword", new[] { "deathrattle", "передсмертний хрип", "дезратл", "хрип" }),
            new AliasEntry("Poisonous", "keyword", new[] { "poisonous", "отруйний", "пойзон", "отрута" }),
            new AliasEntry("Venomous", "keyword", new[] { "venomous", "веномус" }),
            new AliasEntry("Reborn", "keyword", new[] { "reborn", "відродження", "реборн" }),
            new AliasEntry("Windfury", "keyword", new[] { "windfury", "шаленість вітру", "віндфурі" }),
            new AliasEntry("Discover", "keyword", new[] { "discover", "відкриття", "діскавер" }),
            new AliasEntry("Stealth", "keyword", new[] { "stealth", "непомітність", "стелс" }),
            new AliasEntry("Aura", "keyword", new[] { "aura", "аура" }),
            new AliasEntry("Avenge", "keyword", new[] { "avenge", "відплата", "авенж", "помста" }),
            // Text-based keywords (matched against card text, not keywords array)
            new AliasEntry("End of Turn", "text_keyword", new[] { "end of turn", "eot", "кінець ходу" }, "end of"),
            // Tribes
            new AliasEntry("Beast", "tribe", new[] { "beast", "звір", "біст" }),
            new AliasEntry("Demon", "tribe", new[] { "demon", "демон" }),
            new AliasEntry("Dragon", "tribe", new[] { "dragon", "дракон" }),
            new AliasEntry("Elemental", "tribe", new[] { "elemental", "елементаль" }),
            new AliasEntry("Mech", "tribe", new[] { "mech", "механізм", "мех" }),
            new AliasEntry("Murloc", "tribe", new[] { "murloc", "мурлок" }),
            new AliasEntry("Naga", "tribe", new[] { "naga", "нага" }),
            new AliasEntry("Pirate", "tribe", new[] { "pirate", "пірат" }),
            new AliasEntry("Quilboar", "tribe", new[] { "quilboar", "іглошкур", "квілбор" }),
            new AliasEntry("Undead", "tribe", new[] { "undead", "нежить", "андед" }),
            new AliasEntry("All", "tribe", new[] { "all", "все" }),
            new AliasEntry("Neutral", "tribe", new[] { "neutral" }),
            // Card subcategories (minion categories + spell schools)
            new AliasEntry("buddy", "category", new[] { "buddy", "buddies" }),
            new AliasEntry("token", "category", new[] { "token", "tokens" }),
            new AliasEntry("timewarped", "category", new[] { "timewarped", "time warped" }),
            new AliasEntry("spellcraft", "category", new[] { "spellcraft" }),
            new AliasEntry("battlecruiser", "category", new[] { "battlecruiser" }),
            new AliasEntry("darkmoon", "category", new[] { "darkmoon", "dark moon" }),
            // Season 14 Dark Gifts (43 cards, all spells, categories:["darkgift"]). Deliberately NOT
            // aliased on a bare "gift": that word is a hard category filter here, and it would then
            // hide the pool cards actually named Gacha Gift / Sacred Gift / Gift of the Golden Kobold.
            // Kept AFTER darkmoon so a bare "dark" still prefix-resolves to darkmoon as before (the
            // prefix pass takes the first shortest-remaining alias, and "darkgift"/"darkmoon" tie).
            new AliasEntry("darkgift", "category", new[]
                { "dark gift", "dark gifts", "darkgift", "darkgifts", "темний дар", "темні дари" }),
            // Trinket tiers (only filter when a trinket card type is also present)
            new AliasEntry("lesser", "trinket_tier", new[] { "lesser", "менший" }),
            new AliasEntry("greater", "trinket_tier", new[] { "greater", "більший" }),
            // Card types
            new AliasEntry("minion", "card_type", new[] { "minion", "minions", "мініон" }),
            new AliasEntry("hero", "card_type", new[] { "hero", "heroes", "герой" }),
            new AliasEntry("spell", "card_type", new[] { "spell", "spells", "закляття", "спел" }),
            new AliasEntry("quest", "card_type", new[] { "quest", "quests", "квест" }),
            new AliasEntry("anomaly", "card_type", new[] { "anomaly", "anomalies", "аномалія" }),
            new AliasEntry("trinket", "card_type", new[] { "trinket", "trinkets", "тринкет" }),
            new AliasEntry("hero_power", "card_type", new[] { "hero power", "heropower", "hero powers", "сила героя" }),
            new AliasEntry("reward", "card_type", new[] { "reward", "rewards", "нагорода" }),
        };

        public static AliasEntry FuzzyMatch(string input)
        {
            string q = input.ToLowerInvariant();

            // 1. Exact match
            foreach (var entry in Table)
                foreach (var alias in entry.Aliases)
                    if (alias == q) return entry;

            if (q.Length < 3) return null;

            // 2. Input is a prefix of an alias (e.g. "taun" -> "taunt")
            {
                AliasEntry bestEntry = null;
                int bestRemaining = int.MaxValue;
                foreach (var entry in Table)
                    foreach (var alias in entry.Aliases)
                        if (alias.StartsWith(q, StringComparison.Ordinal))
                        {
                            int remaining = alias.Length - q.Length;
                            if (remaining < bestRemaining) { bestRemaining = remaining; bestEntry = entry; }
                        }
                if (bestEntry != null) return bestEntry;
            }

            // 3. Approximate prefix match (e.g. "dethr" ~ "deathr" prefix of "deathrattle")
            if (q.Length >= 4)
            {
                int threshold = Math.Max(1, q.Length / 4);
                AliasEntry bestEntry = null;
                int bestDist = int.MaxValue;
                foreach (var entry in Table)
                    foreach (var alias in entry.Aliases)
                    {
                        if (alias.Length < q.Length) continue;
                        int kMin = Math.Max(3, q.Length - 2);
                        int kMax = Math.Min(q.Length + 2, alias.Length);
                        for (int k = kMin; k <= kMax; k++)
                        {
                            string prefix = alias.Substring(0, k);
                            int dist = Levenshtein.Distance(q, prefix);
                            if (dist <= threshold && dist < bestDist) { bestDist = dist; bestEntry = entry; }
                        }
                    }
                if (bestEntry != null) return bestEntry;
            }

            // 4. Full fuzzy match (input ~ alias, similar length)
            if (q.Length >= 4)
            {
                AliasEntry bestEntry = null;
                int bestDist = int.MaxValue;
                foreach (var entry in Table)
                    foreach (var alias in entry.Aliases)
                    {
                        int shorter = Math.Min(q.Length, alias.Length);
                        int threshold = Math.Max(1, shorter / 3);
                        if (Math.Abs(alias.Length - q.Length) > threshold + 1) continue;
                        int dist = Levenshtein.Distance(q, alias);
                        if (dist <= threshold && dist < bestDist) { bestDist = dist; bestEntry = entry; }
                    }
                return bestEntry;
            }

            return null;
        }
    }
}
