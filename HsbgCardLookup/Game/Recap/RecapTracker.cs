using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;                        // Core.Game
using Hearthstone_Deck_Tracker.Hearthstone;            // GameV2
using Hearthstone_Deck_Tracker.Hearthstone.Entities;   // Entity

namespace HsbgCardLookup.Game.Recap
{
    /// <summary>
    /// Builds a <see cref="RecapRecord"/> for the solo Battlegrounds match in progress: one
    /// <see cref="RecapTurn"/> per shop, with the player's actions and the damage the following
    /// combat dealt.
    ///
    /// Shop turns are defined by the REAL-TIME log, not by HDT's phase flag. <c>Core.Game.PowerLog</c>
    /// holds every <c>GameState.DebugPrintPower</c> line verbatim, timestamp included. Battlegrounds
    /// runs two game turns per round — the shop on the ODD game turn, the combat on the even one
    /// (checked across two full recorded matches, 2026-09-26: every shop sat on TURN 1, 3, 5…) — so
    /// a shop is the span from the game entity's <c>TURN</c> change to an odd value up to that
    /// turn's <c>STEP=MAIN_END</c>. Two things the same logs ruled out: opening on MAIN_ACTION
    /// instead (a combat turn occasionally carries a 0.05 s MAIN_ACTION of its own, which would
    /// make a phantom shop and steal the next combat's damage), and starting the clock at
    /// MAIN_ACTION (one shop spent 49 s in MAIN_START_TRIGGERS on a start-of-turn pick, time the
    /// shop timer counts and the player experiences as their turn). HDT's entity tags replay the
    /// animated timeline, which can lag the server by a whole combat, so the next shop may already
    /// be open in the log while HDT still says "combat". HDT's combat flag is used only to know
    /// when a combat has finished animating, at which point the damage it dealt is attached to the
    /// earliest shop still waiting for its combat.
    ///
    /// One player action is one <c>BLOCK_START BlockType=PLAY</c> block owned by the player — a rule
    /// reconciled line by line against a whole match on the final-board-stats branch (61 blocks,
    /// 61 actions, nothing unclassified). The tavern buttons are pseudo-cards, so the card id says
    /// which button; anything else is a card played from hand, an ability activated on the board,
    /// or a hero power. All of them count.
    /// </summary>
    internal sealed class RecapTracker
    {
        private const int PollMs = 200;

        // The tavern's own buttons. TB_BaconShop_DragBuy is a strict PREFIX of the spell-buy id, so
        // card ids are compared whole.
        private const string BuyCardId = "TB_BaconShop_DragBuy";
        private const string BuySpellCardId = "TB_BaconShop_DragBuy_Spell";
        private const string SellCardId = "TB_BaconShop_DragSell";
        private const string RerollCardId1 = "TB_BaconShop_1p_Reroll_Button";
        private const string RerollCardId8 = "TB_BaconShop_8p_Reroll_Button";
        private const string FreezeCardId = "TB_BaconShopLockAll_Button";
        private const string TripleCardId = "TB_BaconShop_Triples_01";
        private const string TechUpPrefix = "TB_BaconShopTechUp";

        private const string TurnMarker = "tag=TURN value=";
        private const string MainEndMarker = "tag=STEP value=MAIN_END";
        private const string GameEntityMarker = "Entity=GameEntity";
        // The CREATE_GAME dump: "GameEntity EntityID=10" followed by its tags, one per indented line.
        private const string DumpEntityMarker = "GameEntity EntityID=";
        private const string DumpTagMarker = "        tag=";
        private const string StepMarker = "tag=STEP value=";
        private const double CarryMaxMinutes = 15;

        private readonly Action<string> _log;
        private DateTime _lastPoll = DateTime.MinValue;

        private RecapRecord _rec;
        private bool _inMatch;
        private bool _skipped;          // duos, or anything else the recap does not score
        private bool? _wasCombat;
        private int _combatDealt;

        private RecapTurn _open;        // the shop whose MAIN_END has not been seen yet
        private TimeSpan _openTod;      // its MAIN_ACTION timestamp (Zero when the clock stood in)
        private DateTime _openWall;     // poll-clock fallback for the same edge
        private readonly Dictionary<string, int> _kinds = new Dictionary<string, int>();

        private int _logIndex;
        private TimeSpan _lastTod;      // monotonic guard: a reconnect replays old lines from index 0
        private bool _playerIdWarned;

        // A reconnect. When the Hearthstone client drops out of a match and rejoins it, HDT resets
        // and fires OnGameStart again, and the new log opens with a CREATE_GAME dump whose game
        // entity already carries the TURN the match is on (a fresh game has no TURN there). The
        // record in progress is parked in _carry at that game start and taken back as soon as the
        // dump shows the same match continuing; a game that starts from turn 1 drops it.
        private bool _finished;         // Finish() ran: this record is not a reconnect candidate
        private Carry _carry;
        private int _liveFrom;          // first shop of the current log; earlier ones lost their combats
        private bool _inDump;           // reading the game entity's tags in a CREATE_GAME dump
        private int _dumpTurn;
        private string _dumpStep;
        private TimeSpan _dumpTod;

        private sealed class Carry
        {
            public RecapRecord Record;
            public RecapTurn Open;
            public TimeSpan OpenTod, LastTod;
            public DateTime OpenWall, At;
            public Dictionary<string, int> Kinds;
        }

        public RecapTracker(Action<string> log) { _log = log; }

        /// <summary>The match being scored, or null outside a match / in a match the recap skips.</summary>
        public RecapRecord Current => _rec;

        public void Reset()
        {
            _rec = null;
            _inMatch = false;
            _skipped = false;
            _wasCombat = null;
            _combatDealt = 0;
            _open = null;
            _openTod = TimeSpan.Zero;
            _kinds.Clear();
            _logIndex = 0;
            _lastTod = TimeSpan.Zero;
            _playerIdWarned = false;
            _finished = false;
            _carry = null;
            _liveFrom = 0;
            _inDump = false;
        }

        /// <summary>HDT announced a game start. Park an unfinished record in case this is the same
        /// match coming back from a reconnect (see <see cref="_carry"/>).</summary>
        public void Restart()
        {
            Carry carry = null;
            if (_rec != null && !_finished && _rec.Turns.Count > 0)
                carry = new Carry
                {
                    Record = _rec, Open = _open, OpenTod = _openTod, OpenWall = _openWall,
                    LastTod = _lastTod, At = DateTime.Now, Kinds = new Dictionary<string, int>(_kinds),
                };
            Reset();
            _carry = carry;
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
                    _inMatch = false;
                    return;
                }
                if (!_inMatch)
                {
                    _inMatch = true;
                    if (_rec == null && !_skipped) _rec = new RecapRecord { StartedAt = DateTime.Now };
                }
                if (_skipped) return;

                bool duos = false;
                try { duos = g.IsBattlegroundsDuosMatch; } catch { }
                if (duos)
                {
                    Log("duos match - not scored");
                    _rec = null;
                    _skipped = true;
                    return;
                }

                FillIdentity(g);
                ScanPowerLog(g);

                bool combat = false;
                try { combat = g.IsBattlegroundsCombatPhase; } catch { }
                if (!_wasCombat.HasValue) _wasCombat = combat;
                else if (_wasCombat.Value != combat)
                {
                    _wasCombat = combat;
                    if (!combat) ResolveCombat();   // combat finished animating → damage is final
                }
            }
            catch (Exception ex) { Log("Poll error: " + ex.Message); }
        }

        /// <summary>
        /// Close out the match: a shop still open (conceded from the shop) closes on the clock, a
        /// combat HDT was still showing resolves with what it dealt so far, and the placement is
        /// read from the player entity. Returns null when there is nothing to score.
        /// </summary>
        public RecapRecord Finish()
        {
            if (_rec == null) return null;
            var g = Core.Game;
            try
            {
                FillIdentity(g);
                ScanPowerLog(g);
                if (_open != null) CloseOpen(TimeSpan.Zero, "match ended");
                if (_wasCombat.HasValue && _wasCombat.Value) ResolveCombat();
                ReadPlacement(g);
            }
            catch (Exception ex) { Log("Finish error: " + ex.Message); }
            _finished = true;
            return _rec;
        }

        /// <summary>True once the placement tag has a value; the caller re-tries until then.</summary>
        public bool ReadPlacement(GameV2 g)
        {
            if (_rec == null) return false;
            int place = Tag(g != null ? g.PlayerEntity : null, GameTag.PLAYER_LEADERBOARD_PLACE);
            if (place > 0) _rec.Placement = place;
            return place > 0;
        }

        private void FillIdentity(GameV2 g)
        {
            if (_rec == null || g == null) return;
            if (string.IsNullOrEmpty(_rec.GameId))
            {
                try
                {
                    var stats = g.CurrentGameStats;
                    if (stats != null && stats.StartTime != DateTime.MinValue)
                    {
                        _rec.GameId = stats.StartTime.ToString("o");
                        _rec.StartedAt = stats.StartTime;
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(_rec.HeroCardId))
            {
                try
                {
                    var hero = g.Player != null ? g.Player.Hero : null;
                    if (hero != null && !string.IsNullOrEmpty(hero.CardId) && !hero.CardId.StartsWith("TB_BaconShop_HERO_PH", StringComparison.Ordinal))
                        _rec.HeroCardId = hero.CardId;
                }
                catch { }
            }
        }

        // ── the log ─────────────────────────────────────────────────────────────────────────────
        private void ScanPowerLog(GameV2 g)
        {
            List<string> log;
            try { log = g != null ? g.PowerLog : null; } catch { return; }
            if (log == null) return;

            int count;
            try { count = log.Count; } catch { return; }
            if (count < _logIndex) _logIndex = 0;   // HDT cleared it (reconnect / new game)

            for (int i = _logIndex; i < count; i++)
            {
                string line;
                try { line = log[i]; } catch { break; }
                if (string.IsNullOrEmpty(line)) continue;

                TimeSpan tod;
                bool stamped = TryTod(line, out tod);
                if (stamped)
                {
                    // Replayed lines are older than what we already consumed. A midnight rollover
                    // is the one legitimate way for time to go backwards, by nearly a full day.
                    if (tod < _lastTod && (_lastTod - tod).TotalHours < 12) continue;
                    _lastTod = tod;
                }

                if (_inDump)
                {
                    if (line.IndexOf(DumpTagMarker, StringComparison.Ordinal) >= 0)
                    {
                        if (line.IndexOf(TurnMarker, StringComparison.Ordinal) >= 0) _dumpTurn = ParseInt(Field(line, TurnMarker));
                        else if (line.IndexOf(StepMarker, StringComparison.Ordinal) >= 0) _dumpStep = Field(line, StepMarker);
                        continue;
                    }
                    _inDump = false;                 // the game entity's tags are over; this line is the next block
                    JoinInProgress(_dumpTurn, _dumpStep, _dumpTod);
                }
                if (line.IndexOf(DumpEntityMarker, StringComparison.Ordinal) >= 0)
                {
                    _inDump = true;
                    _dumpTurn = 0;
                    _dumpStep = null;
                    _dumpTod = stamped ? tod : TimeSpan.Zero;
                    continue;
                }

                if (line.IndexOf(GameEntityMarker, StringComparison.Ordinal) >= 0)
                {
                    int turnAt = line.IndexOf(TurnMarker, StringComparison.Ordinal);
                    if (turnAt >= 0)
                    {
                        _carry = null;               // a TURN change with the record still parked: not a rejoin
                        int gameTurn = ParseInt(Field(line, TurnMarker));
                        // A shop's MAIN_END precedes the next TURN line in the same millisecond, so
                        // reaching a TURN change with a shop still open means the end was missed.
                        if (_open != null) CloseOpen(stamped ? tod : TimeSpan.Zero, "closed by TURN " + gameTurn);
                        if (gameTurn > 0 && gameTurn % 2 == 1) OpenTurn((gameTurn + 1) / 2, stamped ? tod : TimeSpan.Zero);
                    }
                    else if (_open != null && line.IndexOf(MainEndMarker, StringComparison.Ordinal) >= 0)
                    {
                        CloseOpen(stamped ? tod : TimeSpan.Zero, null);
                    }
                    continue;
                }

                if (_open == null) continue;
                if (line.IndexOf("BLOCK_START", StringComparison.Ordinal) < 0) continue;
                if (line.IndexOf("BlockType=PLAY", StringComparison.Ordinal) < 0) continue;
                try { Classify(g, line, stamped ? tod : TimeSpan.Zero); } catch { }
            }
            _logIndex = count;
        }

        /// <summary>The log opened mid-game: CREATE_GAME gave the game entity a TURN above 1. Take the
        /// parked record back if it is this match, and pick up the shop the game is standing in —
        /// its TURN change was logged before the reconnect and will not come again.</summary>
        private void JoinInProgress(int gameTurn, string step, TimeSpan tod)
        {
            if (gameTurn <= 1 || _rec == null) return;
            int round = (gameTurn + 1) / 2;
            TakeCarry(round);
            if (_liveFrom == 0) _liveFrom = round;

            bool shop = gameTurn % 2 == 1 && step != "MAIN_END" && step != "MAIN_CLEANUP" && step != "MAIN_NEXT";
            if (!shop || (_open != null && _open.Turn == round)) return;
            OpenTurn(round, tod);
        }

        private void TakeCarry(int joinRound)
        {
            var c = _carry;
            _carry = null;
            if (c == null) return;
            if (c.Record.Turns.Max(t => t.Turn) > joinRound) return;
            if ((DateTime.Now - c.At).TotalMinutes > CarryMaxMinutes) return;
            bool bothHeroes = !string.IsNullOrEmpty(c.Record.HeroCardId) && !string.IsNullOrEmpty(_rec.HeroCardId);
            if (bothHeroes && c.Record.HeroCardId != _rec.HeroCardId) return;

            _rec.Turns.InsertRange(0, c.Record.Turns);
            if (string.IsNullOrEmpty(_rec.HeroCardId)) _rec.HeroCardId = c.Record.HeroCardId;
            _liveFrom = joinRound;
            Log(string.Format("reconnect: kept {0} recorded turn(s), rejoined on turn {1}", c.Record.Turns.Count, joinRound));

            if (c.Open == null) return;
            if (c.Open.Turn == joinRound)
            {
                // Still the same shop: carry on with its own opening time, so the window and the
                // action offsets stay one continuous turn.
                _open = c.Open;
                _openTod = c.OpenTod;
                _openWall = c.OpenWall;
                foreach (var k in c.Kinds) _kinds[k.Key] = k.Value;
            }
            else
            {
                // A shop the client never saw the end of: it ran at least to the last line we read.
                double seconds = (c.LastTod - c.OpenTod).TotalSeconds;
                c.Open.WindowSeconds = seconds > 0 && seconds < 30 * 60 ? Math.Round(seconds, 1) : 0;
                c.Open.WindowSource = "cut";
            }
        }

        private void OpenTurn(int round, TimeSpan tod)
        {
            _open = new RecapTurn { Turn = round };
            _openTod = tod;
            _openWall = DateTime.Now;
            _kinds.Clear();
            _rec.Turns.Add(_open);
        }

        private void CloseOpen(TimeSpan endTod, string why)
        {
            var t = _open;
            if (t == null) return;
            bool fromLog = _openTod != TimeSpan.Zero && endTod != TimeSpan.Zero;
            double seconds = fromLog
                ? (endTod - _openTod).TotalSeconds
                : (DateTime.Now - _openWall).TotalSeconds;
            if (seconds < 0 || seconds > 30 * 60) { seconds = (DateTime.Now - _openWall).TotalSeconds; fromLog = false; }
            t.WindowSeconds = Math.Round(seconds, 1);
            t.WindowSource = fromLog ? "log" : "clock";
            _open = null;

            Log(string.Format("turn {0} window={1}s({2}) actions={3} apm={4:0.0}{5}{6}",
                t.Turn, t.WindowSeconds, t.WindowSource, t.Actions, t.Apm,
                _kinds.Count > 0 ? " | " + string.Join(" ", _kinds.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)) : "",
                why != null ? " | " + why : ""));
        }

        private void Classify(GameV2 g, string line, TimeSpan tod)
        {
            string cardId = Field(line, "cardId=");
            if (string.IsNullOrEmpty(cardId)) return;
            if (!IsOurs(g, line)) return;

            if (cardId == BuySpellCardId) Act("buyspell", tod);          // before BuyCardId: it is a prefix
            else if (cardId == BuyCardId) Act("buy", tod);
            else if (cardId == SellCardId) Act("sell", tod);
            else if (cardId == RerollCardId8 || cardId == RerollCardId1) Act("roll", tod);
            else if (cardId == FreezeCardId) Act("freeze", tod);
            else if (cardId == TripleCardId) { }                          // the game's doing, not a press
            else if (cardId.StartsWith(TechUpPrefix, StringComparison.Ordinal)) Act("upgrade", tod);
            else
            {
                string zone = Field(line, "zone=");
                int type = CardTypeOf(g, line);
                if (type == (int)CardType.HERO_POWER) Act("heropower", tod);
                else if (type == (int)CardType.HERO) { }                  // the hero-selection pick
                else if (zone == "HAND") Act(type == (int)CardType.MINION ? "play" : "spell", tod);
                else if (zone == "PLAY" && type == (int)CardType.MINION) Act("activate", tod);
                // Season buttons that are not tavern buttons: the Dark Discovery button
                // (BG36_Button_DarkGift) is a PLAY block in zone PLAY on a non-minion — a press the
                // first live match dropped three times (verified against its Power.log, 340 vs 337).
                else if (zone == "PLAY" && cardId.IndexOf("_Button", StringComparison.Ordinal) >= 0) Act("button", tod);
            }
        }

        private void Act(string kind, TimeSpan tod)
        {
            if (_open == null) return;
            int at = _openTod != TimeSpan.Zero && tod != TimeSpan.Zero
                ? (int)Math.Max(0, (tod - _openTod).TotalMilliseconds)
                : (int)Math.Max(0, (DateTime.Now - _openWall).TotalMilliseconds);
            _open.Actions++;
            _open.ActionTimes.Add(at);
            int n; _kinds.TryGetValue(kind, out n); _kinds[kind] = n + 1;
        }

        // ── combat ──────────────────────────────────────────────────────────────────────────────
        private void ResolveCombat()
        {
            // The combat that just finished belongs to the earliest shop still waiting for one —
            // never to the shop that may already be open in the log, and never to a shop from before
            // a reconnect (its combat played out while the client was away).
            var t = _rec != null ? _rec.Turns.FirstOrDefault(x => !x.Fought && x.Turn >= _liveFrom && !ReferenceEquals(x, _open)) : null;
            if (t == null) { _combatDealt = 0; return; }
            t.Fought = true;
            t.DamageDealt = _combatDealt;
            Log(string.Format("combat after turn {0} dealt={1}", t.Turn, t.DamageDealt));
            _combatDealt = 0;
        }

        /// <summary>
        /// HDT's predamage event. Accept a hit ONLY when the damaged entity is exactly the hero the
        /// opponent player entity points at through HERO_ENTITY — Battlegrounds exposes several
        /// hero-shaped entities and one impact can raise PREDAMAGE more than once, so anything
        /// looser double-counts. Only the largest hit per combat is kept, for the same reason.
        /// </summary>
        public void HandlePredamage(Hearthstone_Deck_Tracker.API.PredamageInfo info)
        {
            try
            {
                if (_rec == null || info == null || info.Entity == null || info.Value <= 0) return;
                var g = Core.Game;
                if (g == null || !g.IsBattlegroundsMatch || !g.IsBattlegroundsCombatPhase) return;
                if (!info.Entity.IsHero) return;
                int theirs = Tag(g.OpponentEntity, GameTag.HERO_ENTITY);
                if (theirs > 0 && info.Entity.Id == theirs && info.Value > _combatDealt) _combatDealt = info.Value;
            }
            catch { }
        }

        // ── reads ───────────────────────────────────────────────────────────────────────────────
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

        private static int Tag(Entity e, GameTag tag)
        {
            try { return e != null ? e.GetTag(tag) : 0; } catch { return 0; }
        }

        /// <summary>The line's own timestamp: "D HH:mm:ss.fffffff ..." — HDT keeps the prefix.</summary>
        private static bool TryTod(string line, out TimeSpan tod)
        {
            tod = TimeSpan.Zero;
            if (line.Length < 3 || line[0] != 'D' || line[1] != ' ') return false;
            int end = line.IndexOf(' ', 2);
            if (end < 0) return false;
            return TimeSpan.TryParse(line.Substring(2, end - 2), out tod);
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

        private void Log(string msg)
        {
            try { _log?.Invoke("[Recap] " + msg); } catch { }
        }
    }
}
