using System.Collections.Generic;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// One Dark Gift's offering rules + display text. Source of truth: the official Battlegrounds
    /// dev-insights post (repo root: developer-insights-dark-gifts). Duplicate names are real — Battle
    /// Scars / Death's Embrace / Spell Siphon / Dexterity each exist twice with different stat values
    /// and turn windows ("43 different Dark Gifts" counts them separately).
    /// </summary>
    public sealed class DarkGift
    {
        public string Name;      // English — the key the static metadata and the pool rules match on
        public int MinTurn;
        public int? MaxTurn;     // null = no upper bound
        public string Text;      // effect (EN, from the dev post; site data may replace this later)
        public string DisplayName;  // what the panel shows: the card language's name when the site has one, else Name
        public string DisplayText;  // likewise for the effect text
        public string Note;      // short offering-condition note as a Strings catalog key ("gifts.note.…"), or null
        public string TribeOnly; // offerable only on this tribe (e.g. "Quilboar"), or null
        public bool TypedOnly;   // offerable only on minions WITH a type
        // Positive per-minion requirement the host must satisfy (drives the guaranteed-tribe pool
        // analysis): "Spellcraft" | "Deathrattle" | "EndOfTurn" | "Avenge" | "DivineShield" |
        // "Battlecry", or null for gifts without one.
        public string Requires;
        // Lobby-level removal: the devs pull the gift from any lobby whose tribe set includes one of
        // these (Toughened Shield ↔ Quilboar/Naga). Null for gifts without one.
        public string[] NotWithTribes;

        public DarkGift(string name, int min, int? max, string text, string note = null,
            string tribeOnly = null, bool typedOnly = false, string requires = null,
            string[] notWithTribes = null)
        {
            Name = name; MinTurn = min; MaxTurn = max; Text = text; Note = note;
            DisplayName = name; DisplayText = text;
            TribeOnly = tribeOnly; TypedOnly = typedOnly; Requires = requires;
            NotWithTribes = notWithTribes;
        }

        /// <summary>Offerable right now (turn windows only — per-minion conditions are the Note).</summary>
        public bool IsCurrent(int turn) => MinTurn <= turn && (!MaxTurn.HasValue || turn <= MaxTurn.Value);
        /// <summary>Becomes offerable on a later turn.</summary>
        public bool IsFuture(int turn) => MinTurn > turn;
        /// <summary>Window closed — can no longer appear this game.</summary>
        public bool IsGone(int turn) => MaxTurn.HasValue && turn > MaxTurn.Value;
    }

    /// <summary>The static rules table (dev-insights post, Season 14 launch — expected to drift with
    /// balance patches; update here when Blizzard changes the rules) + the site-data resolver: once
    /// hsbg.cards ships gift cards (categories:["darkgift"] with darkGiftMinTurn/MaxTurn), names,
    /// text and turn windows come FROM THE DATA and the static table only contributes what the data
    /// doesn't carry (condition notes + tribe/typed emphasis metadata) and serves as the offline
    /// fallback.</summary>
    public static class DarkGifts
    {
        /// <summary>The effective gift list: data-driven when the store has a (sane) darkgift set,
        /// else the static table. Call once per match — cheap, but no reason to re-scan per tick.</summary>
        public static IReadOnlyList<DarkGift> Resolve(Data.CardStore store)
        {
            try
            {
                var outp = new List<DarkGift>();
                foreach (var c in store?.All ?? new List<Search.BgCard>())
                {
                    if (c?.Categories == null || c.Name == null) continue;
                    bool isGift = false;
                    foreach (var cat in c.Categories)
                        if (string.Equals(cat, "darkgift", System.StringComparison.OrdinalIgnoreCase)) { isGift = true; break; }
                    if (!isGift) continue;

                    var st = FindStatic(c.Name, c.DarkGiftMinTurn);
                    int min; int? max;
                    if (c.DarkGiftMinTurn.HasValue) { min = c.DarkGiftMinTurn.Value; max = c.DarkGiftMaxTurn; }   // data authoritative (null max = open)
                    else { min = st?.MinTurn ?? 3; max = st?.MaxTurn; }
                    var gift = new DarkGift(c.Name, min, max, CleanText(c.Text), st?.Note, st?.TribeOnly, st?.TypedOnly ?? false, st?.Requires, st?.NotWithTribes);
                    // The panel reads the card language (English when the site has no translation).
                    gift.DisplayName = c.DisplayName ?? c.Name;
                    gift.DisplayText = CleanText(c.DisplayText ?? c.Text);
                    outp.Add(gift);
                }
                // Sanity: only trust the data when the set looks complete (a partial rollout or a
                // filtered store must not silently shrink the list).
                if (outp.Count >= 20)
                {
                    outp.Sort((a, b) => a.MinTurn != b.MinTurn
                        ? a.MinTurn.CompareTo(b.MinTurn)
                        : Search.SearchEngine.NameComparer.Compare(a.DisplayName, b.DisplayName));
                    return outp;
                }
            }
            catch { }
            return All;
        }

        // Static metadata for a data gift: same name; the dup-named pairs (Battle Scars etc.)
        // disambiguate by min turn when the data provides one.
        private static DarkGift FindStatic(string name, int? minTurn)
        {
            DarkGift first = null;
            foreach (var g in All)
            {
                if (!string.Equals(g.Name, name, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (minTurn.HasValue && g.MinTurn == minTurn.Value) return g;
                if (first == null) first = g;
            }
            return first;
        }

        // Site card text is HTML-ish (<b>…</b>) and may be multi-line; the panel renders plain text
        // with its own keyword coloring.
        private static string CleanText(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "");
            return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        }

        public static readonly IReadOnlyList<DarkGift> All = new[]
        {
            new DarkGift("Sunken Persistence", 3, null, "This minion's Spellcrafts are permanent.", "gifts.note.spellcraftOnly", requires: "Spellcraft"),
            new DarkGift("Harpy's Talons",     3, null, "Divine Shield, Windfury", "gifts.note.moreOftenOnRally"),
            new DarkGift("Jaws of Death",      3, null, "Start of Combat: Trigger this minion's Deathrattles.", "gifts.note.deathrattleOnly", requires: "Deathrattle"),
            new DarkGift("Fortitude",          3, 3,    "+4/+4."),
            new DarkGift("Affinity",           3, 4,    "At the end of every 2 turns, get a random minion of this type.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Sharpened Sword",    3, 5,    "Whenever you play a card, gain +2 Attack.", "gifts.note.notOnAvenge"),
            new DarkGift("Toughened Shield",   3, 5,    "Whenever you play a card, gain +2 Health.", "gifts.note.neverWithQuilboarOrNaga", notWithTribes: new[] { "Quilboar", "Naga" }),
            new DarkGift("Steady Growth",      3, 6,    "At the end of your turn, gain +1/+2 / +2/+2 / +3/+3 / +4/+4 (by offer turn)."),
            new DarkGift("Time Turning",       3, 9,    "This minion's end of turn effects also trigger at start of turn.", "gifts.note.endOfTurnOnly", requires: "EndOfTurn"),
            new DarkGift("Furtiveness",        4, null, "Stealth", "gifts.note.avengeOnly", requires: "Avenge"),
            new DarkGift("Consanguinity",      4, 5,    "Rally: Get 2 Blood Gems.", "gifts.note.quilboarOnly", tribeOnly: "Quilboar"),
            new DarkGift("Fresh Perspective",  4, 5,    "Deathrattle: Gain 2 free Refreshes."),
            new DarkGift("Replication",        4, 6,    "At the end of every 2 turns, get a plain copy of this."),
            new DarkGift("Battle Scars",       4, 6,    "Has +2/+2 for each Battlecry you've triggered this game.", "gifts.note.needsTriggeredBattlecry"),
            new DarkGift("Death's Embrace",    4, 6,    "Has +1/+1 for each Deathrattle you've triggered this game.", "gifts.note.needsTriggeredDeathrattle"),
            new DarkGift("Spell Siphon",       4, 6,    "Has +2/+2 for each Tavern spell you've cast this game.", "gifts.note.needsCastTavernSpell"),
            new DarkGift("Gilding",            4, 8,    "This is Golden, but doesn't give a Triple Reward.", "gifts.note.lowestTierNotActivateRare"),
            new DarkGift("Double Vision",      5, null, "Get an extra copy of this."),
            new DarkGift("Toreth's Blessing",  5, null, "This minion's Divine Shield takes 2 more hits to break.", "gifts.note.divineShieldOnly", requires: "DivineShield"),
            new DarkGift("Amalgamation",       5, null, "Has all minion types.", "gifts.note.typelessOnly"),
            new DarkGift("Demonology",         5, 8,    "Rally: Add a Fodder to your next 3 Refreshes.", "gifts.note.demonsOnly", tribeOnly: "Demon"),
            new DarkGift("Polarization",       5, 8,    "At the end of your turn, Magnetize a random Mech to this.", "gifts.note.mechsOnlyTier3", tribeOnly: "Mech"),
            new DarkGift("Mystic Essence",     5, 8,    "Deathrattle: Get a random Tavern spell."),
            new DarkGift("Tarecgosa's Blessing", 6, null, "Permanently keeps stats and Bonus Keywords gained in combat.", "gifts.note.dragonsOnly", tribeOnly: "Dragon"),
            new DarkGift("Dexterity",          6, 7,    "Whenever you play a card, this gains +2/+2.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Incubation",         6, 8,    "+2/+2. In two turns, double this minion's stats.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Echoing Voice",      6, null, "At the end of your turn, trigger this minion's Battlecries.", "gifts.note.battlecryOnly", requires: "Battlecry"),
            new DarkGift("Offensive Sacrifice", 6, 9,   "Deathrattle: Give this minion's Attack to another friendly minion.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Defensive Sacrifice", 6, 9,   "Deathrattle: Give this minion's max Health to another friendly minion.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Transcendence",      7, null, "Start of Combat: Triple this minion's stats.", "gifts.note.typelessOnly"),
            new DarkGift("Battle Scars",       7, null, "Has +3/+3 for each Battlecry you've triggered this game.", "gifts.note.needsTriggeredBattlecry"),
            new DarkGift("Death's Embrace",    7, null, "Has +2/+2 for each Deathrattle you've triggered this game.", "gifts.note.needsTriggeredDeathrattle"),
            new DarkGift("Spell Siphon",       7, null, "Has +3/+3 for each Tavern spell you've cast this game.", "gifts.note.needsCastTavernSpell"),
            new DarkGift("Admiration",         7, null, "Start of Combat: Gain the stats of the minion to the left."),
            new DarkGift("Toxicity",           7, null, "Venomous", "gifts.note.murlocsOnlyNotVenomous", tribeOnly: "Murloc"),
            new DarkGift("Charisma",           7, null, "Rally: Get a random minion of your most common type.", "gifts.note.notOnTauntAvenge"),
            new DarkGift("Resistance",         7, 10,   "Start of Combat: Double this minion's Health.", "gifts.note.notOnBeastsUndeadTypeless"),
            new DarkGift("Hostility",          7, 10,   "Start of Combat: Double this minion's Attack.", "gifts.note.notOnAvengeTypeless"),
            new DarkGift("Dexterity",          8, 9,    "Whenever you play a card, gain +4/+4.", "gifts.note.typedOnly", typedOnly: true),
            new DarkGift("Golemancy",          9, 10,   "Deathrattle: Summon a Golem with this minion's stats.", "gifts.note.notOnTypeless"),
            new DarkGift("Persisting Horror",  10, null, "Reborn. Is Reborn with full stats and Bonus Keywords.", "gifts.note.notOnTypelessRare"),
            new DarkGift("Titanic Strength",   11, null, "+1000 Attack.", "gifts.note.notOnDragonsRare"),
            new DarkGift("Invulnerability",    12, null, "Immune while attacking.", "gifts.note.notOnTauntTypelessRare"),
        };
    }
}
