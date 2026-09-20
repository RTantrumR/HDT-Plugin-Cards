using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HsbgCardLookup.Data
{
    /// <summary>
    /// Card text as the GAME words it, rebuilt from HearthDb's card definitions.
    ///
    /// Hearthstone computes a card's tooltip at display time and persists none of it: "Still Hungry
    /// +2/+1", "Interpreted +12/+4", "(7 turns left!)" exist on screen for as long as the cursor is
    /// over the card and nowhere else. Everything needed to rebuild them IS in the card data plus
    /// the entity's own tags, which is what this class does.
    ///
    /// **Do not build on <c>HearthDb.Card.Text</c>.** It is post-processed, and both of its
    /// transforms destroy information this feature needs (verified against HearthDb 36.2.0,
    /// 2026-09-20, 35 713 cards):
    ///
    /// 1. A card with several text VARIANTS stores them in one locale string separated by '@'.
    ///    <c>Card.Text</c> concatenates them with no separator, so BG32_HERO_002p reads
    ///    "…(7 turns left!)…(Done!)" as one run. 4 374 cards carry a raw '@'.
    /// 2. '@' is ALSO a value placeholder, and <c>Card.Text</c> fills it from the card
    ///    DEFINITION's default rather than the live entity: BGDUO31_208e "Scribbling" is raw
    ///    <c>+@/+@.</c> and renders as "+1/+1." no matter what the enchantment is actually worth.
    ///
    /// The raw string survives one level down, on the CARDTEXT tag (enum id 184) of the card def —
    /// present for all 30 650 cards that have any text at all. So this class reads that, splits the
    /// variants itself, and substitutes from the values the snapshot stored.
    ///
    /// Splitting on '@' naively is wrong for the same reason: <see cref="IsSeparator"/> is what
    /// tells a separator from a placeholder, and it is a measured heuristic, not a parse — the
    /// format is Blizzard's and undocumented. Everything degrades to "show the whole text", never
    /// to an exception and never to a blank.
    ///
    /// This file is HearthDb-only on purpose: no WPF, and no HDT <c>Database</c>. A stored match
    /// renders with no game running, and the Final Board's offscreen PNG export depends on that.
    /// </summary>
    internal static class CardText
    {
        /// <summary>CARDTEXT — the card def tag holding the localized text template.</summary>
        private const int CardTextTag = 184;

        /// <summary>ENCHANTMENT_INVISIBLE — the client's own "never draw this one" flag.</summary>
        private const int EnchantmentInvisibleTag = 976;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string[]> VariantCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> InvisibleCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<HearthDb.Enums.Locale, System.Reflection.PropertyInfo> LocProps =
            new Dictionary<HearthDb.Enums.Locale, System.Reflection.PropertyInfo>();

        // ── card data ───────────────────────────────────────────────────────────────────────────

        /// <summary>The card definition, or null — including for an id a newer patch added and the
        /// shipped HearthDb has never heard of.</summary>
        internal static HearthDb.Card Def(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return null;
            try
            {
                HearthDb.Card c;
                return HearthDb.Cards.All.TryGetValue(cardId, out c) ? c : null;
            }
            catch { return null; }
        }

        /// <summary>The card's display name, or the raw id when HearthDb does not know it — never
        /// blank, because a blank row in a tooltip reads as a rendering fault.</summary>
        internal static string Name(string cardId)
        {
            var c = Def(cardId);
            string n = null;
            try { n = c?.Name; } catch { }
            return string.IsNullOrEmpty(n) ? (cardId ?? "") : n;
        }

        /// <summary>
        /// The card def's ENCHANTMENT_INVISIBLE flag — the same one the client tests before drawing
        /// an enchantment. Locale-independent, unlike an English text match, and surgical on real
        /// data: over the two 2026-09-20 records it drops exactly the Blood Gem banner (129
        /// attachments) and the shop's "Costs 0" marker (102), and nothing a player would call a buff.
        /// </summary>
        internal static bool IsInvisible(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return false;
            lock (Gate)
            {
                bool hit;
                if (InvisibleCache.TryGetValue(cardId, out hit)) return hit;
                hit = TagValue(cardId, EnchantmentInvisibleTag) == 1;
                InvisibleCache[cardId] = hit;
                return hit;
            }
        }

        private static int TagValue(string cardId, int enumId)
        {
            try
            {
                var tags = Def(cardId)?.Entity?.Tags;
                if (tags == null) return 0;
                foreach (var t in tags) if (t != null && t.EnumId == enumId) return t.Value;
            }
            catch { }
            return 0;
        }

        // ── variants ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The card's text variants, in the order the client indexes them with
        /// <c>USE_ALTERNATE_CARD_TEXT</c>. Always at least one entry; empty array only when the card
        /// has no text at all. Cached — a turn's worth of hovering asks for the same few hundred ids.
        /// </summary>
        internal static string[] Variants(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return Empty;
            lock (Gate)
            {
                string[] v;
                if (VariantCache.TryGetValue(cardId, out v)) return v;
                v = BuildVariants(cardId);
                VariantCache[cardId] = v;
                return v;
            }
        }

        private static readonly string[] Empty = new string[0];

        private static string[] BuildVariants(string cardId)
        {
            string raw = null;
            try { raw = Raw(cardId); } catch { }
            if (string.IsNullOrEmpty(raw))
            {
                // No locale string. Fall back to the processed text whole — concatenated variants and
                // default-filled placeholders are both worse than nothing, but blank is worse still.
                string t = null;
                try { t = Def(cardId)?.Text; } catch { }
                return string.IsNullOrEmpty(t) ? Empty : new[] { t };
            }
            return Split(raw);
        }

        /// <summary>The untouched locale string for HearthDb's current language, falling back to
        /// en-US (the only locale guaranteed filled for every card).</summary>
        private static string Raw(string cardId)
        {
            var def = Def(cardId);
            var tags = def?.Entity?.Tags;
            if (tags == null) return null;
            HearthDb.CardDefs.Tag tag = null;
            foreach (var t in tags) if (t != null && t.EnumId == CardTextTag) { tag = t; break; }
            if (tag == null) return null;

            string s = null;
            var prop = LocProp(def.DefaultLanguage);
            if (prop != null) { try { s = prop.GetValue(tag, null) as string; } catch { } }
            return string.IsNullOrEmpty(s) ? tag.LocStringEnUs : s;
        }

        /// <summary>
        /// HearthDb spells the per-locale properties <c>LocStringEnUs</c>, <c>LocStringPtBr</c>,
        /// <c>LocStringZhCn</c> — the enum's own "enUS"/"ptBR"/"zhCN" with the case flipped on the
        /// second half. Two enum members (enGB, ptPT) have no property of their own and fall through
        /// to en-US, which is what the null return means.
        /// </summary>
        private static System.Reflection.PropertyInfo LocProp(HearthDb.Enums.Locale loc)
        {
            System.Reflection.PropertyInfo p;
            if (LocProps.TryGetValue(loc, out p)) return p;
            p = null;
            try
            {
                var n = loc.ToString();
                if (n.Length == 4)
                {
                    var name = "LocString"
                        + char.ToUpperInvariant(n[0]) + char.ToLowerInvariant(n[1])
                        + char.ToUpperInvariant(n[2]) + char.ToLowerInvariant(n[3]);
                    p = typeof(HearthDb.CardDefs.Tag).GetProperty(name);
                }
            }
            catch { }
            LocProps[loc] = p;
            return p;
        }

        /// <summary>
        /// Cut the locale string into variants at every separator '@'. When no '@' qualifies the
        /// whole string is the single variant, which is the right answer for the ~26 000 cards that
        /// have only one and the safe answer when the heuristic is unsure.
        /// </summary>
        private static string[] Split(string raw)
        {
            List<int> cuts = null;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '@' || !IsSeparator(raw, i)) continue;
                (cuts ?? (cuts = new List<int>())).Add(i);
            }
            if (cuts == null) return new[] { raw };

            var parts = new List<string>(cuts.Count + 1);
            int from = 0;
            foreach (var cut in cuts)
            {
                parts.Add(raw.Substring(from, cut - from));
                from = cut + 1;
            }
            parts.Add(raw.Substring(from));

            // A cut that leaves a stub behind was not a cut: '<b>@</b>' passes the character tests
            // and is a placeholder inside markup. One thin part condemns the whole split rather than
            // just itself — a partial cut would show half a variant, which is worse than showing both.
            foreach (var p in parts) if (Weight(p) < 3) return new[] { raw };
            return parts.ToArray();
        }

        /// <summary>How much real text a candidate variant has, ignoring markup and whitespace.</summary>
        private static int Weight(string s)
        {
            int n = 0;
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (inTag || char.IsWhiteSpace(c) || c == '_') continue;
                if (c == '[' && i + 2 < s.Length && s[i + 1] == 'x' && s[i + 2] == ']') { i += 2; continue; }
                n++;
            }
            return n;
        }

        /// <summary>
        /// Separator or placeholder? '@' is both in this format, and telling them apart is the one
        /// genuinely unsafe step in the file. Measured over every card in HearthDb: a separator ends
        /// the previous sentence and opens the next variant, while a placeholder sits where a NUMBER
        /// goes — mid-phrase, after '+' or '(' or a word.
        /// </summary>
        private static bool IsSeparator(string s, int at)
        {
            if (at <= 0 || at >= s.Length - 1) return false;

            // The variant before it reads as finished: a sentence end, or a closing tag/bracket.
            int j = at - 1;
            while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
            if (j < 0) return false;
            if (".!?)>]\":".IndexOf(s[j]) < 0) return false;

            // The variant after it reads as a beginning: '[x]' layout marker, a tag, a new line, or
            // a capital. A placeholder is followed by a space, a '/' or a unit ("@ turns", "+@/+@").
            char next = s[at + 1];
            return next == '[' || next == '<' || next == '\n' || next == '\r' || char.IsUpper(next);
        }

        // ── rendering ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One card's text as the game would show it at this moment: the chosen variant with its
        /// numbers filled in and its markup removed. Returns "" when the card has no text, which the
        /// caller should read as "show the name alone" — several real enchantments (Satellite,
        /// Booming) carry their whole meaning in the name.
        /// </summary>
        /// <param name="variant">USE_ALTERNATE_CARD_TEXT (tag 955). Clamped, never trusted.</param>
        /// <param name="n1">TAG_SCRIPT_DATA_NUM_1 (tag 2) — the template's {0}.</param>
        /// <param name="n2">TAG_SCRIPT_DATA_NUM_2 (tag 3) — the template's {1}.</param>
        internal static string Render(string cardId, int variant, int n1, int n2)
        {
            try
            {
                var vs = Variants(cardId);
                if (vs.Length == 0) return "";
                var t = vs[variant >= 0 && variant < vs.Length ? variant : 0];
                return StripMarkup(Fill(t, n1, n2));
            }
            catch { return ""; }
        }

        private static readonly Regex Placeholder = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);
        private static readonly Regex Plural = new Regex(@"\|4\(([^,)]*),\s*([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex Markup = new Regex(@"<[^>]*>", RegexOptions.Compiled);

        /// <summary>
        /// Put the entity's two script numbers into <c>{0}</c> and <c>{1}</c>, and leave every other
        /// notation exactly as it was.
        ///
        /// This is the conservative half, and the split from <see cref="Fill"/> is deliberate: it is
        /// what <c>MatchRecorder</c> calls, its output goes into a CSV people have already collected,
        /// and a template artifact that has always been in a column stays in that column. Verified
        /// character-for-character against the inline version it replaced, over all 30 650 cards with
        /// text (2026-09-20).
        /// </summary>
        internal static string Substitute(string text, int n1, int n2)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                if (text.IndexOf("{0}", StringComparison.Ordinal) >= 0)
                    text = text.Replace("{0}", n1.ToString(CultureInfo.InvariantCulture));
                if (text.IndexOf("{1}", StringComparison.Ordinal) >= 0)
                    text = text.Replace("{1}", n2.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
            return text;
        }

        /// <summary>
        /// Resolve every value notation a raw locale string can carry — the display half, used only
        /// by <see cref="Render"/>.
        ///
        /// Three notations, all Blizzard's: <c>{0}</c>/<c>{1}</c> index the script numbers directly;
        /// a non-separator <c>@</c> is a positional slot, taking n1 then n2 in order of appearance
        /// (<c>+@/+@.</c> is attack then health); and <c>|4(one, many)</c> picks a word by the number
        /// just before it, which is how "(1 turn left!)" avoids reading "1 turns".
        ///
        /// An index past {1} has no tag behind it — a handful of Battlegrounds cards use {2}. It
        /// renders as "?" rather than a guess, on the rule the stats view already follows: an absent
        /// number is honest, a confident wrong one gets acted on.
        /// </summary>
        internal static string Fill(string text, int n1, int n2)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                var s = text;
                if (s.IndexOf('@') >= 0)
                {
                    var sb = new StringBuilder(s.Length + 8);
                    int seen = 0;
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (s[i] != '@') { sb.Append(s[i]); continue; }
                        sb.Append((seen++ == 0 ? n1 : n2).ToString(CultureInfo.InvariantCulture));
                    }
                    s = sb.ToString();
                }
                s = Placeholder.Replace(s, m =>
                {
                    int k;
                    if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out k)) return "?";
                    if (k == 0) return n1.ToString(CultureInfo.InvariantCulture);
                    if (k == 1) return n2.ToString(CultureInfo.InvariantCulture);
                    return "?";
                });
                s = Plural.Replace(s, m => PluralFor(s, m.Index) ? m.Groups[1].Value : m.Groups[2].Value);
                return s;
            }
            catch { return text; }
        }

        /// <summary>True when the number immediately before a <c>|4(…)</c> is exactly one. No number
        /// in front of it — the count was a {2} we could not fill — reads as plural, which is the
        /// form that survives being wrong.</summary>
        private static bool PluralFor(string s, int at)
        {
            int j = at - 1;
            while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
            int end = j;
            while (j >= 0 && s[j] >= '0' && s[j] <= '9') j--;
            if (j == end) return false;
            int v;
            return int.TryParse(s.Substring(j + 1, end - j), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v == 1;
        }

        /// <summary>
        /// The card's words with the client's layout directives taken out: the <c>[x]</c> "this text
        /// is pre-wrapped" marker, the bold/italic tags, the <c>__</c> and non-breaking-space
        /// indents, and the hard line breaks the card art needed. What is left is one line of prose.
        /// </summary>
        internal static string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var s = Markup.Replace(text, "");
                s = s.Replace("[x]", " ").Replace("__", " ").Replace(' ', ' ');
                return Collapse(s);
            }
            catch { return text; }
        }

        /// <summary>Every run of whitespace down to one space, ends trimmed.</summary>
        internal static string Collapse(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return string.Join(" ", text.Split(WhiteSpace, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static readonly char[] WhiteSpace = { '\n', '\r', '\t', ' ' };

        // ── hoisted from MatchRecorder, behaviour unchanged ─────────────────────────────────────

        /// <summary>
        /// Port of HSBot's <c>clean_effect_text</c>, moved here verbatim so the CSV recorder and the
        /// panel share one definition of "the effect" — with the two destructive steps behind flags,
        /// because they are right for a spreadsheet column and wrong for a tooltip. A CSV cell wants
        /// "+8/+8" bounded at 80 characters; a tooltip must show the game's own sentence, lead-in and
        /// all. <c>MatchRecorder</c> passes true/80 and its output is unchanged.
        /// </summary>
        internal static string Clean(string text, bool stripLeadIns, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = Collapse(text).TrimEnd('.');
            if (stripLeadIns)
            {
                foreach (var p in LeadIns)
                    if (t.StartsWith(p, StringComparison.Ordinal)) { t = t.Substring(p.Length); break; }
                foreach (var p in GiveIns)
                    if (t.StartsWith(p, StringComparison.Ordinal)) { t = t.Substring(p.Length); break; }
            }
            t = t.Replace("______", "").Trim();
            if (maxLen > 3 && t.Length > maxLen) t = t.Substring(0, maxLen - 3) + "...";
            return t;
        }

        private static readonly string[] LeadIns =
            { "Battlecry: ", "Deathrattle: ", "Start of Combat: ", "End of Turn: ", "Passive: ", "Choose One - " };

        private static readonly string[] GiveIns =
            { "Give a friendly minion ", "Give a minion ", "Give your minions ", "Give all ", "Give your " };

        /// <summary>
        /// Shop/UI marker enchantments (purchasable/triple/cost state) - not gameplay buffs. They
        /// render as "Costs (N)" or carry "Drag To Buy". Real buffs are stat/keyword effects, so this
        /// never drops them. English-text heuristic: prefer <see cref="IsInvisible"/> wherever a card
        /// id is at hand, which is the client's own locale-independent flag for the same idea.
        /// </summary>
        internal static bool IsShopMarker(string text) =>
            !string.IsNullOrEmpty(text) &&
            (CostMarker.IsMatch(text) || text.IndexOf("Drag To Buy", StringComparison.OrdinalIgnoreCase) >= 0);

        private static readonly Regex CostMarker = new Regex(@"^Costs\s*\(\d+\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Engine bookkeeping by card id — the tavern's own enchantment families.</summary>
        internal static bool IsNoiseId(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return false;
            return cardId.StartsWith("TB_BaconShop_", StringComparison.OrdinalIgnoreCase)
                || cardId.StartsWith("Bacon_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Engine bookkeeping by name (mirrors HSBot's [DNT] + system-name filtering).</summary>
        internal static bool IsNoiseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("[DNT]", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("(DNT)", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PlayerEnchant", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Tripled/golden ids carry a trailing _G with no record of their own.</summary>
        internal static string StripGold(string cardId) =>
            string.IsNullOrEmpty(cardId) ? cardId
                : (cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId.Substring(0, cardId.Length - 2) : cardId);
    }
}
