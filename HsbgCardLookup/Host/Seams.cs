using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HsbgCardLookup.Host
{
    // The four things the toolkit takes from whatever hosts it. Features talk only to these; each host
    // (today: HDT, under Host\Hdt\) implements them. Shaped after the calls the features actually make,
    // nothing more. See docs/standalone/standalone-plan.md.

    /// <summary>One game entity as the host tracks it.</summary>
    public interface IGameEntity
    {
        int Id { get; }
        string CardId { get; }
        /// <summary>The host's own name for the card, used only as a last-resort label.</summary>
        string CardName { get; }
        bool HasTag(int tag);
        int GetTag(int tag);
        bool IsInPlay { get; }
        /// <summary>HEALTH minus DAMAGE.</summary>
        int Health { get; }
    }

    /// <summary>The current match. Getters may throw while the host swaps state; callers guard.</summary>
    public interface IGameState
    {
        bool IsBattlegroundsMatch { get; }
        bool IsBattlegroundsDuosMatch { get; }
        bool IsBattlegroundsCombatPhase { get; }
        int TurnNumber { get; }
        /// <summary>"US", "EU", "AP", "CN", or null when unknown.</summary>
        string Region { get; }
        /// <summary>A snapshot of every entity; empty (never null) when there is no game.</summary>
        IList<IGameEntity> Entities();
        /// <summary>Null when absent.</summary>
        IGameEntity Entity(int id);
        IGameEntity PlayerEntity { get; }
        /// <summary>The local player's PLAYER_ID, 0 when unknown.</summary>
        int PlayerId { get; }
        /// <summary>The current opponent's PLAYER_ID, 0 when unknown.</summary>
        int OpponentId { get; }
        IList<IGameEntity> PlayerMinions();
        IList<IGameEntity> PlayerTrinkets();
        /// <summary>Raised for a new match and for a mid-match reconnect alike.</summary>
        event Action GameStarted;
        event Action GameEnded;
    }

    public sealed class LobbyPlayer
    {
        public string Name;          // display name, no #number
        public string HeroCardId;
    }

    /// <summary>The per-tile minion-type counts walk, with its own per-match log throttles.</summary>
    public interface IRaceReader
    {
        void ResetMatch();
        /// <summary>Hero entity id → (race id → count); null when the walk failed.</summary>
        Dictionary<int, Dictionary<int, int>> Read();
    }

    /// <summary>What only the Hearthstone client's memory holds.</summary>
    public interface IClientMemory
    {
        /// <summary>Null when unavailable.</summary>
        IList<LobbyPlayer> LobbyPlayers();
        /// <summary>This lobby's TAG_RACE ids; null when unavailable.</summary>
        IList<int> LobbyTribes();
        IRaceReader CreateRaceReader(Action<string> log);
    }

    /// <summary>The surface in-match features draw on, kept over the Hearthstone window.</summary>
    public interface IOverlaySurface
    {
        /// <summary>Null when there is no surface (yet).</summary>
        Canvas Canvas { get; }
        Dispatcher Dispatcher { get; }
        /// <summary>Lets <paramref name="element"/> take real mouse input while the rest stays click-through.</summary>
        void SetHitTestVisible(UIElement element);
        /// <summary>The Hearthstone window in device-independent pixels; null when there is none.</summary>
        Rect? HearthstoneRect();
    }

    /// <summary>Process-level services of the host.</summary>
    public interface IHostShell
    {
        bool IsHearthstoneRunning { get; }
        void BringHearthstoneToForeground();
        /// <summary>The host's own UI language as a locale code, or null — what <c>Language = ""</c> follows.</summary>
        string HostLanguage();
        /// <summary>Whether the toolkit starts with Windows; null when the host has no such switch (HDT
        /// starts the plugin itself, and has its own "start with Windows").</summary>
        bool? StartWithWindows { get; set; }
        /// <summary>Whether the host can open a stream view window for OBS (the standalone can; under HDT the
        /// toolkit draws on HDT's own overlay window).</summary>
        bool HasStreamView { get; }
    }

    /// <summary>The services of the current host, set once by it at start-up.</summary>
    public static class HostServices
    {
        public static IGameState Game { get; private set; }
        public static IClientMemory Memory { get; private set; }
        public static IOverlaySurface Overlay { get; private set; }
        public static IHostShell Shell { get; private set; }

        public static void Use(IGameState game, IClientMemory memory, IOverlaySurface overlay, IHostShell shell)
        {
            Game = game; Memory = memory; Overlay = overlay; Shell = shell;
        }
    }

    /// <summary>GAME_TAG ids the features read — the game protocol's own numbers.</summary>
    public static class Tags
    {
        public const int TAG_SCRIPT_DATA_NUM_2 = 3;
        public const int PLAYER_ID = 30;
        public const int EXHAUSTED = 43;
        public const int HEALTH = 45;
        public const int NEXT_OPPONENT_PLAYER_ID = 1360;
        public const int PLAYER_LEADERBOARD_PLACE = 1373;
        public const int PLAYER_TECH_LEVEL = 1377;
        public const int TAG_SCRIPT_DATA_NUM_3 = 2889;
        public const int TAG_SCRIPT_DATA_NUM_4 = 2919;
        public const int BACON_DUO_TEAMMATE_PLAYER_ID = 2939;
        public const int BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT = 2975;
        public const int NEXT_OPPONENT_TEAMMATE_PLAYER_ID = 2988;
        public const int LOCK_VISUAL = 4414;
    }
}
