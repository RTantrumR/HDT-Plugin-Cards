using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// What a player did during a match, as opposed to what they ended up with. This is the half
    /// Hearthstone does not show anywhere: the board, hero power and trinkets are all on the game's
    /// own end screens, but how much gold went unspent, how often the tavern was rolled, how fast
    /// the hands moved — none of that exists until something counts it.
    ///
    /// Counter semantics follow Reign-in-blood's HDT-FinalStatsPlugin (MIT) where they overlap; his
    /// AGENTS.md §11 documents both the rules and the ways each one goes wrong, which is worth more
    /// than the code. <see cref="MinionsSold"/> and the APM figures are ours — his plugin tracks
    /// neither.
    ///
    /// A missing value is left at zero and rendered as "—" rather than guessed. That is his rule and
    /// ours: a confident wrong number is worse than an absent one, because a player will act on it.
    /// </summary>
    internal sealed class MatchStats
    {
        // ── economy ─────────────────────────────────────────────────────────────────────────────
        public int GoldSpent { get; set; }
        public int TavernRolls { get; set; }
        public int TavernUpgrades { get; set; }
        public int Freezes { get; set; }
        public int TriplesCreated { get; set; }
        public int HeroPowersUsed { get; set; }
        public int MinionsBought { get; set; }
        public int SpellsBought { get; set; }
        public int MinionsSold { get; set; }
        public int MinionsPlayed { get; set; }
        public int SpellsPlayed { get; set; }

        /// <summary>
        /// Season 14's "Activate (N)" abilities used on minions already on the board. A separate
        /// action from playing the minion, costing its own gold, and one nothing else counts —
        /// seven of them in a single 8-turn match, so leaving them out understates a busy player.
        /// </summary>
        public int MinionActivations { get; set; }

        // ── board peaks ─────────────────────────────────────────────────────────────────────────
        public int HighestAttack { get; set; }
        public int HighestHealth { get; set; }
        /// <summary>Attack+health of the single biggest minion — never the max attack of one glued to the max health of another.</summary>
        public int HighestMinionAttack { get; set; }
        public int HighestMinionHealth { get; set; }
        public string HighestMinionCardId { get; set; }

        // ── combat ──────────────────────────────────────────────────────────────────────────────
        public int HeroDamageDealt { get; set; }
        public int MaxHeroDamageDealt { get; set; }
        public int HeroDamageTaken { get; set; }
        public int MaxHeroDamageTaken { get; set; }
        public int CombatWins { get; set; }
        public int CombatLosses { get; set; }
        public int CombatDraws { get; set; }

        /// <summary>One entry per shop turn — the series is what makes the numbers analysable rather than just true.</summary>
        public List<TurnStat> Turns { get; set; } = new List<TurnStat>();

        [JsonIgnore] public int HighestTurn => Turns != null && Turns.Count > 0 ? Turns.Max(t => t.Turn) : 0;

        /// <summary>Total gold left unspent across the match — the cheapest mistake to see and to fix.</summary>
        [JsonIgnore] public int GoldWasted => Turns != null ? Turns.Sum(t => Math.Max(0, t.GoldLeftover)) : 0;

        [JsonIgnore] public int ActionCount => Turns != null ? Turns.Sum(t => t.Actions) : 0;

        /// <summary>
        /// APA — actions per ACTIVE minute, deliberately not called APM: "per minute" promises a
        /// clock reading, and a three-press turn scoring 96 against a 46-press turn's 39 broke that
        /// promise for the user. Two exclusions, one reason: time in which nothing
        /// could be done, or nothing was being done, says nothing about speed. Combat is out
        /// because no action is possible during it — counting it would punish long fights and
        /// flatter a slow player whose combats happened to be short. Idle shop time is out because
        /// sitting on spent gold is not slow play, it is no play; <see cref="ActiveSeconds"/> is
        /// the model.
        /// </summary>
        [JsonIgnore] public double ApaAverage => ActiveSeconds >= 1 ? ActionCount / (ActiveSeconds / 60.0) : 0;

        /// <summary>Total time spent in shops, idle included — the wall-clock half of the "active X of Y" readout.</summary>
        [JsonIgnore] public double ShopSeconds => Turns != null ? Turns.Sum(t => t.ShopSeconds) : 0;

        /// <summary>
        /// A shop turn has three phases: reading the shop in, playing, and sitting there once the
        /// playing is done. Only the middle one measures speed, so active time runs from the first
        /// press to the last, and a silent stretch between two presses counts for at most
        /// <see cref="IdleCapSeconds"/> — a mid-turn AFK cannot pass for deliberation. Derived
        /// from the stored per-action timeline, so every record ever written recomputes under it.
        /// </summary>
        [JsonIgnore] public double ActiveSeconds => Turns != null ? Turns.Sum(t => t.ActiveSeconds) : 0;

        /// <summary>
        /// The most a pause between two presses can count for. Ten seconds is thinking that
        /// belongs to playing — reading a discover, weighing a roll; anything longer is time away
        /// from the turn, and only its first ten seconds stay on the clock.
        /// </summary>
        public const double IdleCapSeconds = 10;

        /// <summary>
        /// Best SUSTAINED turn — how fast a whole shop was played. The floor is 15s of ACTIVE time,
        /// and it was measured, not guessed: at the old 5s a real match's turn 4 — seven presses in
        /// 8.1 active seconds — scored 52 and took the tile from turn 15's 40.7 sustained across 91,
        /// which is the short-flurry mistake this figure exists to avoid. Active time is far denser
        /// than shop time, so the noise floor has to grow with it.
        /// </summary>
        [JsonIgnore] public double ApaPeakTurn => PeakTurn().Value;

        /// <summary>Which turn <see cref="ApaPeakTurn"/> belongs to, or 0 if there is no usable turn.</summary>
        [JsonIgnore] public int ApaPeakTurnNumber => PeakTurn().Key;

        private KeyValuePair<int, double> PeakTurn()
        {
            int bestTurn = 0;
            double best = 0;
            if (Turns != null)
            {
                foreach (var t in Turns)
                {
                    if (t.ActiveSeconds < 15) continue;
                    double apa = t.Apa;
                    if (apa > best) { best = apa; bestTurn = t.Turn; }
                }
            }
            return new KeyValuePair<int, double>(bestTurn, best);
        }

        /// <summary>
        /// Best BURST — the most actions inside any four seconds of the match. Four seconds is
        /// Firestone's window, so the window is comparable to their in-game widget's; theirs is
        /// live-only and resets every turn, so a match-level version does not exist anywhere else.
        /// Reported as the COUNT, not an APM: "7 actions in 4s" is a thing that happened, while
        /// the same window written as "105 APM" describes a minute that never took place.
        ///
        /// NOT the headline number, and that was measured. In a real match whose turn 8 carried 34
        /// of the player's 61 actions, the four-second peak picked turn FIVE instead — five
        /// ordinary drags that happened to land close together beat the flurry the player actually
        /// remembers. A window that short measures how fast two hands can move once, not how fast
        /// a turn was played. <see cref="ApaPeakTurn"/> is the one that agrees with the player.
        /// </summary>
        [JsonIgnore]
        public int PeakBurstActions => BurstActions(4.0);

        public int BurstActions(double windowSeconds)
        {
            if (Turns == null || windowSeconds <= 0) return 0;
            int best = 0;
            foreach (var t in Turns)
            {
                var times = t.ActionTimes;
                if (times == null || times.Count == 0) continue;
                // Every window that starts at an action: the busiest window always does.
                for (int i = 0; i < times.Count; i++)
                {
                    int count = 0;
                    double until = times[i] + windowSeconds * 1000.0;
                    for (int j = i; j < times.Count && times[j] <= until; j++) count++;
                    if (count > best) best = count;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// One shop turn and the combat that followed it, finalised once. Keeping the per-turn series
    /// rather than only totals is what allows "you stopped spending after turn 8" to be visible at
    /// all — a total can never show when something went wrong.
    /// </summary>
    internal sealed class TurnStat
    {
        public int Turn { get; set; }
        public int TavernTier { get; set; }

        public int Actions { get; set; }
        public double ShopSeconds { get; set; }

        /// <summary>
        /// When each action happened, in milliseconds from the start of this shop turn.
        ///
        /// Storing the timeline rather than only the count is what keeps every APM definition open:
        /// a rolling-window peak, a sustained per-turn rate, or whatever a later comparison needs.
        /// A busy match is a few hundred small integers, so the cost is nothing next to being locked
        /// into whichever formula happened to be written first.
        /// </summary>
        public List<int> ActionTimes { get; set; } = new List<int>();

        /// <summary>Parallel to <see cref="ActionTimes"/>: what each action was, for per-turn breakdowns.</summary>
        public List<string> ActionKinds { get; set; } = new List<string>();

        public int GoldSpent { get; set; }
        /// <summary>Gold still in hand when the shop closed.</summary>
        public int GoldLeftover { get; set; }

        public int MinionsBought { get; set; }
        public int MinionsSold { get; set; }
        public int Rolls { get; set; }

        public int BoardSize { get; set; }

        public int HeroHpStart { get; set; }
        public int HeroHpEnd { get; set; }

        /// <summary>"win" / "loss" / "draw", or null while the combat has not resolved.</summary>
        public string CombatResult { get; set; }
        public int DamageDealt { get; set; }
        public int DamageTaken { get; set; }

        // ── the three per-shop snapshots ────────────────────────────────────────────────────────
        // Null on every record written before they existed, and on any moment the tracker could not
        // catch — absence means "not captured", never "empty board". NullValueHandling keeps old-style
        // turns from growing three "null" lines each.

        /// <summary>A — the board as the shop opened, taken once the post-combat state has settled.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ShopSnap SnapStart { get; set; }

        /// <summary>
        /// B — the board when the player's actionable time ended, BEFORE end-of-turn triggers.
        /// This is the last rolling sample taken while MAIN_END had not yet appeared in the log:
        /// the trigger window resolves in the log in 0–150ms (measured), so no read taken after
        /// detecting MAIN_END can ever be pre-trigger.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ShopSnap SnapEnd { get; set; }

        /// <summary>C — after end-of-turn triggers, before combat. B→C differs by exactly those triggers.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ShopSnap SnapPreCombat { get; set; }

        /// <summary>
        /// In-play time: first press to last, each silent gap counted at no more than
        /// <see cref="MatchStats.IdleCapSeconds"/>. Zero for a turn with fewer than two presses —
        /// one instant is not a span. The max() guards the rare out-of-order fallback timestamp.
        /// </summary>
        [JsonIgnore]
        public double ActiveSeconds
        {
            get
            {
                var times = ActionTimes;
                if (times == null || times.Count < 2) return 0;
                double ms = 0;
                for (int i = 1; i < times.Count; i++)
                    ms += Math.Min(MatchStats.IdleCapSeconds * 1000.0, Math.Max(0, times[i] - times[i - 1]));
                return ms / 1000.0;
            }
        }

        /// <summary>Actions per ACTIVE minute. See <see cref="MatchStats.ApaAverage"/> for why it is not called APM.</summary>
        [JsonIgnore]
        public double Apa => ActiveSeconds >= 1 ? Actions / (ActiveSeconds / 60.0) : 0;

        /// <summary>Actions per minute of the whole shop phase, idle included — the literal reading of "APM".</summary>
        [JsonIgnore]
        public double ShopApm => ShopSeconds >= 1 ? Actions / (ShopSeconds / 60.0) : 0;
    }

    /// <summary>
    /// The player's situation at one moment of a shop turn. Board minions are stored as
    /// <see cref="MinionRecord"/>s trimmed to the snapshot tag whitelist (identity, stats, tier,
    /// keywords) rather than the full ~36-tag dictionary the final board keeps — three of these per
    /// turn with full tags would triple the record for tags no diff or render ever reads.
    /// </summary>
    internal sealed class ShopSnap
    {
        /// <summary>Milliseconds from the start of the shop turn, on the same clock as ActionTimes.</summary>
        public int AtMs { get; set; }

        public int Gold { get; set; }
        public int TavernTier { get; set; }
        public int HeroHp { get; set; }

        /// <summary>
        /// In board order. ENTITY_ID inside each record is the identity key for diffing WITHIN a turn.
        /// Across a combat the game re-creates every board minion under a new id (measured: 0 of N
        /// ids survive from one turn's C to the next turn's A, every round); the new entity's
        /// COPIED_FROM_ENTITY_ID points at the previous round's id, and that chain is the
        /// cross-turn key.
        /// </summary>
        public List<MinionRecord> Board { get; set; }

        // ── added after the first live-verified round; null on records written before, and on any
        // moment they could not be read. Same contract as the snapshots themselves: absence means
        // "not captured", never "empty". ──

        /// <summary>Cards in hand, hand order — minions and spells alike (CARDTYPE tells them apart).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<MinionRecord> Hand { get; set; }

        /// <summary>
        /// The tavern's offer row, with the buffed stats the shop shows. Null at C — the tavern is
        /// torn down at the recruit→combat edge and the same read there returns the opponent's
        /// warband — and null in duos, where the teammate's board passes the read's filter and the
        /// tavern's own controller id has not been verified.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<MinionRecord> Shop { get; set; }

        /// <summary>
        /// The trinkets held at this moment, lesser first (entity order). Empty = captured and none
        /// held, which is the truth for every turn before the first one is offered; null = not
        /// captured. FULL tag dictionaries, unlike the board: there are at most two, and what makes
        /// a per-turn trinket record worth keeping (script data, charges) is outside the board
        /// whitelist.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<MinionRecord> Trinkets { get; set; }

        /// <summary>
        /// The hero power at this moment, full tags — so EXHAUSTED says whether it had been used by
        /// then, and the script numbers carry the ones that count up. Resolved by
        /// <c>FinalBoardCapture.ResolveHeroPower</c>, the same rule the end-of-match record uses, so
        /// the header and the turns can never name different hero powers. Null = not resolved.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public MinionRecord HeroPower { get; set; }

        /// <summary>
        /// Every enchantment attached to a board minion, a card in hand, the hero or the player
        /// entity, identical ones collapsed into a count. Raw on purpose: the shop's own markers and
        /// [DNT] bookkeeping are in here too, because filtering at capture would throw away what a
        /// later reader might want, and the viewer can drop them for free.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<EnchantRecord> Enchants { get; set; }
    }

    /// <summary>
    /// One kind of enchantment on one host. Host is the ENTITY_ID it is attached to (a board or hand
    /// minion, the hero, or the player entity for player-level buffs); Source is the card id of the
    /// entity that created it, resolved at capture because that entity may be long sold by the time
    /// anyone reads this; N1/N2 are the two script numbers the card text's {0}/{1} stand for, so
    /// "+{0}/+{1}" can be rendered later without the entity.
    /// </summary>
    internal sealed class EnchantRecord
    {
        public int Host { get; set; }
        public string CardId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }
        public int Count { get; set; }
        public int N1 { get; set; }
        public int N2 { get; set; }
    }
}
