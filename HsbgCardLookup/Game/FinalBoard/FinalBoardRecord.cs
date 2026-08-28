using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// One finished Battlegrounds match, as we keep it on disk.
    ///
    /// Deliberately a plain POCO with no HDT types in it: the same record has to survive being
    /// written by the live capture, read back months later, rendered with no match running, and
    /// exported to a PNG. Anything that needs <c>Hearthstone_Deck_Tracker</c> to interpret it
    /// belongs in <see cref="FinalBoardStore"/> or the renderer, not here.
    ///
    /// TWO TIERS, and every consumer must handle both (see final-board-plan.md §3):
    ///   • tier A — imported from HDT's own <c>BattlegroundsLastGames</c>, so it exists for every
    ///     game of the last 7 days INCLUDING ones played before this plugin was installed. Board,
    ///     hero, placement, rating.
    ///   • tier B — captured by us while the match ended, so it only exists for matches we watched:
    ///     hero power, trinkets, anomaly. <see cref="HasOurCapture"/> is the test.
    /// A tier-A record is not a broken tier-B record; it is the normal case for anything historic.
    /// </summary>
    internal sealed class FinalBoardRecord
    {
        /// <summary>Bumped only for a change old readers cannot cope with. New optional fields don't count.</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>HDT's match start timestamp — its own key for a game, so imports dedupe against live captures for free.</summary>
        public string GameId { get; set; }

        /// <summary>"hdt" (imported) or "live" (we watched the match end). See <see cref="HasOurCapture"/>.</summary>
        public string Source { get; set; }

        public string StartedAt { get; set; }
        public string EndedAt { get; set; }

        public bool Duos { get; set; }
        public bool FriendlyGame { get; set; }

        /// <summary>
        /// The hero as a card id. Note this must NOT come from <c>GameStats.PlayerHeroCardId</c>,
        /// which is a placeholder (`TB_BaconShop_HERO_PH`) — live-verified 2026-08-25. It comes from
        /// the hero entity, or from HDT's store, both of which carry the real id.
        /// </summary>
        public string HeroCardId { get; set; }

        public string HeroName { get; set; }
        public string PlayerName { get; set; }

        public int Placement { get; set; }
        public int Rating { get; set; }
        public int? RatingAfter { get; set; }

        /// <summary>A season reset makes RatingAfter an absolute value rather than a step; HDT flags this the same way.</summary>
        public bool SeasonReset { get; set; }

        public int Turns { get; set; }

        /// <summary>The final warband, in board order. Card id + full tag dictionary, which is all HDT needs to re-render it.</summary>
        public List<MinionRecord> Board { get; set; }

        // ── tier B: only present when we watched the match end ──────────────────────────────────
        /// <summary>What the player DID — null for any match we did not watch, including every import.</summary>
        public MatchStats Stats { get; set; }

        public string HeroPowerCardId { get; set; }
        public List<MinionRecord> Trinkets { get; set; }
        public int AnomalyDbfId { get; set; }
        public string AnomalyCardId { get; set; }

        /// <summary>
        /// The lobby ran Dark Gifts. It is not an anomaly in the tag sense — there is no
        /// BACON_GLOBAL_ANOMALY_DBID for it — it is the presence of the BG36_Button_DarkGift entity,
        /// which is how <c>DarkGiftWatcher</c> finds it too. Stored because it is a fact about the
        /// match that cannot be recovered afterwards, and because the trinket medallions are drawn
        /// in that season's frame. False on every record captured before this field existed, and on
        /// every import: absence here means "not known", not "no".
        /// </summary>
        public bool DarkGiftLobby { get; set; }

        [JsonIgnore]
        public bool HasOurCapture => string.Equals(Source, "live", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rating change, or null when HDT never resolved one (an abandoned game, or a read taken
        /// before it arrived — it lands ~400ms after the match ends). A season reset reports the new
        /// rating rather than a difference, matching how HDT itself renders it.
        /// </summary>
        [JsonIgnore]
        public int? MmrDelta
        {
            get
            {
                if (!RatingAfter.HasValue) return null;
                return SeasonReset ? RatingAfter.Value : RatingAfter.Value - Rating;
            }
        }

        [JsonIgnore]
        public DateTime? StartedAtUtc
        {
            get
            {
                DateTime parsed;
                if (DateTime.TryParse(StartedAt, out parsed)) return parsed.ToUniversalTime();
                return null;
            }
        }
    }

    /// <summary>
    /// A minion (or trinket) as card id + its GameTag dictionary, keyed by the tag's integer value.
    /// Ints rather than the enum on purpose: a future Hearthstone patch can introduce tags this
    /// build has no name for, and a record written today must still round-trip through a build that
    /// does know them.
    /// </summary>
    internal sealed class MinionRecord
    {
        public string CardId { get; set; }
        public Dictionary<int, int> Tags { get; set; }
    }
}
