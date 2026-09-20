using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HearthDb.Enums;
using HsbgCardLookup.Data;
using HsbgCardLookup.Game.FinalBoard;

namespace HsbgCardLookup.Ui.FinalBoard
{
    /// <summary>
    /// What the game shows when you hover a card, rebuilt from a stored snapshot.
    ///
    /// Hearthstone puts a minion's enchantments on screen only while the cursor is over it, and
    /// keeps none of them afterwards: how a 4/3 became a 538/537, which trinket was still counting
    /// down on turn 9, whether the hero power had been spent — all of it is gone the moment the
    /// match ends. The capture side already records it; this turns a record back into the sentences
    /// the game would have written.
    ///
    /// Everything here is presentation over data that is already on disk. It filters the way the
    /// client filters (<see cref="CardText.IsInvisible"/>) rather than the way a debug dump would:
    /// the Blood Gem banner and the shop's "Costs 0" marker are attached to real minions in every
    /// record and are not buffs, and a list that shows them reads as a log rather than a tooltip.
    /// The raw rows stay in the record either way.
    /// </summary>
    internal static class FinalBoardTips
    {
        /// <summary>One line of a tooltip: what the enchantment is called, what it did, and where it
        /// came from.</summary>
        internal sealed class Row
        {
            public string Name;
            public string Text;
            public string Source;
            public int Count;
        }

        // ── the data half (no WPF — this is the part worth checking against a record) ────────────

        /// <summary>
        /// Every enchantment on one host in one snapshot, as displayable rows, in capture order.
        ///
        /// Rows that render identically are merged and counted; rows that merely share a card id are
        /// NOT. Two Surging Mana instances worth +42/+33 and +50/+41 are two separate things the game
        /// shows separately, and summing "Stats set to {0}/{1}" would produce a number that never
        /// existed.
        /// </summary>
        internal static List<Row> Rows(ShopSnap snap, int hostId)
        {
            var outp = new List<Row>();
            if (snap?.Enchants == null || hostId <= 0) return outp;
            try
            {
                var seen = new Dictionary<string, Row>(StringComparer.Ordinal);
                foreach (var e in snap.Enchants)
                {
                    if (e == null || e.Host != hostId || string.IsNullOrEmpty(e.CardId)) continue;
                    if (CardText.IsInvisible(e.CardId)) continue;

                    var name = CardText.Name(e.CardId);
                    if (CardText.IsNoiseName(name)) continue;

                    var text = CardText.Render(e.CardId, 0, e.N1, e.N2);
                    if (CardText.IsShopMarker(text)) continue;

                    var source = SourceName(e.Source, name);
                    var key = name + "\u0001" + text + "\u0001" + (source ?? "");

                    Row row;
                    if (seen.TryGetValue(key, out row)) { row.Count += Math.Max(1, e.Count); continue; }
                    row = new Row { Name = name, Text = text, Source = source, Count = Math.Max(1, e.Count) };
                    seen[key] = row;
                    outp.Add(row);
                }
            }
            catch { }
            return outp;
        }

        /// <summary>The creator's name, or null when naming it adds nothing — an unknown source, the
        /// engine's own [DNT] bookkeeping, or a source that is simply the enchantment again.</summary>
        private static string SourceName(string sourceId, string enchantName)
        {
            if (string.IsNullOrEmpty(sourceId)) return null;
            var n = CardText.Name(sourceId);
            if (string.IsNullOrEmpty(n) || CardText.IsNoiseName(n)) return null;
            return string.Equals(n, enchantName, StringComparison.Ordinal) ? null : n;
        }

        // ── the visual half ─────────────────────────────────────────────────────────────────────

        private const double TipWidth = 340;

        /// <summary>
        /// A board or hand card's tooltip: its name, then one line per enchantment. Null when there
        /// is nothing to say — a plain minion with no buffs gets no tooltip at all rather than an
        /// empty box, which is also what stops the panel sprouting hover targets that do nothing.
        /// </summary>
        internal static UIElement Enchants(string cardName, ShopSnap snap, int hostId)
        {
            var rows = Rows(snap, hostId);
            if (rows.Count == 0) return null;
            var box = Box(cardName);
            foreach (var r in rows) box.Children.Add(Line(r));
            return box;
        }

        /// <summary>
        /// The hero power as it stood at this moment: its name, the countdown or charge text the
        /// game would be showing, and whether it had already been used this turn.
        /// </summary>
        internal static UIElement Power(MinionRecord power)
        {
            if (power == null || string.IsNullOrEmpty(power.CardId)) return null;
            var box = Box(CardText.Name(power.CardId));
            var text = Describe(power);
            if (!string.IsNullOrEmpty(text)) box.Children.Add(Body(text));
            if (Tag(power, GameTag.EXHAUSTED) == 1) box.Children.Add(Note("used this turn"));
            return box;
        }

        /// <summary>A trinket's name and its current text — the same "(N left!)" the shop shows.</summary>
        internal static UIElement Trinket(MinionRecord trinket)
        {
            if (trinket == null || string.IsNullOrEmpty(trinket.CardId)) return null;
            var box = Box(CardText.Name(trinket.CardId));
            var text = Describe(trinket);
            if (!string.IsNullOrEmpty(text)) box.Children.Add(Body(text));
            return box;
        }

        /// <summary>
        /// A stored card's text for the variant and numbers it was carrying at that moment. The
        /// three tags are all in the FULL dictionaries that trinkets and hero powers are stored
        /// with — which is why they are stored that way and the board is not.
        /// </summary>
        private static string Describe(MinionRecord m) =>
            CardText.Render(m.CardId,
                Tag(m, GameTag.USE_ALTERNATE_CARD_TEXT),
                Tag(m, GameTag.TAG_SCRIPT_DATA_NUM_1),
                Tag(m, GameTag.TAG_SCRIPT_DATA_NUM_2));

        private static int Tag(MinionRecord m, GameTag tag)
        {
            int v;
            return m?.Tags != null && m.Tags.TryGetValue((int)tag, out v) ? v : 0;
        }

        /// <summary>The entity id a snapshot's enchantments are keyed by. Zero when the record
        /// predates the whitelist carrying it, which reads as "no enchantments" rather than as a
        /// wrong join.</summary>
        internal static int HostId(MinionRecord m) => Tag(m, GameTag.ENTITY_ID);

        // ── pieces ──────────────────────────────────────────────────────────────────────────────

        private static StackPanel Box(string title)
        {
            var box = new StackPanel { MaxWidth = TipWidth };
            if (!string.IsNullOrEmpty(title))
                box.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = UiKit.TextPrimary,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            return box;
        }

        /// <summary>One enchantment: its name, the effect in the panel's keyword colours, the count
        /// when the same buff landed more than once, and the source when it names something the
        /// player would recognise.</summary>
        private static UIElement Line(Row r)
        {
            var tb = new TextBlock
            {
                Foreground = UiKit.TextSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            };
            tb.Inlines.Add(new System.Windows.Documents.Run(r.Name)
            {
                Foreground = UiKit.TextPrimary,
                FontWeight = FontWeights.SemiBold,
            });
            if (!string.IsNullOrEmpty(r.Text))
            {
                tb.Inlines.Add(new System.Windows.Documents.Run("  "));
                UiKit.ColorRuns(tb, r.Text);
            }
            if (r.Count > 1)
                tb.Inlines.Add(new System.Windows.Documents.Run(
                    "  ×" + r.Count.ToString(CultureInfo.InvariantCulture))
                { Foreground = UiKit.TextMuted });
            if (!string.IsNullOrEmpty(r.Source))
                tb.Inlines.Add(new System.Windows.Documents.Run("  ← " + r.Source)
                { Foreground = UiKit.TextMuted, FontSize = 11 });
            return tb;
        }

        private static UIElement Body(string text)
        {
            var tb = new TextBlock
            {
                Foreground = UiKit.TextSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            UiKit.ColorRuns(tb, text);
            return tb;
        }

        private static UIElement Note(string text) => new TextBlock
        {
            Text = text,
            Foreground = UiKit.TextMuted,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
        };
    }
}
