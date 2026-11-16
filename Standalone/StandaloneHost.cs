using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HsbgCardLookup.Host;

namespace Toolkit.Standalone
{
    // The four host seams over our own memory reader and our own overlay window. The game state is
    // the latest Snapshot the reader thread published; every call reads that one frozen snapshot.

    internal sealed class SnapEntity : IGameEntity
    {
        private readonly EntitySnap _e;
        public SnapEntity(EntitySnap e) { _e = e; }
        public int Id => _e.Id;
        public string CardId => _e.CardId;
        public string CardName => null;               // the features name cards from our own store
        public bool HasTag(int tag) => _e.Tags.ContainsKey(tag);
        public int GetTag(int tag) { int v; return _e.Tags.TryGetValue(tag, out v) ? v : 0; }
        public bool IsInPlay => GetTag(49) == 1;      // ZONE == PLAY
        public int Health => GetTag(45) - GetTag(44); // HEALTH - DAMAGE
    }

    internal sealed class StandaloneGameState : IGameState
    {
        private const int TagController = 50, TagCardType = 202, TagTrinket = 3407, TagTurn = 20, TagState = 204, TagPlayerId = 30;
        private const int CardTypeMinion = 4, StateComplete = 3;

        private volatile Snapshot _snap;
        private long _gameAddr;
        private bool _endedRaised;

        public event Action GameStarted;
        public event Action GameEnded;

        /// <summary>UI thread: adopt a new snapshot and raise start/end edges before the features poll.</summary>
        public void Publish(Snapshot s)
        {
            _snap = s;
            long addr = s?.GameStateAddr ?? 0;
            if (addr != 0 && addr != _gameAddr)
            {
                _gameAddr = addr; _endedRaised = false;
                GameStarted?.Invoke();                     // a new GameState object: a new match or a rejoin
            }
            bool complete = addr != 0 && GameTag(TagState) == StateComplete;
            if (!_endedRaised && _gameAddr != 0 && (addr == 0 || complete))
            {
                _endedRaised = true;
                GameEnded?.Invoke();
            }
            if (addr == 0) _gameAddr = 0;
        }

        private Snapshot S => _snap;

        private int GameTag(int tag)
        {
            var s = S; EntitySnap g; int v;
            return s != null && s.Entities.TryGetValue(s.GameEntityId, out g) && g.Tags.TryGetValue(tag, out v) ? v : 0;
        }

        // A Battlegrounds board is up once the client's own leaderboard has tiles; duos when its teams
        // hold two tiles each.
        public bool IsBattlegroundsMatch { get { var s = S; return s != null && s.GameStateAddr != 0 && s.Tiles.Count > 0; } }
        public bool IsBattlegroundsDuosMatch { get { var s = S; return IsBattlegroundsMatch && s.TeamsWithTwo > 0; } }
        // The game entity's TURN counts shop and combat alike: odd = recruit, even = combat.
        public bool IsBattlegroundsCombatPhase { get { int t = GameTag(TagTurn); return IsBattlegroundsMatch && t > 0 && t % 2 == 0; } }
        public int TurnNumber => (GameTag(TagTurn) + 1) / 2;
        public string Region => S?.Region;

        public IList<IGameEntity> Entities()
        {
            var s = S;
            return s == null ? new List<IGameEntity>() : s.Entities.Values.Select(e => (IGameEntity)new SnapEntity(e)).ToList();
        }

        public IGameEntity Entity(int id)
        {
            var s = S; EntitySnap e;
            return s != null && s.Entities.TryGetValue(id, out e) ? new SnapEntity(e) : null;
        }

        public IGameEntity PlayerEntity => Entity(S?.LocalPlayerEntityId ?? 0);
        public int PlayerId => PlayerEntity?.GetTag(TagPlayerId) ?? 0;
        public int OpponentId => Entity(S?.OpponentPlayerEntityId ?? 0)?.GetTag(TagPlayerId) ?? 0;

        // The local player's board: their entities in play; minions by card type, trinkets by tag.
        private IList<IGameEntity> Board(Func<SnapEntity, bool> pick)
        {
            int me = PlayerId;
            var s = S;
            if (s == null || me == 0) return new List<IGameEntity>();
            return s.Entities.Values.Select(e => new SnapEntity(e))
                .Where(e => e.GetTag(TagController) == me && e.IsInPlay && pick(e))
                .Cast<IGameEntity>().ToList();
        }

        public IList<IGameEntity> PlayerMinions() => Board(e => e.GetTag(TagCardType) == CardTypeMinion);
        public IList<IGameEntity> PlayerTrinkets() => Board(e => e.HasTag(TagTrinket));

        public Snapshot Current => S;
    }

    internal sealed class StandaloneMemory : IClientMemory
    {
        private readonly StandaloneGameState _game;
        public StandaloneMemory(StandaloneGameState game) { _game = game; }

        public IList<LobbyPlayer> LobbyPlayers()
        {
            var s = _game.Current;
            if (s == null || s.GameStateAddr == 0) return null;
            return s.Lobby.Select(kv => new LobbyPlayer { Name = kv.Key, HeroCardId = kv.Value }).ToList();
        }

        public IList<int> LobbyTribes()
        {
            var s = _game.Current;
            return s == null || s.GameStateAddr == 0 ? null : new List<int>(s.LobbyTribes);
        }

        public IRaceReader CreateRaceReader(Action<string> log) => new Races(_game);

        private sealed class Races : IRaceReader
        {
            private readonly StandaloneGameState _game;
            public Races(StandaloneGameState game) { _game = game; }
            public void ResetMatch() { }
            public Dictionary<int, Dictionary<int, int>> Read()
            {
                var s = _game.Current;
                if (s == null) return null;
                var d = new Dictionary<int, Dictionary<int, int>>();
                foreach (var t in s.Tiles) if (t.HeroEntityId > 0) d[t.HeroEntityId] = new Dictionary<int, int>(t.Races);
                return d;
            }
        }
    }

    internal sealed class StandaloneOverlay : IOverlaySurface
    {
        private readonly OverlayWindow _window;
        public StandaloneOverlay(OverlayWindow window) { _window = window; }
        public Canvas Canvas => _window.Surface;
        public Dispatcher Dispatcher => _window.Dispatcher;
        public void SetHitTestVisible(UIElement element) { }   // the window is click-through as a whole for now
        public Rect? HearthstoneRect()
        {
            int x, y, w, h; bool fg;
            if (!HsWindow.ClientArea(out x, out y, out w, out h, out fg)) return null;
            var src = PresentationSource.FromVisual(_window);
            var m = src?.CompositionTarget?.TransformFromDevice;
            if (m == null) return new Rect(x, y, w, h);
            var tl = m.Value.Transform(new Point(x, y)); var br = m.Value.Transform(new Point(x + w, y + h));
            return new Rect(tl, br);
        }
    }

    internal sealed class StandaloneShell : IHostShell
    {
        [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr h);
        public bool IsHearthstoneRunning => HsWindow.Find() != IntPtr.Zero;
        public void BringHearthstoneToForeground() { var h = HsWindow.Find(); if (h != IntPtr.Zero) SetForegroundWindow(h); }
        public string HostLanguage() => null;   // no host language: Language = "" falls back to the default
    }
}
