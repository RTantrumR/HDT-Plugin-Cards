using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HsbgCardLookup.Search
{
    public sealed class SearchResult
    {
        public List<BgCard> Cards = new List<BgCard>();
        public ParsedQuery Parsed;     // null for Simple search
        public bool IsFuzzy;
    }

    /// <summary>
    /// Port of the web app's lib/search. Smart = structured (t3 / 5/5 / tribes / keywords), sorted
    /// tier→name. Simple = name/text substring + fuzzy fallback, relevance-ranked.
    /// </summary>
    public static class SearchEngine
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex WordSplit = new Regex(@"[\s\-,']+", RegexOptions.Compiled);

        public static string Normalize(string s) => Whitespace.Replace(s.ToLowerInvariant(), "");

        private static string[] Words(string s) =>
            WordSplit.Split(s).Where(w => w.Length > 0).ToArray();

        public static int FuzzyNameScore(string query, string cardName)
        {
            string qn = Normalize(query);
            int threshold = Math.Max(1, qn.Length / 3);

            // 1. Full normalized name
            string cn = Normalize(cardName);
            if (Math.Abs(cn.Length - qn.Length) <= threshold + 2)
            {
                int fullDist = Levenshtein.Distance(qn, cn);
                if (fullDist <= threshold) return fullDist;
            }

            // 2. Word-level
            int best = int.MaxValue;
            foreach (var word in Words(cardName.ToLowerInvariant()))
            {
                if (Math.Abs(word.Length - qn.Length) <= threshold + 2)
                {
                    int dist = Levenshtein.Distance(qn, word);
                    if (dist < best) best = dist;
                }
                if (word.Length > qn.Length)
                {
                    int kMin = Math.Max(3, qn.Length - 1);
                    int kMax = Math.Min(qn.Length + 2, word.Length);
                    for (int k = kMin; k <= kMax; k++)
                    {
                        string prefix = word.Substring(0, k);
                        int dist = Levenshtein.Distance(qn, prefix);
                        if (dist < best) best = dist;
                    }
                }
            }
            return best;
        }

        private static int ScoreCard(BgCard card, string query)
        {
            string qTrim = query.Trim();
            string qLower = qTrim.ToLowerInvariant();
            string qn = Normalize(qTrim);
            string nLower = card.Name.ToLowerInvariant();
            string nn = Normalize(card.Name);

            int nameScore = 0;
            if (qn.Length > 0)
            {
                if (nn == qn) nameScore = 1000;
                else if (nn.StartsWith(qn, StringComparison.Ordinal)) nameScore = 800 - Math.Min(50, nn.Length - qn.Length);
                else
                {
                    var words = Words(nLower);
                    if (words.Any(w => w.StartsWith(qLower, StringComparison.Ordinal)))
                        nameScore = 600 - Math.Min(40, nn.Length - qn.Length);
                    else if (nn.IndexOf(qn, StringComparison.Ordinal) >= 0)
                        nameScore = 400 - Math.Min(30, nn.Length - qn.Length);
                    else if (qn.Length >= 4)
                    {
                        int d = FuzzyNameScore(qTrim, card.Name);
                        int threshold = Math.Max(1, qn.Length / 3);
                        if (d <= threshold) nameScore = Math.Max(50, 200 - d * 30);
                    }
                }
            }

            int textScore = 0;
            string textLower = (card.Text ?? "").ToLowerInvariant();
            if (qLower.Length > 0 && textLower.IndexOf(qLower, StringComparison.Ordinal) >= 0)
                textScore = 50;

            int tierBonus = card.Tier.HasValue ? Math.Max(0, 8 - card.Tier.Value) : 0;
            return nameScore + textScore + (nameScore > 0 ? tierBonus : 0);
        }

        private static List<BgCard> SortByRelevance(IEnumerable<BgCard> cards, string query) =>
            cards.OrderByDescending(c => ScoreCard(c, query)).ToList();

        private static List<BgCard> SortTierThenName(IEnumerable<BgCard> cards) =>
            cards.OrderBy(c => c.Tier ?? 99)
                 .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                 .ToList();

        public static SearchResult Smart(IList<BgCard> cards, string query)
        {
            var parsed = QueryParser.Parse(query);
            IEnumerable<BgCard> result = cards;

            if (parsed.HasStats)
                result = result.Where(c => c.Attack == parsed.StatsAttack && c.Health == parsed.StatsHealth);

            if (parsed.Tier.HasValue)
                result = result.Where(c => c.Tier == parsed.Tier);

            foreach (var keyword in parsed.Keywords)
            {
                string kw = keyword.ToLowerInvariant();
                result = result.Where(c =>
                    (c.Keywords ?? new List<string>()).Any(k => k.ToLowerInvariant() == kw) ||
                    c.Name.ToLowerInvariant().Contains(kw) ||
                    (c.Text ?? "").ToLowerInvariant().Contains(kw));
            }

            foreach (var tk in parsed.TextKeywords)
            {
                string pattern = (tk.TextPattern ?? tk.Canonical).ToLowerInvariant();
                result = result.Where(c => (c.Text ?? "").ToLowerInvariant().Contains(pattern));
            }

            // Card type filters first; then a tribe/category no card of that type has is demoted to
            // the name/text query (`demotedTerms`) instead of zeroing results — but only when a card
            // type was parsed. All/Neutral stay structural, never demoted.
            foreach (var cardType in parsed.CardTypes)
                result = result.Where(c => c.CardType == cardType);
            var res = result.ToList();
            var demotedTerms = new List<string>();

            // Snapshot the pre-tribe pool for the name-query union ("beast beast").
            List<BgCard> preTribe = parsed.Tribes.Count > 0 ? res : null;
            var appliedTribes = new List<string>();
            foreach (var tribe in parsed.Tribes)
            {
                string t = tribe;
                Func<BgCard, bool> hasTribe = c =>
                    t == "Neutral" ? (c.MinionTypes ?? new List<string>()).Count == 0
                    : t == "All" ? (c.MinionTypes ?? new List<string>()).Contains("All")
                    : (c.MinionTypes ?? new List<string>()).Contains(t);
                if (parsed.CardTypes.Count > 0 && t != "Neutral" && t != "All" && !res.Any(hasTribe))
                    demotedTerms.Add(t);
                else { res = res.Where(hasTribe).ToList(); appliedTribes.Add(t); }
            }

            // Categories (minion subcategories + spell schools).
            foreach (var cat in parsed.Categories)
            {
                string ct = cat;
                Func<BgCard, bool> hasCat = c =>
                    (c.Categories ?? new List<string>()).Contains(ct) ||
                    (c.SpellSchool != null && c.SpellSchool.ToLowerInvariant() == ct);
                if (parsed.CardTypes.Count > 0 && !res.Any(hasCat))
                    demotedTerms.Add(ct);
                else res = res.Where(hasCat).ToList();
            }

            // Name/text search with fuzzy fallback. Demoted facet words join the name query.
            bool isFuzzy = false;
            string effectiveNameQuery = string.Join(" ",
                new[] { parsed.NameQuery }.Concat(demotedTerms).Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(effectiveNameQuery))
            {
                var resultList = res;
                var queryWords = Words2(effectiveNameQuery.ToLowerInvariant());

                // Only tribes that were actually applied as a filter count for the union; demoted
                // tribes are already handled by the substring branch below.
                bool nameOverlapsTribe = preTribe != null &&
                    appliedTribes.Any(t => t.ToLowerInvariant() == (parsed.NameQuery ?? "").ToLowerInvariant());

                if (nameOverlapsTribe)
                {
                    var textMatches = preTribe.Where(c =>
                    {
                        string nameLower = c.Name.ToLowerInvariant();
                        string textLower = (c.Text ?? "").ToLowerInvariant();
                        return queryWords.All(w => nameLower.Contains(w) || textLower.Contains(w));
                    });
                    var idSet = new HashSet<int>(resultList.Select(c => c.Id));
                    foreach (var card in textMatches)
                        if (!idSet.Contains(card.Id)) resultList.Add(card);
                    res = resultList;
                }
                else
                {
                    var substring = resultList.Where(c =>
                    {
                        string nameLower = c.Name.ToLowerInvariant();
                        string textLower = (c.Text ?? "").ToLowerInvariant();
                        return queryWords.All(w => nameLower.Contains(w) || textLower.Contains(w));
                    }).ToList();

                    if (substring.Count > 0)
                    {
                        res = substring;
                    }
                    else
                    {
                        var fuzzy = resultList.Where(c =>
                        {
                            string nameLower = c.Name.ToLowerInvariant();
                            string textLower = (c.Text ?? "").ToLowerInvariant();
                            return queryWords.All(w =>
                            {
                                if (nameLower.Contains(w) || textLower.Contains(w)) return true;
                                if (w.Length >= 4)
                                {
                                    int threshold = Math.Max(1, w.Length / 3);
                                    return FuzzyNameScore(w, c.Name) <= threshold;
                                }
                                return false;
                            });
                        }).ToList();

                        if (fuzzy.Count > 0) { res = fuzzy; isFuzzy = true; }
                        else res = new List<BgCard>();
                    }
                }
            }

            return new SearchResult { Cards = SortTierThenName(res), Parsed = parsed, IsFuzzy = isFuzzy };
        }

        public static SearchResult Simple(IList<BgCard> cards, string query)
        {
            var queryWords = Words2(query.ToLowerInvariant());

            var substring = cards.Where(c =>
            {
                string nameLower = c.Name.ToLowerInvariant();
                string textLower = (c.Text ?? "").ToLowerInvariant();
                return queryWords.All(w => nameLower.Contains(w) || textLower.Contains(w));
            });
            var subList = substring.ToList();
            if (subList.Count > 0)
                return new SearchResult { Cards = SortByRelevance(subList, query), IsFuzzy = false };

            var fuzzy = cards.Where(c =>
            {
                string nameLower = c.Name.ToLowerInvariant();
                string textLower = (c.Text ?? "").ToLowerInvariant();
                return queryWords.All(w =>
                {
                    if (nameLower.Contains(w) || textLower.Contains(w)) return true;
                    if (w.Length >= 4)
                    {
                        int threshold = Math.Max(1, w.Length / 3);
                        return FuzzyNameScore(w, c.Name) <= threshold;
                    }
                    return false;
                });
            }).ToList();

            if (fuzzy.Count > 0)
                return new SearchResult { Cards = SortByRelevance(fuzzy, query), IsFuzzy = true };

            return new SearchResult();
        }

        // query.split(/\s+/).filter(Boolean)
        private static string[] Words2(string s) =>
            Whitespace.Split(s).Where(w => w.Length > 0).ToArray();
    }
}
