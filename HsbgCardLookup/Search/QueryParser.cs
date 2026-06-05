using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HsbgCardLookup.Search
{
    /// <summary>Port of lib/search/query-parser.ts.</summary>
    public sealed class ParsedQuery
    {
        public int? StatsAttack;
        public int? StatsHealth;
        public bool HasStats => StatsAttack.HasValue && StatsHealth.HasValue;
        public int? Tier;
        public int? ManaCost;
        public List<string> Keywords = new List<string>();
        public List<AliasEntry> TextKeywords = new List<AliasEntry>();
        public List<string> Tribes = new List<string>();
        public List<string> CardTypes = new List<string>();
        public List<string> Categories = new List<string>();
        public List<string> TrinketTiers = new List<string>();
        public string NameQuery = "";
        public bool IsStructured;
    }

    internal static class QueryParser
    {
        private static readonly Regex SlashStats = new Regex(@"^(\d{1,2})/(\d{1,2})$", RegexOptions.Compiled);
        private static readonly Regex TierMarker = new Regex(@"^[tт]([1-7])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NumberRe = new Regex(@"^\d{1,2}$", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        // Mana cost filter. The data field is manaCost; we trigger only on "cost" (+ UA aliases).
        // "gold"/"mana" are intentionally NOT triggers: "gold" collides with the many cards whose
        // text references gold ("gain N gold", golden cards), and "mana" is dev-only naming the BG
        // UI never shows. Combined forms ("cost2", "2cost") are caught in pass 1; separated forms
        // ("cost 2", "2 cost") in a dedicated pass.
        private const string CostStems = @"cost|кост|вартість";
        private static readonly Regex CostCombined = new Regex(
            @"^(?:" + CostStems + @")(\d{1,2})$|^(\d{1,2})(?:" + CostStems + @")$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> CostWords = new HashSet<string>(
            new[] { "cost", "кост", "вартість" },
            StringComparer.OrdinalIgnoreCase);

        public static ParsedQuery Parse(string raw)
        {
            var tokens = new List<string>(Whitespace.Split(raw.Trim()));

            int? statsA = null, statsH = null;
            int? tier = null;
            int? manaCost = null;
            var keywords = new List<string>();
            var textKeywords = new List<AliasEntry>();
            var tribes = new List<string>();
            var cardTypes = new List<string>();
            var categories = new List<string>();
            var trinketTiers = new List<string>();
            var remaining = new List<string>();

            // Pass 1: slash-stats (5/6), tier markers (t3 / т3), combined cost tokens (cost2 / 2cost)
            foreach (var token in tokens)
            {
                var slash = SlashStats.Match(token);
                if (slash.Success && !statsA.HasValue)
                {
                    statsA = int.Parse(slash.Groups[1].Value);
                    statsH = int.Parse(slash.Groups[2].Value);
                    continue;
                }
                var tm = TierMarker.Match(token);
                if (tm.Success && !tier.HasValue)
                {
                    tier = int.Parse(tm.Groups[1].Value);
                    continue;
                }
                var cc = CostCombined.Match(token);
                if (cc.Success && !manaCost.HasValue)
                {
                    manaCost = int.Parse(cc.Groups[1].Success ? cc.Groups[1].Value : cc.Groups[2].Value);
                    continue;
                }
                remaining.Add(token);
            }

            // Pass 1.5: separated cost ("cost 2" / "2 cost"). Runs before the two-number stats pass
            // so the cost number isn't mistaken for a stat. Consumes the cost word + its adjacent
            // number; a lone "cost" with no neighboring number falls through to the name query.
            if (!manaCost.HasValue)
            {
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (!CostWords.Contains(remaining[i])) continue;
                    if (i + 1 < remaining.Count && NumberRe.IsMatch(remaining[i + 1]))
                    {
                        manaCost = int.Parse(remaining[i + 1]);
                        remaining.RemoveRange(i, 2);
                        break;
                    }
                    if (i - 1 >= 0 && NumberRe.IsMatch(remaining[i - 1]))
                    {
                        manaCost = int.Parse(remaining[i - 1]);
                        remaining.RemoveRange(i - 1, 2);
                        break;
                    }
                }
            }

            // Pass 2: if no slash-stats, two consecutive numbers
            if (!statsA.HasValue)
            {
                for (int i = 0; i < remaining.Count - 1; i++)
                {
                    if (NumberRe.IsMatch(remaining[i]) && NumberRe.IsMatch(remaining[i + 1]))
                    {
                        statsA = int.Parse(remaining[i]);
                        statsH = int.Parse(remaining[i + 1]);
                        remaining.RemoveRange(i, 2);
                        break;
                    }
                }
            }

            // Pass 3: fuzzy-match remaining against alias table (triples, pairs, singles).
            // Card-type matches are collected with their token position (not routed immediately):
            // a card can only be one type, so we keep the earliest and demote the rest to the
            // name/text query (e.g. "trinket hero power" => type=trinket + text "hero power",
            // rather than an impossible type==trinket AND type==hero_power).
            var unmatched = new List<string>();
            var consumed = new HashSet<int>();
            // Card types and trinket tiers are both single-valued first-wins: keep the earliest by
            // position and demote the rest to the name/text query.
            var cardTypeHits = new List<(int Index, string Canonical, string[] Tokens)>();
            var trinketTierHits = new List<(int Index, string Canonical, string[] Tokens)>();

            for (int i = 0; i < remaining.Count - 2; i++)
            {
                if (consumed.Contains(i) || consumed.Contains(i + 1) || consumed.Contains(i + 2)) continue;
                var match = Aliases.FuzzyMatch(remaining[i] + " " + remaining[i + 1] + " " + remaining[i + 2]);
                if (match != null)
                {
                    var toks = new[] { remaining[i], remaining[i + 1], remaining[i + 2] };
                    if (match.Type == "card_type") cardTypeHits.Add((i, match.Canonical, toks));
                    else if (match.Type == "trinket_tier") trinketTierHits.Add((i, match.Canonical, toks));
                    else Route(match, keywords, textKeywords, tribes, cardTypes, categories);
                    consumed.Add(i); consumed.Add(i + 1); consumed.Add(i + 2);
                }
            }

            for (int i = 0; i < remaining.Count - 1; i++)
            {
                if (consumed.Contains(i) || consumed.Contains(i + 1)) continue;
                var match = Aliases.FuzzyMatch(remaining[i] + " " + remaining[i + 1]);
                if (match != null)
                {
                    var toks = new[] { remaining[i], remaining[i + 1] };
                    if (match.Type == "card_type") cardTypeHits.Add((i, match.Canonical, toks));
                    else if (match.Type == "trinket_tier") trinketTierHits.Add((i, match.Canonical, toks));
                    else Route(match, keywords, textKeywords, tribes, cardTypes, categories);
                    consumed.Add(i); consumed.Add(i + 1);
                }
            }

            for (int i = 0; i < remaining.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                var match = Aliases.FuzzyMatch(remaining[i]);
                if (match == null)
                {
                    unmatched.Add(remaining[i]);
                }
                else if (match.Type == "card_type")
                {
                    cardTypeHits.Add((i, match.Canonical, new[] { remaining[i] }));
                }
                else if (match.Type == "trinket_tier")
                {
                    trinketTierHits.Add((i, match.Canonical, new[] { remaining[i] }));
                }
                else if (IsDuplicate(match, keywords, textKeywords, tribes, cardTypes, categories))
                {
                    unmatched.Add(remaining[i]);
                }
                else
                {
                    Route(match, keywords, textKeywords, tribes, cardTypes, categories);
                }
            }

            // Resolve card types: keep the earliest (by query position), demote the rest to text.
            if (cardTypeHits.Count > 0)
            {
                cardTypeHits.Sort((a, b) => a.Index.CompareTo(b.Index));
                cardTypes.Add(cardTypeHits[0].Canonical);
                for (int k = 1; k < cardTypeHits.Count; k++)
                    unmatched.AddRange(cardTypeHits[k].Tokens);
            }

            // Trinket tiers: same first-wins resolution (a trinket is lesser xor greater).
            if (trinketTierHits.Count > 0)
            {
                trinketTierHits.Sort((a, b) => a.Index.CompareTo(b.Index));
                trinketTiers.Add(trinketTierHits[0].Canonical);
                for (int k = 1; k < trinketTierHits.Count; k++)
                    unmatched.AddRange(trinketTierHits[k].Tokens);
            }

            bool isStructured =
                statsA.HasValue || tier.HasValue || manaCost.HasValue || keywords.Count > 0 ||
                textKeywords.Count > 0 || tribes.Count > 0 || cardTypes.Count > 0 ||
                categories.Count > 0 || trinketTiers.Count > 0;

            return new ParsedQuery
            {
                StatsAttack = statsA,
                StatsHealth = statsH,
                Tier = tier,
                ManaCost = manaCost,
                Keywords = keywords,
                TextKeywords = textKeywords,
                Tribes = tribes,
                CardTypes = cardTypes,
                Categories = categories,
                TrinketTiers = trinketTiers,
                NameQuery = string.Join(" ", unmatched),
                IsStructured = isStructured,
            };
        }

        private static bool IsDuplicate(AliasEntry match, List<string> keywords, List<AliasEntry> textKeywords,
            List<string> tribes, List<string> cardTypes, List<string> categories)
        {
            switch (match.Type)
            {
                case "keyword": return keywords.Contains(match.Canonical);
                case "text_keyword": return textKeywords.Exists(tk => tk.Canonical == match.Canonical);
                case "tribe": return tribes.Contains(match.Canonical);
                case "card_type": return cardTypes.Contains(match.Canonical);
                case "category": return categories.Contains(match.Canonical);
                default: return false;
            }
        }

        private static void Route(AliasEntry match, List<string> keywords, List<AliasEntry> textKeywords,
            List<string> tribes, List<string> cardTypes, List<string> categories)
        {
            switch (match.Type)
            {
                case "keyword": keywords.Add(match.Canonical); break;
                case "text_keyword": textKeywords.Add(match); break;
                case "card_type": cardTypes.Add(match.Canonical); break;
                case "category": categories.Add(match.Canonical); break;
                default: tribes.Add(match.Canonical); break;
            }
        }
    }
}
