using System;
using System.Diagnostics;

namespace MemSpike
{
    /// <summary>
    /// Spike: read the Hearthstone client's managed objects with nothing but ReadProcessMemory.
    ///   MemSpike info            attach, print the discovered runtime layout
    ///   MemSpike me              own battletag (works from the main menu)
    ///   MemSpike lobby           the match's players: id, name, hero card (needs a game in progress)
    ///   MemSpike class &lt;Name&gt;    fields a class declares
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
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

        // GameState.s_instance.m_playerInfoMap : Map<int, SharedPlayerInfo { m_name, m_playerHero }>
        private static void Lobby(Mono mono)
        {
            long gs = mono.StaticObj("GameState", "s_instance");
            if (gs == 0) { Console.WriteLine("no game in progress (GameState.s_instance is null)"); return; }
            int n = 0;
            foreach (var kv in mono.IntMap(mono.Obj(gs, "m_playerInfoMap")))
            {
                long hero = mono.Obj(kv.Value, "m_playerHero");
                Console.WriteLine("player " + kv.Key + ": name='" + mono.Str(kv.Value, "m_name") + "' hero=" + (mono.Str(hero, "m_cardIdInternal") ?? "(none)"));
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
