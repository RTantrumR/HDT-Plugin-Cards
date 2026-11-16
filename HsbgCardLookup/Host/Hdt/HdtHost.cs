using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using HdtEntity = Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity;

namespace HsbgCardLookup.Host.Hdt
{
    /// <summary>Installs the HDT implementations of the four seams.</summary>
    internal static class HdtHost
    {
        public static void Install()
        {
            HostServices.Use(new HdtGameState(), new HdtClientMemory(), new HdtOverlay(), new HdtShell());
        }
    }

    internal sealed class HdtGameEntity : IGameEntity
    {
        // One wrapper per HDT entity for as long as HDT keeps it, so a poll over every entity doesn't
        // allocate a fresh object for each one.
        private static readonly ConditionalWeakTable<HdtEntity, HdtGameEntity> Cache = new ConditionalWeakTable<HdtEntity, HdtGameEntity>();
        private readonly HdtEntity _e;

        private HdtGameEntity(HdtEntity e) { _e = e; }

        public static IGameEntity Of(HdtEntity e) => e == null ? null : Cache.GetValue(e, x => new HdtGameEntity(x));

        public int Id => _e.Id;
        public string CardId => _e.CardId;
        public string CardName => _e.Card?.Name;
        public bool HasTag(int tag) => _e.HasTag((GameTag)tag);
        public int GetTag(int tag) => _e.GetTag((GameTag)tag);
        public bool IsInPlay => _e.IsInPlay;
        public int Health => _e.Health;
    }

    internal sealed class HdtGameState : IGameState
    {
        // HDT's ActionList has Add but no Remove, so it is hooked once per process and fans out through
        // these statics; a plugin reload creates a new HdtGameState but never stacks handlers.
        private static bool _hooked;
        private static event Action StartedStatic, EndedStatic;

        public HdtGameState()
        {
            if (_hooked) return;
            _hooked = true;
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(new Action(() => StartedStatic?.Invoke())); } catch { }
            try { Hearthstone_Deck_Tracker.API.GameEvents.OnGameEnd.Add(new Action(() => EndedStatic?.Invoke())); } catch { }
        }

        public event Action GameStarted { add { StartedStatic += value; } remove { StartedStatic -= value; } }
        public event Action GameEnded { add { EndedStatic += value; } remove { EndedStatic -= value; } }

        private static Hearthstone_Deck_Tracker.Hearthstone.GameV2 G => Core.Game;

        public bool IsBattlegroundsMatch { get { var g = G; return g != null && g.IsBattlegroundsMatch; } }
        public bool IsBattlegroundsDuosMatch { get { var g = G; return g != null && g.IsBattlegroundsDuosMatch; } }
        public bool IsBattlegroundsCombatPhase { get { var g = G; return g != null && g.IsBattlegroundsCombatPhase; } }
        public int TurnNumber => G?.GetTurnNumber() ?? 0;

        public string Region
        {
            get
            {
                switch (G?.CurrentRegion ?? Hearthstone_Deck_Tracker.Enums.Region.UNKNOWN)
                {
                    case Hearthstone_Deck_Tracker.Enums.Region.US: return "US";
                    case Hearthstone_Deck_Tracker.Enums.Region.EU: return "EU";
                    case Hearthstone_Deck_Tracker.Enums.Region.ASIA: return "AP";
                    case Hearthstone_Deck_Tracker.Enums.Region.CHINA: return "CN";
                    default: return null;
                }
            }
        }

        // HDT mutates its collections on its own threads: copy defensively, an empty list on failure.
        private static IList<IGameEntity> Wrap(IEnumerable<HdtEntity> src)
        {
            try { return src == null ? new List<IGameEntity>() : src.ToList().Select(HdtGameEntity.Of).ToList(); }
            catch { return new List<IGameEntity>(); }
        }

        public IList<IGameEntity> Entities() => Wrap(G?.Entities?.Values);

        public IGameEntity Entity(int id)
        {
            var ents = G?.Entities;
            if (ents == null) return null;
            HdtEntity e;
            try { ents.TryGetValue(id, out e); } catch { e = null; }
            return HdtGameEntity.Of(e);
        }

        public IGameEntity PlayerEntity => HdtGameEntity.Of(G?.PlayerEntity);
        public int PlayerId => G?.Player?.Id ?? 0;
        public int OpponentId => G?.Opponent?.Id ?? 0;
        public IList<IGameEntity> PlayerMinions() => Wrap(G?.Player?.Minions);
        public IList<IGameEntity> PlayerTrinkets() => Wrap(G?.Player?.Trinkets);
    }

    internal sealed class HdtClientMemory : IClientMemory
    {
        public IList<LobbyPlayer> LobbyPlayers()
        {
            var lobby = HearthMirror.Reflection.Client?.GetBattlegroundsLobbyInfo();
            if (lobby?.Players == null) return null;
            var list = new List<LobbyPlayer>();
            foreach (var p in lobby.Players)
                if (p != null) list.Add(new LobbyPlayer { Name = p.Name, HeroCardId = p.HeroCardId });
            return list;
        }

        public IList<int> LobbyTribes() => HearthMirror.Reflection.Client?.GetAvailableBattlegroundsRaces();

        public IRaceReader CreateRaceReader(Action<string> log) => new LeaderboardRaceScry(log);
    }

    internal sealed class HdtOverlay : IOverlaySurface
    {
        public Canvas Canvas => Hearthstone_Deck_Tracker.API.Core.OverlayCanvas;
        public Dispatcher Dispatcher => Hearthstone_Deck_Tracker.API.Core.OverlayCanvas?.Dispatcher;
        public void SetHitTestVisible(UIElement element) => OverlayExtensions.SetIsOverlayHitTestVisible(element, true);
        public Rect? HearthstoneRect()
        {
            var r = User32.GetHearthstoneRect(true);   // true = DIPs (a System.Drawing.Rectangle)
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }
    }

    internal sealed class HdtShell : IHostShell
    {
        public bool IsHearthstoneRunning => User32.GetHearthstoneWindow() != IntPtr.Zero;
        public void BringHearthstoneToForeground() => User32.BringHsToForeground();
        public string HostLanguage() => HdtLanguage.Detect();
    }
}
