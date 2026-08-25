using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;                        // Core.Game
using Hearthstone_Deck_Tracker.Hearthstone;            // GameV2
using Hearthstone_Deck_Tracker.Hearthstone.Entities;   // Entity

namespace HsbgCardLookup.Game.FinalBoard
{
    /// <summary>
    /// Counts what the player DID during a Battlegrounds match — gold, rolls, buys, sells, plays,
    /// upgrades, combat damage — as a per-turn series.
    ///
    /// Method and semantics follow Reign-in-blood's HDT-FinalStatsPlugin (MIT) where they overlap.
    /// The load-bearing idea taken from it: the tavern's shop cards are entities that are IN PLAY but
    /// NOT controlled by the player, and a per-entity (zone, controller) snapshot diffed each tick
    /// turns that into events — a known shop entity arriving in the player's hand is a purchase.
    /// Selling and the APM series are ours; his plugin tracks neither.
    ///
    /// EVERY counter here is a heuristic over game state that Blizzard can change without warning,
    /// so each detection logs why it fired. That is not debug noise left behind: it is the only way
    /// a miscount after a patch shows up as a wrong log line instead of a wrong number a player
    /// trusts. Anything uncertain stays at zero — a missing stat beats an invented one.
    /// </summary>
    internal sealed class MatchStatsTracker
    {
        private const int PollMs = 200;   // fine enough for APM; the work per tick is a dictionary diff

        // The game's own shop buttons. Pressing one opens a POWER block sourced from these
        // pseudo-cards, which is how HearthSim's converter and Firestone detect these actions — and
        // it beats inferring them from state: a sale looks like a triple if you only watch minions
        // leave the board, and Bob reshuffling looks like a roll if you only diff the shop.
        private const string SellCardId = "TB_BaconShop_DragSell";
        private const string RerollCardId1 = "TB_BaconShop_1p_Reroll_Button";
        private const string RerollCardId8 = "TB_BaconShop_8p_Reroll_Button";
        private const string FreezeCardId = "TB_BaconShopLockAll_Button";
        private const string TripleCardId = "TB_BaconShop_Triples_01";

        private readonly Action<string> _log;

        private DateTime _lastPoll = DateTime.MinValue;
        private bool _inMatch;
        private bool? _wasCombat;

        private MatchStats _stats;
        private TurnStat _turn;
        private readonly Stopwatch _shopClock = new Stopwatch();

        // Per-entity (zone, controller) from the previous tick — the diff is what produces events.
        private readonly Dictionary<int, ZoneState> _prev = new Dictionary<int, ZoneState>();
        private readonly HashSet<int> _knownShopIds = new HashSet<int>();
        private readonly HashSet<int> _countedBuys = new HashSet<int>();
        private readonly HashSet<int> _countedPlays = new HashSet<int>();

        private int _prevResourcesUsed;
        private int _prevTechLevel;
        private int _combatDealt, _combatTaken;
        private bool _turnClosed;
        private int _logIndex;

        public MatchStatsTracker(Action<string> log) { _log = log; }

        /// <summary>The live counters, or null outside a match.</summary>
        public MatchStats Current => _stats;

        /// <summary>Called by <see cref="FinalBoardCapture"/> when it writes the record.</summary>
        public MatchStats TakeSnapshot()
        {
            CloseTurn();   // no-op if the shop turn was already closed by the combat edge
            return _stats;
        }

        public void Reset()
        {
            _stats = null;
            _turn = null;
            _wasCombat = null;
            _shopClock.Reset();
            _prev.Clear();
            _knownShopIds.Clear();
            _countedBuys.Clear();
            _countedPlays.Clear();
            _prevResourcesUsed = 0;
            _prevTechLevel = 0;
            _turnClosed = false;
            _logIndex = 0;
            _combatDealt = _combatTaken = 0;
        }

        // ── poll ────────────────────────────────────────────────────────────────────────────────
        public void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPoll).TotalMilliseconds < PollMs) return;
                _lastPoll = now;

                var g = Core.Game;
                bool isBg = false;
                try { isBg = g != null && g.IsBattlegroundsMatch; } catch { }
                if (!isBg)
                {
                    if (_inMatch) _inMatch = false;
                    return;
                }
                if (!_inMatch)
                {
                    _inMatch = true;
                    if (_stats == null) _stats = new MatchStats();
                }

                bool combat = false;
                try { combat = g.IsBattlegroundsCombatPhase; } catch { }

                if (!_wasCombat.HasValue) { _wasCombat = combat; if (!combat) OpenTurn(g); }
                else if (_wasCombat.Value != combat)
                {
                    _wasCombat = combat;
                    if (combat) CloseTurn();               // shop just closed, combat starting
                    else { ResolveCombat(g); OpenTurn(g); } // combat resolved, new shop
                }

                if (!combat) TrackShop(g);
            }
            catch (Exception ex) { Log("Poll error: " + ex.Message); }
        }

        // ── turn boundaries ─────────────────────────────────────────────────────────────────────
        private void OpenTurn(GameV2 g)
        {
            int n = 0;
            try { n = g.GetTurnNumber(); } catch { }

            _turn = new TurnStat
            {
                Turn = n,
                TavernTier = Tag(g.PlayerEntity, GameTag.PLAYER_TECH_LEVEL),
                HeroHpStart = HeroHp(g),
            };
            _stats.Turns.Add(_turn);
            _turnClosed = false;
            _shopClock.Restart();

            // RESOURCES_USED restarts each turn, so the baseline has to restart with it or the first
            // read of the new turn would be counted as a full turn's spending.
            _prevResourcesUsed = Math.Max(0, Tag(g.PlayerEntity, GameTag.RESOURCES_USED));
            _prevTechLevel = _turn.TavernTier;
        }

        private void CloseTurn()
        {
            // A match that ends during combat has already had its shop turn closed at the phase
            // edge. Closing twice would recompute the leftover gold and board size from the
            // post-combat state and quietly overwrite what the player actually finished the shop with.
            if (_turn == null || _turnClosed) return;
            _turnClosed = true;
            _turn.ShopSeconds = Math.Round(_shopClock.Elapsed.TotalSeconds, 1);
            _shopClock.Stop();
            try
            {
                var g = Core.Game;
                _turn.GoldLeftover = GoldAvailable(g);
                _turn.BoardSize = BoardSize(g);
                if (_turn.TavernTier <= 0) _turn.TavernTier = Tag(g.PlayerEntity, GameTag.PLAYER_TECH_LEVEL);
            }
            catch { }
            Log(string.Format("turn {0} closed | tier={1} actions={2} shop={3}s spent={4} leftover={5} bought={6} sold={7} rolls={8}",
                _turn.Turn, _turn.TavernTier, _turn.Actions, _turn.ShopSeconds, _turn.GoldSpent,
                _turn.GoldLeftover, _turn.MinionsBought, _turn.MinionsSold, _turn.Rolls));
            _combatDealt = _combatTaken = 0;
        }

        /// <summary>
        /// Settle the combat that just ended. Damage TAKEN comes from the hero's own health, which is
        /// the one number that cannot be argued with; damage DEALT comes from the predamage event
        /// (see <see cref="NoteHeroDamage"/>), because nothing in our own state can observe it.
        /// A combat where neither side lost health is a draw, which is exactly how it should read.
        /// </summary>
        private void ResolveCombat(GameV2 g)
        {
            var t = _stats != null ? _stats.Turns.LastOrDefault() : null;
            if (t == null) return;

            t.HeroHpEnd = HeroHp(g);
            int lost = Math.Max(0, t.HeroHpStart - t.HeroHpEnd);
            t.DamageTaken = lost > 0 ? lost : _combatTaken;
            t.DamageDealt = _combatDealt;

            if (t.DamageTaken > 0) { t.CombatResult = "loss"; _stats.CombatLosses++; }
            else if (t.DamageDealt > 0) { t.CombatResult = "win"; _stats.CombatWins++; }
            else { t.CombatResult = "draw"; _stats.CombatDraws++; }

            _stats.HeroDamageTaken += t.DamageTaken;
            _stats.HeroDamageDealt += t.DamageDealt;
            if (t.DamageTaken > _stats.MaxHeroDamageTaken) _stats.MaxHeroDamageTaken = t.DamageTaken;
            if (t.DamageDealt > _stats.MaxHeroDamageDealt) _stats.MaxHeroDamageDealt = t.DamageDealt;

            Log(string.Format("combat after turn {0} | {1} | dealt={2} taken={3} (hp {4}->{5})",
                t.Turn, t.CombatResult, t.DamageDealt, t.DamageTaken, t.HeroHpStart, t.HeroHpEnd));
        }

        /// <summary>
        /// HDT's predamage event. The filtering is the whole trick, and it is Reign's: accept a hit
        /// ONLY when the damaged entity is exactly the hero referenced by a player entity's
        /// HERO_ENTITY tag. Battlegrounds exposes several hero-shaped entities (leaderboard and
        /// combat representations) and one impact can raise PREDAMAGE more than once, so anything
        /// looser double-counts.
        /// </summary>
        public void HandlePredamage(Hearthstone_Deck_Tracker.API.PredamageInfo info)
        {
            try
            {
                if (_stats == null || info == null || info.Entity == null || info.Value <= 0) return;
                var g = Core.Game;
                if (g == null || !g.IsBattlegroundsMatch || !g.IsBattlegroundsCombatPhase) return;
                if (!info.Entity.IsHero) return;

                int mine = Tag(g.PlayerEntity, GameTag.HERO_ENTITY);
                int theirs = Tag(g.OpponentEntity, GameTag.HERO_ENTITY);
                if (theirs > 0 && info.Entity.Id == theirs) NoteHeroDamage(false, info.Value);
                else if (mine > 0 && info.Entity.Id == mine) NoteHeroDamage(true, info.Value);
            }
            catch { }
        }

        /// <summary>
        /// Only the largest hit per side per combat is kept:
        /// one impact can surface more than once, and Battlegrounds shows several hero-like entities,
        /// so summing would inflate. This mirrors Reign's rule, which exists for the same reason.
        /// </summary>
        public void NoteHeroDamage(bool onMyHero, int amount)
        {
            if (amount <= 0 || _stats == null) return;
            if (onMyHero) { if (amount > _combatTaken) _combatTaken = amount; }
            else { if (amount > _combatDealt) _combatDealt = amount; }
        }

        // ── the shop phase ──────────────────────────────────────────────────────────────────────
        private void TrackShop(GameV2 g)
        {
            if (_turn == null) OpenTurn(g);

            int playerId = -1;
            try { playerId = g.Player.Id; } catch { return; }

            // Gold: RESOURCES_USED only ever climbs within a turn. A DROP is a refund (a sale) or a
            // turn reset -- never spending -- so it just becomes the new baseline. Reign's rule.
            int used = Math.Max(0, Tag(g.PlayerEntity, GameTag.RESOURCES_USED));
            if (used > _prevResourcesUsed)
            {
                int delta = used - _prevResourcesUsed;
                _stats.GoldSpent += delta;
                _turn.GoldSpent += delta;
            }
            _prevResourcesUsed = used;

            int tech = Tag(g.PlayerEntity, GameTag.PLAYER_TECH_LEVEL);
            if (tech > _prevTechLevel && _prevTechLevel > 0)
            {
                _stats.TavernUpgrades++;
                Act();
                Log("tavern upgrade to " + tech + " on turn " + _turn.Turn);
            }
            if (tech > 0) { _prevTechLevel = tech; _turn.TavernTier = tech; }

            var entities = Snapshot(g);
            var shopIds = new HashSet<int>();
            var current = new Dictionary<int, ZoneState>();

            foreach (var e in entities)
            {
                if (e == null || e.Id <= 0) continue;
                var state = new ZoneState((Zone)Tag(e, GameTag.ZONE), Tag(e, GameTag.CONTROLLER));
                current[e.Id] = state;

                bool mine = state.Controller == playerId;
                bool inPlay = state.Zone == Zone.PLAY;

                // Bob's offerings: in play, but not ours.
                if (inPlay && !mine && (IsMinion(e) || IsTavernSpell(e)))
                {
                    shopIds.Add(e.Id);
                    _knownShopIds.Add(e.Id);
                }

                ZoneState prev;
                bool known = _prev.TryGetValue(e.Id, out prev);

                // BUY: a card we saw in the tavern is now in our hand.
                if (mine && state.Zone == Zone.HAND && _knownShopIds.Contains(e.Id) && _countedBuys.Add(e.Id))
                {
                    if (IsTavernSpell(e)) _stats.SpellsBought++;
                    else { _stats.MinionsBought++; _turn.MinionsBought++; }
                    Act();
                    Log("bought " + (e.CardId ?? "?") + " on turn " + _turn.Turn);
                }

                // PLAY: it left our hand for the board.
                if (mine && known && prev.Zone == Zone.HAND && state.Zone != Zone.HAND && _countedPlays.Add(e.Id))
                {
                    if (IsTavernSpell(e)) _stats.SpellsPlayed++;
                    else _stats.MinionsPlayed++;
                    Act();
                }

            }

            ScanPowerLog();

            TrackBoardPeaks(g, playerId);

            _prev.Clear();
            foreach (var kv in current) _prev[kv.Key] = kv.Value;
        }

        /// <summary>
        /// Count the shop-button presses by walking the new lines of HDT's Power.log.
        ///
        /// Only NEW lines are read, and the cursor resets if the log ever shrinks (a new game).
        /// Selling, rolling, freezing and tripling leave no usable trace in entity state — a sold
        /// minion and a tripled one both simply leave the board — but each opens a POWER block
        /// sourced from the button's own pseudo-card, which is unambiguous. Card ids per HearthSim's
        /// replay converter, which Firestone also relies on.
        /// </summary>
        private void ScanPowerLog()
        {
            List<string> log;
            try { log = Core.Game != null ? Core.Game.PowerLog : null; } catch { return; }
            if (log == null) return;

            int count;
            try { count = log.Count; } catch { return; }
            if (count < _logIndex) _logIndex = 0;

            for (int i = _logIndex; i < count; i++)
            {
                string line;
                try { line = log[i]; } catch { break; }
                if (string.IsNullOrEmpty(line)) continue;
                if (line.IndexOf("BLOCK_START", StringComparison.Ordinal) < 0) continue;

                if (Mentions(line, SellCardId))
                {
                    _stats.MinionsSold++;
                    if (_turn != null) _turn.MinionsSold++;
                    Act();
                    Log("sold a minion on turn " + TurnNo());
                }
                else if (Mentions(line, RerollCardId1) || Mentions(line, RerollCardId8))
                {
                    _stats.TavernRolls++;
                    if (_turn != null) _turn.Rolls++;
                    Act();
                }
                else if (Mentions(line, FreezeCardId))
                {
                    _stats.Freezes++;
                    Act();
                }
                else if (Mentions(line, TripleCardId))
                {
                    _stats.TriplesCreated++;
                }
            }
            _logIndex = count;
        }

        private static bool Mentions(string line, string cardId)
        {
            return line.IndexOf("cardId=" + cardId, StringComparison.Ordinal) >= 0;
        }

        private int TurnNo() { return _turn != null ? _turn.Turn : 0; }

        /// <summary>
        /// Biggest minion of the match. Attack and health are taken from the SAME minion — gluing the
        /// best attack seen to the best health seen would invent a creature that never existed.
        /// </summary>
        private void TrackBoardPeaks(GameV2 g, int playerId)
        {
            try
            {
                var minions = g.Player != null && g.Player.Minions != null ? g.Player.Minions.ToList() : null;
                if (minions == null) return;
                foreach (var m in minions)
                {
                    if (m == null) continue;
                    int atk = 0, hp = 0;
                    try { atk = m.Attack; } catch { }
                    try { hp = m.Health; } catch { }
                    if (atk > _stats.HighestAttack) _stats.HighestAttack = atk;
                    if (hp > _stats.HighestHealth) _stats.HighestHealth = hp;
                    if (atk + hp > _stats.HighestMinionAttack + _stats.HighestMinionHealth)
                    {
                        _stats.HighestMinionAttack = atk;
                        _stats.HighestMinionHealth = hp;
                        _stats.HighestMinionCardId = m.CardId;
                    }
                }
            }
            catch { }
        }

        private void Act() { if (_turn != null) _turn.Actions++; }

        // ── reads ───────────────────────────────────────────────────────────────────────────────
        private static List<Entity> Snapshot(GameV2 g)
        {
            try { return g.Entities != null ? g.Entities.Values.ToList() : new List<Entity>(); }
            catch { return new List<Entity>(); }
        }

        private static int Tag(Entity e, GameTag tag)
        {
            try { return e != null ? e.GetTag(tag) : 0; } catch { return 0; }
        }

        private static int HeroHp(GameV2 g)
        {
            try
            {
                var hero = g.Player != null ? g.Player.Hero : null;
                if (hero == null) return 0;
                return hero.Health + hero.GetTag(GameTag.ARMOR);
            }
            catch { return 0; }
        }

        private static int GoldAvailable(GameV2 g)
        {
            try
            {
                var p = g.PlayerEntity;
                if (p == null) return 0;
                return Math.Max(0, p.GetTag(GameTag.RESOURCES) - p.GetTag(GameTag.RESOURCES_USED)
                                   + p.GetTag(GameTag.TEMP_RESOURCES));
            }
            catch { return 0; }
        }

        private static int BoardSize(GameV2 g)
        {
            try { return g.Player != null && g.Player.Minions != null ? g.Player.Minions.Count() : 0; }
            catch { return 0; }
        }

        private static bool IsMinion(Entity e)
        {
            try { return e.GetTag(GameTag.CARDTYPE) == (int)CardType.MINION; } catch { return false; }
        }

        private static bool IsTavernSpell(Entity e)
        {
            try
            {
                if (e.GetTag(GameTag.CARDTYPE) != (int)CardType.SPELL) return false;
                return e.HasTag(GameTag.IS_BACON_POOL_SPELL) || e.GetTag(GameTag.IS_BACON_POOL_SPELL) > 0;
            }
            catch { return false; }
        }

        private void Log(string msg)
        {
            try { if (_log != null) _log("[MatchStats] " + msg); } catch { }
        }

        private struct ZoneState
        {
            public readonly Zone Zone;
            public readonly int Controller;
            public ZoneState(Zone zone, int controller) { Zone = zone; Controller = controller; }
        }
    }
}
