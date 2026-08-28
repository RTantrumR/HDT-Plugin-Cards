using System;
using System.Collections.Generic;

namespace HsbgCardLookup.Config
{
    /// <summary>
    /// Which parts of a stored match the Final Board panel draws.
    ///
    /// Display only. The capture side never consults this — a match is always recorded whole, so
    /// switching a block back on shows history that was already there rather than starting a new
    /// collection from today. That split is the whole reason these live here and not on the tracker.
    ///
    /// The switches are per BLOCK. <see cref="Blocks"/> is the one place they are declared: a new
    /// switch is one property plus one row there, and the settings page, being generated from that
    /// list, grows with it. Finer per-item switches (a single headline tile, one table column) are
    /// the intended next step and fit the same shape — add the property, add the row, and give it a
    /// <see cref="Block.Parent"/>.
    /// </summary>
    public class FinalBoardOptions
    {
        // ── shared by both views ────────────────────────────────────────────────────────────────
        public bool HeroPortrait { get; set; } = true;
        public bool MmrDelta { get; set; } = true;
        public bool MatchMeta { get; set; } = true;
        public bool PlayerName { get; set; } = true;

        // ── the Stats view ──────────────────────────────────────────────────────────────────────
        public bool HeadlineTiles { get; set; } = true;
        public bool Counters { get; set; } = true;
        public bool TurnTable { get; set; } = true;

        // ── the Board view ──────────────────────────────────────────────────────────────────────
        public bool DetailRow { get; set; } = true;
        public bool Board { get; set; } = true;

        /// <summary>True when the Stats tab would render something.</summary>
        public bool AnyStats => HeadlineTiles || Counters || TurnTable;

        /// <summary>True when the Board tab would render something.</summary>
        public bool AnyBoard => DetailRow || Board;

        public sealed class Block
        {
            public string Label;
            public string Desc;
            /// <summary>Heading the settings page files this row under.</summary>
            public string Group;
            /// <summary>Reserved for per-item switches: the block this one refines, or null.</summary>
            public string Parent;
            public Func<FinalBoardOptions, bool> Get;
            public Action<FinalBoardOptions, bool> Set;
        }

        public static readonly IReadOnlyList<Block> Blocks = new[]
        {
            new Block { Group = "Header", Label = "Hero portrait", Desc = "The hero's picture beside the placement.",
                        Get = o => o.HeroPortrait, Set = (o, v) => o.HeroPortrait = v },
            new Block { Group = "Header", Label = "Rating change", Desc = "The MMR you won or lost.",
                        Get = o => o.MmrDelta, Set = (o, v) => o.MmrDelta = v },
            new Block { Group = "Header", Label = "Match details", Desc = "Turns, length and when it was played.",
                        Get = o => o.MatchMeta, Set = (o, v) => o.MatchMeta = v },
            new Block { Group = "Header", Label = "Your name", Desc = "The BattleTag line under the panel.",
                        Get = o => o.PlayerName, Set = (o, v) => o.PlayerName = v },

            new Block { Group = "Stats", Label = "Headline numbers", Desc = "Actions, APM, fastest turn, gold left unspent.",
                        Get = o => o.HeadlineTiles, Set = (o, v) => o.HeadlineTiles = v },
            new Block { Group = "Stats", Label = "Counters", Desc = "Everything you pressed, totalled — buys, rolls, damage, biggest minion.",
                        Get = o => o.Counters, Set = (o, v) => o.Counters = v },
            new Block { Group = "Stats", Label = "Turn-by-turn table", Desc = "One row per turn: tier, health, the fight, gold and what you did.",
                        Get = o => o.TurnTable, Set = (o, v) => o.TurnTable = v },

            new Block { Group = "Board", Label = "Hero power, trinkets, anomaly", Desc = "Only ever there for matches recorded with the plugin running.",
                        Get = o => o.DetailRow, Set = (o, v) => o.DetailRow = v },
            new Block { Group = "Board", Label = "Final warband", Desc = "The minions you finished the match with.",
                        Get = o => o.Board, Set = (o, v) => o.Board = v },
        };
    }
}
