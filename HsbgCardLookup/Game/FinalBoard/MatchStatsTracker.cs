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
    /// Counts what the player DID during a Battlegrounds match — buys, sells, rolls, plays, upgrades,
    /// combat damage — as a per-turn series.
    ///
    /// THE RULE THIS IS BUILT ON, measured rather than assumed: in a Battlegrounds match, one player
    /// action is exactly one BLOCK_START BlockType=PLAY block owned by the player. A whole 8-turn
    /// match was reconciled line by line against its own Power.log — 61 such blocks, 61 actions,
    /// nothing left over and nothing unclassified:
    ///
    ///     27 played from hand | 9 bought | 8 sold | 7 activated on board | 5 rolled | 3 upgraded
    ///      + 2 tavern spells bought
    ///
    /// The card id says WHICH action (the shop buttons are pseudo-cards), and the entity's zone
    /// separates a card played out of hand from an "Activate" ability used on the board.
    ///
    /// An earlier build inferred buys and plays from a per-entity (zone, controller) diff instead —
    /// Reign-in-blood's approach in HDT-FinalStatsPlugin, and the reason his AGENTS.md documents how
    /// each counter goes wrong. Measured against the same match it was wrong four ways: a minion
    /// GRANTED to hand for free (Deepwater Chieftain, arriving with a "Costs 0" enchantment) is
    /// indistinguishable from a purchase; buying a tavern SPELL takes a different button and was
    /// missed entirely; hero-selection cards leaving hand counted as minions played; and spells the
    /// game does not tag IS_BACON_POOL_SPELL landed in the minion bucket. The log has no such
    /// ambiguity, because it records the press rather than its consequences.
    ///
    /// EVERY counter is still a heuristic over data Blizzard can change without warning, so each
    /// detection logs why it fired — that is how a miscount after a patch shows up as a wrong log
    /// line instead of a wrong number a player trusts. Anything unrecognised stays uncounted.
    /// </summary>
    internal sealed class MatchStatsTracker
    {
        private const int PollMs = 200;

        // The tavern's own buttons. Every one of these is a pseudo-card whose PLAY block IS the press.
        // NB TB_BaconShop_DragBuy is a strict PREFIX of TB_BaconShop_DragBuy_Spell, so the card id has
        // to be compared whole — a substring test would count every spell purchase twice.
        private const string BuyCardId = "TB_BaconShop_DragBuy";
        private const string BuySpellCardId = "TB_BaconShop_DragBuy_Spell";
        private const string SellCardId = "TB_BaconShop_DragSell";
        private const string RerollCardId1 = "TB_BaconShop_1p_Reroll_Button";
        private const string RerollCardId8 = "TB_BaconShop_8p_Reroll_Button";
        private const string FreezeCardId = "TB_BaconShopLockAll_Button";
        private const string TripleCardId = "TB_BaconShop_Triples_01";
        // Tier 2..6 each have their own button: TB_BaconShopTechUp02_Button and friends.
        private const string TechUpPrefix = "TB_BaconShopTechUp";

        private readonly Action<string> _log;

        private DateTime _lastPoll = DateTime.MinValue;
        private bool _inMatch;
        private bool? _wasCombat;

        private MatchStats _stats;
        private TurnStat _turn;
        private readonly Stopwatch _shopClock = new Stopwatch();
        private TimeSpan _turnStartTod;

        private int _prevResourcesUsed;
        private int _combatDealt, _combatTaken;
        // Health + armour as the shop closed: the baseline a combat is judged against.
        private int _hpAtShopClose;
        private bool _turnClosed;
        private int _logIndex;
        private bool _playerIdWarned;

        // The rolling pre-MAIN_END sample and its latch. B cannot be a live read: the end-of-turn
        // trigger window resolves in the log in 0-150ms (measured over a full match), far inside our
        // poll latency, so any state read taken after MAIN_END is detected is already post-trigger.
        private ShopSnap _rolling;
        private bool _mainEndSeen;

        // Matches MatchRecorder.PostCombatSettleMs: board reads straight after the combat->recruit
        // flip are not yet trustworthy, so A waits this long into the shop before sampling.
        private const int OpenSettleMs = 600;

        public MatchStatsTracker(Action<string> log) { _log = log; }

        /// <summary>The live counters, or null outside a match.</summary>
        public MatchStats Current => _stats;

        /// <summary>
        /// Bumped every time the stats reach a state worth having on disk: a shop turn closed with
        /// its three snapshots, or a combat resolved. <see cref="FinalBoardCapture"/> compares it to
        /// the revision it last wrote and saves on any change — so each turn is on disk the moment
        /// it is complete, and a crash costs at most the turn in progress.
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Continue an earlier record's stats instead of starting fresh — the case where HDT (or the
        /// plugin) restarted mid-match and the turns already written must not be replaced by the
        /// few that follow. Only takes effect before the match's first poll has created its own.
        /// </summary>
        public void Adopt(MatchStats s)
        {
            if (s == null || _stats != null) return;
            if (s.Turns == null) s.Turns = new List<TurnStat>();
            _stats = s;
        }

        /// <summary>Called by <see cref="FinalBoardCapture"/> when it writes the record.</summary>
        public MatchStats TakeSnapshot()
        {
            // A match can end in three ways and each leaves a different thing undone. Beaten in
            // combat: the shop turn was closed at the phase edge but its combat never resolved,
            // because resolution normally happens when the NEXT shop opens and there isn't one.
            // Conceded from the shop: the turn is still open. Ended cleanly: both already done.
            if (_wasCombat.HasValue && _wasCombat.Value) ResolveCombat(Core.Game);
            CloseTurn(Core.Game);
            return _stats;
        }

        public void Reset()
        {
            _stats = null;
            _turn = null;
            _wasCombat = null;
            _shopClock.Reset();
            _turnStartTod = TimeSpan.Zero;
            _prevResourcesUsed = 0;
            _turnClosed = false;
            _logIndex = 0;
            _playerIdWarned = false;
            _combatDealt = _combatTaken = 0;
            _hpAtShopClose = 0;
            _rolling = null;
            _mainEndSeen = false;
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
                    if (combat) CloseTurn(g);              // shop just closed, combat starting
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

            // Hero selection reads as turn 0 and is not a shop. Opening a turn there produced a
            // phantom "turn 0" that swallowed the whole of turn 1 — its actions, its gold and its
            // clock — and left the record with no turn 1 at all. The hero does not exist yet either,
            // which is why that entry also recorded a starting health of 0.
            if (n < 1) return;

            _turn = new TurnStat
            {
                Turn = n,
                TavernTier = Tag(g.PlayerEntity, GameTag.PLAYER_TECH_LEVEL),
                HeroHpStart = HeroHp(g),
            };
            _stats.Turns.Add(_turn);
            _turnClosed = false;
            _shopClock.Restart();
            _turnStartTod = DateTime.Now.TimeOfDay;
            _rolling = null;
            _mainEndSeen = false;

            // RESOURCES_USED restarts each turn, so the baseline has to restart with it or the first
            // read of the new turn would be counted as a full turn's spending.
            _prevResourcesUsed = Math.Max(0, Tag(g.PlayerEntity, GameTag.RESOURCES_USED));
        }

        private void CloseTurn(GameV2 g)
        {
            if (_turn == null || _turnClosed) return;
            _turnClosed = true;
            _turn.ShopSeconds = Math.Round(_shopClock.Elapsed.TotalSeconds, 1);
            _shopClock.Stop();
            _hpAtShopClose = _turn.HeroHpEnd;   // the last tick sample, see TrackShop

            // C — post-trigger, pre-combat. The BOARD is a live read: this is the same edge
            // MatchRecorder's "End of Turn" capture reads boards from, verified stat-for-stat over a
            // full match (combat is simulated on separate entities, so the recruit board persists).
            // The SCALARS are the tick-sampled values: gold and hero HP live-read zero here because
            // the game has already torn the shop down (see TrackShop).
            // Hand and enchantments are live too — our own entities persist into combat. The SHOP is
            // not read here at all: the tavern is gone at this edge and the same filter would return
            // the opponent's warband.
            if (_turn.SnapPreCombat == null)
            {
                var sw = Stopwatch.StartNew();
                var all = AllEntities(g);
                var board = ReadBoard(g);
                var hand = ReadHand(g);
                _turn.SnapPreCombat = new ShopSnap
                {
                    AtMs = (int)_shopClock.ElapsedMilliseconds,
                    Gold = _turn.GoldLeftover,
                    TavernTier = _turn.TavernTier,
                    HeroHp = _turn.HeroHpEnd,
                    Board = board,
                    Hand = hand,
                    Enchants = ReadEnchants(g, all, board, hand),
                    Trinkets = ReadTrinkets(g),
                    HeroPower = ReadHeroPowerSnap(g),
                };
                Log(string.Format("turn {0} snapshot C | {1} ({2}ms to read)", _turn.Turn, Counts(_turn.SnapPreCombat), (int)sw.ElapsedMilliseconds));
            }
            Log(string.Format("turn {0} closed | tier={1} actions={2} shop={3}s spent={4} leftover={5} bought={6} sold={7} rolls={8}",
                _turn.Turn, _turn.TavernTier, _turn.Actions, _turn.ShopSeconds, _turn.GoldSpent,
                _turn.GoldLeftover, _turn.MinionsBought, _turn.MinionsSold, _turn.Rolls));
            _combatDealt = _combatTaken = 0;
            Revision++;
        }

        /// <summary>
        /// Settle the combat that just ended. Damage TAKEN is the hero's health at the END OF THE
        /// SHOP minus its health now — not the start of the turn. A shop has its own ways of moving
        /// that number (a demon that hits you, a spell that armours you), and none of them are a
        /// combat result: the first live match booked a 2-damage "loss" for a combat the hero
        /// walked out of untouched, because a demon had cost 2 in the shop. The user's rule:
        /// "the actual loss should resolve to a loss — everything else is self-inflicted until
        /// proven otherwise." Damage DEALT comes from the predamage event (see
        /// <see cref="NoteHeroDamage"/>), because nothing in our own state can observe it. A combat
        /// where neither side lost health is a draw, which is exactly how it should read.
        /// </summary>
        private void ResolveCombat(GameV2 g)
        {
            var t = _stats != null ? _stats.Turns.LastOrDefault() : null;
            if (t == null || t.CombatResult != null) return;

            t.HeroHpEnd = HeroHp(g);
            int baseline = _hpAtShopClose > 0 ? _hpAtShopClose : t.HeroHpStart;
            int lost = Math.Max(0, baseline - t.HeroHpEnd);
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
            Revision++;
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

            ScanPowerLog(g);
            if (_turn == null) return;

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
            if (tech > 0) _turn.TavernTier = tech;

            // Sampled every tick and kept, rather than read once when the turn closes. The close
            // happens on the combat edge, by which point the game has already torn the shop down:
            // gold read as zero for every single turn, and a conceded match recorded a health of
            // zero for a hero that was still alive. The last sample before the shop ends IS the end
            // state.
            _turn.GoldLeftover = GoldAvailable(g);
            _turn.BoardSize = BoardSize(g);
            _turn.HeroHpEnd = HeroHp(g);

            TrackBoardPeaks(g);

            // Rolling sample for B (and, first time, A). Runs AFTER ScanPowerLog on purpose: if this
            // poll's scan saw MAIN_END, _mainEndSeen already stopped the sampler, so the sample frozen
            // as B is from a poll where MAIN_END had not yet been written — pre-trigger by ordering.
            if (!_mainEndSeen && _shopClock.ElapsedMilliseconds >= OpenSettleMs)
            {
                var snap = BuildSnap(g);
                // Lines HDT parsed while we were sampling. HDT appends a line to PowerLog BEFORE
                // applying its tags (LogWatcherManager.OnNewLines), so if MAIN_END shows up here this
                // sample may already carry the triggers: the rescan freezes the PREVIOUS sample as B
                // and the latch drops this one.
                ScanPowerLog(g);
                if (snap != null && !_mainEndSeen)
                {
                    _rolling = snap;
                    // A waits for the gold. Turn 1 opens ~8s before the game deals the shop (measured:
                    // RESOURCES stays 0 through the intro), and a start snapshot with no gold is not
                    // the start of anything. Every later turn has its gold at open.
                    if (_turn.SnapStart == null && Tag(g.PlayerEntity, GameTag.RESOURCES) > 0)
                    {
                        _turn.SnapStart = snap;
                        Log(string.Format("turn {0} snapshot A at {1}ms | {2} gold={3} ({4}ms to read)",
                            _turn.Turn, snap.AtMs, Counts(snap), snap.Gold, _lastSnapMs));
                    }
                }
            }
        }

        /// <summary>
        /// Walk the new lines of HDT's Power.log and turn each player-owned PLAY block into an action.
        ///
        /// Only NEW lines are read, and the cursor resets if the log ever shrinks (a new game).
        /// ONLY BlockType=PLAY counts, which was measured rather than assumed: in a real 13-turn
        /// match the reroll button produced 40 PLAY blocks but 120 POWER and 53 TRIGGER ones — a
        /// single manual roll emits one PLAY plus several POWER/TRIGGER blocks at the same timestamp,
        /// and the tavern's automatic turn-start refresh emits a lone TRIGGER. PLAY is the one that
        /// means "the player pressed this". A passive hero power is the same story: Fragrant
        /// Phylactery fired seven TRIGGER blocks across a match and never a PLAY one, so counting
        /// hero-power PLAY blocks cannot mistake a passive for a press.
        ///
        /// Lines are only counted while a shop turn is OPEN. Nothing a player does happens outside
        /// one, and attributing a stray block to a turn already closed would corrupt its timings.
        /// </summary>
        private void ScanPowerLog(GameV2 g)
        {
            List<string> log;
            try { log = g != null ? g.PowerLog : null; } catch { return; }
            if (log == null) return;

            int count;
            try { count = log.Count; } catch { return; }
            if (count < _logIndex) _logIndex = 0;
            if (_turn == null || _turnClosed) { _logIndex = count; return; }

            for (int i = _logIndex; i < count; i++)
            {
                string line;
                try { line = log[i]; } catch { break; }
                if (string.IsNullOrEmpty(line)) continue;
                if (!_mainEndSeen
                    && line.IndexOf("tag=STEP value=MAIN_END", StringComparison.Ordinal) >= 0
                    && line.IndexOf("Entity=GameEntity", StringComparison.Ordinal) >= 0
                    && InThisTurn(line))
                {
                    // First MAIN_END while this shop turn is open = the SHOP game turn's. BG runs two
                    // game turns per round, and the combat turn's own MAIN_END follows 0.4-3s later
                    // (measured) - usually before the phase flag even flips - so the latch, not the
                    // timestamp, is what keeps the trailing one out. InThisTurn rejects replayed old
                    // lines after a reconnect resets the log cursor.
                    _mainEndSeen = true;
                    if (_rolling != null)
                    {
                        _turn.SnapEnd = _rolling;
                        Log(string.Format("turn {0} MAIN_END at {1}ms | B frozen from {2}ms ({3}ms stale) | {4} ({5}ms to read)",
                            _turn.Turn, OffsetMs(line), _rolling.AtMs,
                            (int)_shopClock.ElapsedMilliseconds - _rolling.AtMs, Counts(_rolling), _lastSnapMs));
                    }
                    else
                        Log(string.Format("turn {0} MAIN_END | B missed - no settled sample yet", _turn.Turn));
                    continue;
                }
                if (line.IndexOf("BLOCK_START", StringComparison.Ordinal) < 0) continue;
                if (line.IndexOf("BlockType=PLAY", StringComparison.Ordinal) < 0) continue;
                try { Classify(g, line); } catch { }
            }
            _logIndex = count;
        }

        private void Classify(GameV2 g, string line)
        {
            string cardId = Field(line, "cardId=");
            if (string.IsNullOrEmpty(cardId)) return;
            if (!IsOurs(g, line)) return;

            int at = OffsetMs(line);

            if (cardId == BuySpellCardId)                  // checked before BuyCardId: it is a prefix
            {
                _stats.SpellsBought++;
                Act("buyspell", at);
                Log("bought a tavern spell on turn " + TurnNo());
            }
            else if (cardId == BuyCardId)
            {
                _stats.MinionsBought++;
                _turn.MinionsBought++;
                Act("buy", at);
                Log("bought a minion on turn " + TurnNo());
            }
            else if (cardId == SellCardId)
            {
                _stats.MinionsSold++;
                _turn.MinionsSold++;
                Act("sell", at);
            }
            else if (cardId == RerollCardId8 || cardId == RerollCardId1)
            {
                _stats.TavernRolls++;
                _turn.Rolls++;
                Act("roll", at);
            }
            else if (cardId == FreezeCardId)
            {
                _stats.Freezes++;
                Act("freeze", at);
            }
            else if (cardId == TripleCardId)
            {
                _stats.TriplesCreated++;
            }
            else if (cardId.StartsWith(TechUpPrefix, StringComparison.Ordinal))
            {
                _stats.TavernUpgrades++;
                Act("upgrade", at);
                Log("tavern upgrade on turn " + TurnNo());
            }
            else
            {
                ClassifyCard(g, line, at);
            }
        }

        /// <summary>
        /// A card the player used rather than a button they pressed. The entity's ZONE is what
        /// separates the two cases: out of HAND is a card being played, still in PLAY is one of the
        /// season's "Activate (N)" abilities being used on a minion already on the board — a
        /// deliberate, gold-costing action that nothing was counting before.
        /// </summary>
        private void ClassifyCard(GameV2 g, string line, int at)
        {
            string zone = Field(line, "zone=");
            int type = CardTypeOf(g, line);

            if (type == (int)CardType.HERO_POWER)
            {
                _stats.HeroPowersUsed++;
                Act("heropower", at);
                return;
            }
            if (type == (int)CardType.HERO) return;   // the hero-selection pick is not a play

            if (zone == "HAND")
            {
                // Minion or not. Everything else played out of hand — tavern spells, the season's
                // magic items, anything new — counts as a spell rather than being dropped, because a
                // card the player spent a turn on should never vanish from their own action count.
                if (type == (int)CardType.MINION) _stats.MinionsPlayed++;
                else _stats.SpellsPlayed++;
                Act(type == (int)CardType.MINION ? "play" : "spell", at);
            }
            else if (zone == "PLAY" && type == (int)CardType.MINION)
            {
                _stats.MinionActivations++;
                Act("activate", at);
            }
        }

        /// <summary>
        /// Whether this block belongs to the local player. The log's own player= field answers it
        /// directly; the entity is consulted only when that field is missing or disagrees, which
        /// would mean the field does not mean what a whole reconciled match says it means.
        /// </summary>
        private bool IsOurs(GameV2 g, string line)
        {
            int mine;
            try { mine = g.Player != null ? g.Player.Id : -1; } catch { return false; }
            if (mine <= 0) return false;

            int logPlayer = ParseInt(Field(line, "player="));
            if (logPlayer > 0 && logPlayer == mine) return true;

            var e = EntityOf(g, line);
            if (e == null) return false;
            bool ours = Tag(e, GameTag.CONTROLLER) == mine;
            if (ours && logPlayer > 0 && !_playerIdWarned)
            {
                _playerIdWarned = true;
                Log("NOTE: log player=" + logPlayer + " but our player id is " + mine + " - using the entity's controller");
            }
            return ours;
        }

        private static Entity EntityOf(GameV2 g, string line)
        {
            int id = ParseInt(Field(line, " id="));
            if (id <= 0) return null;
            try
            {
                Entity e;
                return g.Entities != null && g.Entities.TryGetValue(id, out e) ? e : null;
            }
            catch { return null; }
        }

        private static int CardTypeOf(GameV2 g, string line)
        {
            var e = EntityOf(g, line);
            return e != null ? Tag(e, GameTag.CARDTYPE) : 0;
        }

        /// <summary>
        /// Milliseconds from the start of this shop turn, taken from the log line's own timestamp
        /// rather than from when we happened to read it. A 200ms poll can deliver a whole burst of
        /// lines at once, and the burst is exactly what a peak-APM window measures — so reading the
        /// clock at scan time would smear the fastest four seconds of the match into one instant.
        /// Falls back to the shop clock if the line carries no usable timestamp.
        /// </summary>
        private int OffsetMs(string line)
        {
            int fallback = (int)_shopClock.ElapsedMilliseconds;
            if (_turnStartTod == TimeSpan.Zero) return fallback;
            if (line.Length < 3 || line[0] != 'D' || line[1] != ' ') return fallback;
            int end = line.IndexOf(' ', 2);
            if (end < 0) return fallback;
            TimeSpan tod;
            if (!TimeSpan.TryParse(line.Substring(2, end - 2), out tod)) return fallback;
            double ms = (tod - _turnStartTod).TotalMilliseconds;
            if (ms < 0 || ms > 30 * 60 * 1000) return fallback;   // clock rollover, or a stale line
            return (int)ms;
        }

        /// <summary>
        /// Like <see cref="OffsetMs"/> but REJECTS instead of falling back: a MAIN_END line must
        /// prove it belongs to this shop turn. The fallback would defeat the point — a replayed old
        /// line (reconnect resets the cursor to 0) must not freeze B from a stale sample.
        /// </summary>
        private bool InThisTurn(string line)
        {
            if (_turnStartTod == TimeSpan.Zero) return false;
            if (line.Length < 3 || line[0] != 'D' || line[1] != ' ') return false;
            int end = line.IndexOf(' ', 2);
            if (end < 0) return false;
            TimeSpan tod;
            if (!TimeSpan.TryParse(line.Substring(2, end - 2), out tod)) return false;
            double ms = (tod - _turnStartTod).TotalMilliseconds;
            return ms >= 0 && ms <= 30 * 60 * 1000;
        }

        // What a snapshot minion keeps: identity + position, the stats a diff compares, and the
        // tags HDT's own BattlegroundsMinion renderer reads (pinned by IL: PREMIUM, TAUNT,
        // DIVINE_SHIELD, DEATHRATTLE, POISONOUS, VENOMOUS, REBORN, ATK, HEALTH, DAMAGE), plus
        // tier/race/legendary/keyword tags a later viewer needs. Everything else a live entity
        // carries (~36 tags) is noise three times per turn.
        private static readonly HashSet<int> SnapTags = new HashSet<int>
        {
            (int)GameTag.ENTITY_ID,
            (int)GameTag.ZONE_POSITION,
            (int)GameTag.ATK,
            (int)GameTag.HEALTH,
            (int)GameTag.DAMAGE,
            (int)GameTag.PREMIUM,
            (int)GameTag.TECH_LEVEL,
            (int)GameTag.CARDRACE,
            (int)GameTag.ELITE,
            (int)GameTag.TAUNT,
            (int)GameTag.DIVINE_SHIELD,
            (int)GameTag.DEATHRATTLE,
            (int)GameTag.POISONOUS,
            (int)GameTag.VENOMOUS,
            (int)GameTag.REBORN,
            (int)GameTag.WINDFURY,
            (int)GameTag.MEGA_WINDFURY,
            (int)GameTag.STEALTH,
            (int)GameTag.MODULAR,
            // Added with hand/shop: a hand or a tavern row holds spells too, and cost matters there.
            (int)GameTag.CARDTYPE,
            (int)GameTag.COST,
            // The cross-turn identity: after each combat every board minion is a NEW entity whose
            // COPIED_FROM_ENTITY_ID names the one it replaced (verified on the 2026-08-31 record).
            (int)GameTag.COPIED_FROM_ENTITY_ID,
        };

        /// <summary>How long the last snapshot took to read, for the log — the 200 ms poll pays this every tick of a shop.</summary>
        private int _lastSnapMs;

        private ShopSnap BuildSnap(GameV2 g)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var all = AllEntities(g);
                var board = ReadBoard(g);
                var hand = ReadHand(g);
                var snap = new ShopSnap
                {
                    AtMs = (int)_shopClock.ElapsedMilliseconds,
                    Gold = GoldAvailable(g),
                    TavernTier = _turn != null ? _turn.TavernTier : 0,
                    HeroHp = HeroHp(g),
                    Board = board,
                    Hand = hand,
                    Shop = IsDuos(g) ? null : ReadShop(g, all),
                    Enchants = ReadEnchants(g, all, board, hand),
                    Trinkets = ReadTrinkets(g),
                    HeroPower = ReadHeroPowerSnap(g),
                };
                _lastSnapMs = (int)sw.ElapsedMilliseconds;
                return snap;
            }
            catch { return null; }
        }

        private static string Counts(ShopSnap s) => s == null ? "-" : string.Format("board={0} hand={1} shop={2} ench={3} trinkets={4} hp={5}",
            s.Board != null ? s.Board.Count.ToString() : "-", s.Hand != null ? s.Hand.Count.ToString() : "-",
            s.Shop != null ? s.Shop.Count.ToString() : "-", s.Enchants != null ? s.Enchants.Count : 0,
            s.Trinkets != null ? s.Trinkets.Count.ToString() : "-",
            s.HeroPower != null ? s.HeroPower.CardId : "-");

        internal static bool IsDuos(GameV2 g)
        {
            try { return g.IsBattlegroundsDuosMatch; } catch { return false; }
        }

        private static List<Entity> AllEntities(GameV2 g)
        {
            try { return g != null && g.Entities != null ? g.Entities.Values.ToList() : new List<Entity>(); }
            catch { return new List<Entity>(); }
        }

        private static List<MinionRecord> ReadHand(GameV2 g)
        {
            try
            {
                var hand = g != null && g.Player != null && g.Player.Hand != null ? g.Player.Hand.ToList() : null;
                if (hand == null) return null;
                var list = new List<MinionRecord>();
                foreach (var c in hand.OrderBy(c => Tag(c, GameTag.ZONE_POSITION)))
                    if (c != null) list.Add(ToSnapRecord(c));
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// The tavern row: in-PLAY minions and spells some other controller owns. During a shop that
        /// is only the tavern (every hit across a whole solo match had controller 9, and the count
        /// tracked the tier); in combat the same filter is the opponent's warband, which is why C
        /// never calls this. Solo only — see <see cref="ShopSnap.Shop"/>.
        /// </summary>
        private static List<MinionRecord> ReadShop(GameV2 g, List<Entity> all)
        {
            var list = new List<MinionRecord>();
            try
            {
                if (all == null) return null;
                int us = g != null && g.Player != null ? g.Player.Id : -1;
                var row = new List<Entity>();
                foreach (var e in all)
                {
                    if (e == null) continue;
                    int zone = Tag(e, GameTag.ZONE), type = Tag(e, GameTag.CARDTYPE), ctrl = Tag(e, GameTag.CONTROLLER);
                    if (zone != (int)Zone.PLAY || ctrl == us || ctrl <= 0) continue;
                    if (type != (int)CardType.MINION && type != (int)CardType.SPELL) continue;
                    row.Add(e);
                }
                foreach (var e in row.OrderBy(e => Tag(e, GameTag.ZONE_POSITION)))
                    list.Add(ToSnapRecord(e));
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// Enchantments on our hosts — the board, the hand, the hero and the player entity — with
        /// identical ones (same host, card, source and script numbers) collapsed into one count.
        /// A late board carries up to ~32 per minion, most of them repeats of the same buff.
        /// </summary>
        private static readonly List<MinionRecord> Empty = new List<MinionRecord>();

        private static List<EnchantRecord> ReadEnchants(GameV2 g, List<Entity> all, List<MinionRecord> board, List<MinionRecord> hand)
        {
            var outp = new List<EnchantRecord>();
            try
            {
                var hosts = new HashSet<int>();
                foreach (var m in board ?? Empty) { int id; if (m.Tags != null && m.Tags.TryGetValue((int)GameTag.ENTITY_ID, out id)) hosts.Add(id); }
                foreach (var m in hand ?? Empty) { int id; if (m.Tags != null && m.Tags.TryGetValue((int)GameTag.ENTITY_ID, out id)) hosts.Add(id); }
                try { if (g.Player != null && g.Player.Hero != null) hosts.Add(g.Player.Hero.Id); } catch { }
                try { if (g.PlayerEntity != null) hosts.Add(g.PlayerEntity.Id); } catch { }
                if (hosts.Count == 0) return outp;

                var byKey = new Dictionary<string, EnchantRecord>();
                foreach (var e in all)
                {
                    if (e == null) continue;
                    bool ench = false;
                    try { ench = e.IsEnchantment; } catch { }
                    if (!ench) continue;
                    int host = Tag(e, GameTag.ATTACHED);
                    if (!hosts.Contains(host)) continue;

                    string source = null;
                    int creator = Tag(e, GameTag.CREATOR);
                    if (creator > 0)
                    {
                        try { Entity ce; if (g.Entities.TryGetValue(creator, out ce) && ce != null) source = ce.CardId; }
                        catch { }
                    }
                    int n1 = Tag(e, GameTag.TAG_SCRIPT_DATA_NUM_1), n2 = Tag(e, GameTag.TAG_SCRIPT_DATA_NUM_2);
                    string cardId = e.CardId ?? "";
                    string key = host + "|" + cardId + "|" + source + "|" + n1 + "|" + n2;
                    EnchantRecord r;
                    if (byKey.TryGetValue(key, out r)) r.Count++;
                    else
                    {
                        r = new EnchantRecord { Host = host, CardId = cardId, Source = source, Count = 1, N1 = n1, N2 = n2 };
                        byKey[key] = r;
                        outp.Add(r);
                    }
                }
            }
            catch { }
            return outp;
        }

        /// <summary>The trinkets held right now. Empty until the game offers the first one, which is
        /// a real reading and not a failure — only a throw gives null. Full tags, see
        /// <see cref="ShopSnap.Trinkets"/>.</summary>
        private static List<MinionRecord> ReadTrinkets(GameV2 g)
        {
            try
            {
                var list = new List<MinionRecord>();
                var trinkets = g != null && g.Player != null && g.Player.Trinkets != null
                    ? g.Player.Trinkets.ToList() : new List<Entity>();
                foreach (var t in trinkets.OrderBy(t => t != null ? t.Id : 0))
                    if (t != null) list.Add(FinalBoardCapture.ToRecord(t));
                return list;
            }
            catch { return null; }
        }

        /// <summary>The hero power right now, through the end-of-match record's own resolver so the
        /// two can never disagree. Null when it cannot be resolved.</summary>
        private static MinionRecord ReadHeroPowerSnap(GameV2 g)
        {
            try
            {
                string via;
                var e = FinalBoardCapture.ResolveHeroPower(g, out via);
                return e != null ? FinalBoardCapture.ToRecord(e) : null;
            }
            catch { return null; }
        }

        private static List<MinionRecord> ReadBoard(GameV2 g)
        {
            try
            {
                var minions = g != null && g.Player != null && g.Player.Minions != null
                    ? g.Player.Minions.ToList() : null;
                if (minions == null) return null;
                var list = new List<MinionRecord>();
                foreach (var m in minions)
                    if (m != null) list.Add(ToSnapRecord(m));
                return list;
            }
            catch { return null; }
        }

        /// <summary>ENTITY_ID is forced from <c>e.Id</c> — authoritative even if the tag were absent.</summary>
        private static MinionRecord ToSnapRecord(Entity e)
        {
            var tags = new Dictionary<int, int>();
            try
            {
                if (e.Tags != null)
                    foreach (var kv in e.Tags.ToList())
                        if (SnapTags.Contains((int)kv.Key)) tags[(int)kv.Key] = kv.Value;
            }
            catch { }
            try { tags[(int)GameTag.ENTITY_ID] = e.Id; } catch { }
            return new MinionRecord { CardId = e.CardId, Tags = tags };
        }

        private static string Field(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = i;
            while (j < line.Length && line[j] != ' ' && line[j] != ']') j++;
            return line.Substring(i, j - i);
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        private int TurnNo() { return _turn != null ? _turn.Turn : 0; }

        /// <summary>
        /// Biggest minion of the match. Attack and health are taken from the SAME minion — gluing the
        /// best attack seen to the best health seen would invent a creature that never existed.
        /// </summary>
        private void TrackBoardPeaks(GameV2 g)
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

        /// <summary>
        /// Record one player action. The timestamp is what makes any later APM definition possible;
        /// the kind is what makes a per-turn breakdown possible. Both are cheap.
        ///
        /// Which kinds count as "an action" is deliberately NOT decided here — everything the player
        /// does is recorded, and the formula picks. Firestone counts rerolls, buys, sells, plays,
        /// spells, discovers, upgrades and hero powers but NOT freezes; keeping the raw stream means
        /// matching them, or not, stays a display decision rather than a data one.
        /// </summary>
        private void Act(string kind, int atMs)
        {
            if (_turn == null) return;
            _turn.Actions++;
            _turn.ActionTimes.Add(atMs);
            _turn.ActionKinds.Add(kind);
        }

        // ── reads ───────────────────────────────────────────────────────────────────────────────
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

        private void Log(string msg)
        {
            try { if (_log != null) _log("[MatchStats] " + msg); } catch { }
        }
    }
}
