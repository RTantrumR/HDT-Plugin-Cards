using System;
using System.Diagnostics;

namespace MemSpike
{
    /// <summary>
    /// Spike: read the Hearthstone client's managed objects with nothing but ReadProcessMemory.
    ///   MemSpike info            attach, print the discovered runtime layout
    ///   MemSpike me              own battletag (works from the main menu)
    ///   MemSpike lobby           the match's players: id, name, hero card (needs a game in progress)
    ///   MemSpike board           leaderboard tiles in screen order: hero, place, tier, minion types
    ///   MemSpike recent          Battle.net recent-players list with battletags
    ///   MemSpike class &lt;Name&gt;    fields a class declares
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;   // names can be Cyrillic, CJK, ...
            string cmd = args.Length > 0 ? args[0] : "info";
            var procs = Process.GetProcessesByName("Hearthstone");
            if (procs.Length == 0) { Console.WriteLine("Hearthstone is not running."); return 2; }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var mem = new Mem(procs[0].Id))
                {
                    var mono = new Mono(mem);
                    mono.Attach(procs[0], "Assembly-CSharp");
                    bool statics = mono.CalibrateStatics("BnetPresenceMgr", "s_instance");
                    long attachMs = sw.ElapsedMilliseconds, attachReads = mem.Reads;

                    switch (cmd)
                    {
                        case "info":
                            Console.WriteLine(mono.Describe());
                            Console.WriteLine("statics calibrated: " + statics);
                            break;
                        case "class":
                            long k = mono.Class(args[1]);
                            if (k == 0) { Console.WriteLine("class not found"); break; }
                            Console.WriteLine(args[1] + " : " + mono.ClassName(mono.Parent(k)));
                            foreach (var f in mono.Fields(k)) Console.WriteLine("  " + (f.Static ? "static " : "       ") + "0x" + f.Offset.ToString("X3") + "  " + f.Name);
                            break;
                        case "me":
                            if (!statics) { Console.WriteLine("statics not calibrated"); return 3; }
                            Me(mono);
                            break;
                        case "lobby":
                            if (!statics) { Console.WriteLine("statics not calibrated"); return 3; }
                            Lobby(mono);
                            break;
                        case "board":
                            if (!statics) { Console.WriteLine("statics not calibrated"); return 3; }
                            Board(mono);
                            break;
                        case "recent":
                            if (!statics) { Console.WriteLine("statics not calibrated"); return 3; }
                            Recent(mono);
                            break;
                        default:
                            Console.WriteLine("unknown command");
                            return 1;
                    }
                    Console.WriteLine("attach " + attachMs + " ms / " + attachReads + " reads; command "
                        + (sw.ElapsedMilliseconds - attachMs) + " ms / " + (mem.Reads - attachReads) + " reads");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 4;
            }
        }

        // BnetPresenceMgr.s_instance.m_myPlayer.m_account.m_battleTag { m_name, m_number }
        private static void Me(Mono mono)
        {
            long mgr = mono.StaticObj("BnetPresenceMgr", "s_instance");
            long tag = mono.Obj(mono.Obj(mono.Obj(mgr, "m_myPlayer"), "m_account"), "m_battleTag");
            if (tag == 0) { Console.WriteLine("no battletag (not logged in yet?)"); return; }
            Console.WriteLine("me: " + mono.Str(tag, "m_name") + "#" + mono.Str(tag, "m_number"));
        }

        // The leaderboard as the client draws it, top to bottom:
        // PlayerLeaderboardManager.s_instance.m_teams[].m_playerLeaderboardCards[]
        //   .m_playerHeroEntity { m_cardIdInternal, m_tags.m_values }   .m_overlay { m_raceCounts, m_recentCombatsPanel.m_techLevelCount }
        private static void Board(Mono mono)
        {
            long mgr = mono.StaticObj("PlayerLeaderboardManager", "s_instance");
            if (mgr == 0) { Console.WriteLine("no leaderboard (PlayerLeaderboardManager.s_instance is null)"); return; }
            int pos = 0;
            foreach (long team in mono.List(mono.Obj(mgr, "m_teams")))
                foreach (long card in mono.List(mono.Obj(team, "m_playerLeaderboardCards")))
                {
                    pos++;
                    long hero = mono.Obj(card, "m_playerHeroEntity");
                    int entityId = 0, playerId = 0, place = 0, tierTag = 0, health = 0, damage = 0;
                    foreach (var t in mono.IntIntDict(mono.Obj(mono.Obj(hero, "m_tags"), "m_values")))
                    {
                        if (t.Key == 53) entityId = t.Value;          // ENTITY_ID
                        else if (t.Key == 30) playerId = t.Value;     // PLAYER_ID
                        else if (t.Key == 1373) place = t.Value;      // PLAYER_LEADERBOARD_PLACE
                        else if (t.Key == 1377) tierTag = t.Value;    // PLAYER_TECH_LEVEL
                        else if (t.Key == 45) health = t.Value;       // HEALTH
                        else if (t.Key == 44) damage = t.Value;       // DAMAGE
                    }
                    long overlay = mono.Obj(card, "m_overlay");
                    var races = new System.Text.StringBuilder();
                    foreach (var r in mono.IntIntMap(mono.Obj(overlay, "m_raceCounts"))) races.Append(r.Key).Append(':').Append(r.Value).Append(' ');
                    int tierShown = mono.Int(mono.Obj(overlay, "m_recentCombatsPanel"), "m_techLevelCount");
                    Console.WriteLine("#" + pos + " hero=" + mono.Str(hero, "m_cardIdInternal") + " entity=" + entityId + " player=" + playerId
                        + " place=" + place + " hp=" + (health - damage) + " tier(tag)=" + tierTag + " tier(shown)=" + tierShown + " races[" + races.ToString().Trim() + "]");
                }
            Console.WriteLine(pos + " tiles");
        }

        // BnetRecentPlayerMgr.s_instance.m_recentPlayers : List<BnetPlayer>, capped at 5 on PC.
        // Each BnetPlayer's battletag: m_account.m_battleTag, else m_hsGameAccount.m_battleTag.
        private static void Recent(Mono mono)
        {
            long mgr = mono.StaticObj("BnetRecentPlayerMgr", "s_instance");
            if (mgr == 0) { Console.WriteLine("no BnetRecentPlayerMgr instance"); return; }
            int n = 0;
            foreach (long p in mono.List(mono.Obj(mgr, "m_recentPlayers")))
            {
                Console.WriteLine("recent " + (++n) + ": " + BattleTag(mono, p));
            }
            Console.WriteLine(n + " recent players");
        }

        private static string BattleTag(Mono mono, long bnetPlayer)
        {
            long tag = mono.Obj(mono.Obj(bnetPlayer, "m_account"), "m_battleTag");
            if (tag == 0) tag = mono.Obj(mono.Obj(bnetPlayer, "m_hsGameAccount"), "m_battleTag");
            return tag == 0 ? "(no battletag)" : mono.Str(tag, "m_name") + "#" + mono.Str(tag, "m_number");
        }

        // Every instance field of an object as raw 8 bytes — for classes outside Assembly-CSharp
        // whose layout we have not decompiled (e.g. BnetGameAccountId).
        private static string Raw(Mono mono, long obj)
        {
            if (obj == 0) return "null";
            var sb = new System.Text.StringBuilder(mono.ClassName(mono.ClassOf(obj)) ?? "?");
            int depth = 0;
            for (long k = mono.ClassOf(obj); k != 0 && depth < 4; k = mono.Parent(k), depth++)
                foreach (var f in mono.Fields(k))
                    if (!f.Static) sb.Append(' ').Append(f.Name).Append("=0x").Append(mono.M.Ptr(obj + f.Offset).ToString("X"));
            return sb.ToString();
        }

        // GameState.s_instance.m_playerInfoMap : Map<int, SharedPlayerInfo { m_name, m_playerHero }>
        private static void Lobby(Mono mono)
        {
            long gs = mono.StaticObj("GameState", "s_instance");
            if (gs == 0) { Console.WriteLine("no game in progress (GameState.s_instance is null)"); return; }
            int n = 0;
            foreach (var kv in mono.IntMap(mono.Obj(gs, "m_playerInfoMap")))
            {
                long hero = mono.Obj(kv.Value, "m_playerHero");
                Console.WriteLine("player " + kv.Key + ": name='" + mono.Str(kv.Value, "m_name") + "' hero=" + (mono.Str(hero, "m_cardIdInternal") ?? "(none)")
                    + " gameAccount=[" + Raw(mono, mono.Obj(kv.Value, "m_gameAccountId")) + "]");
                n++;
            }
            Console.WriteLine(n + " entries in m_playerInfoMap");
            n = 0;
            foreach (var kv in mono.IntMap(mono.Obj(gs, "m_playerMap")))
            {
                Console.WriteLine("Player " + kv.Key + ": name='" + mono.Str(kv.Value, "m_name") + "' local=" + (mono.M.I32(mono.FieldAddr(kv.Value, "m_local")) & 0xFF));
                n++;
            }
            Console.WriteLine(n + " entries in m_playerMap");
        }
    }
}
