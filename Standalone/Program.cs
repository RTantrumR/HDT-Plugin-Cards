using System;
using System.IO;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Toolkit.Standalone
{
    /// <summary>
    /// Standalone vertical slice: reads the Hearthstone client with our own memory reader and draws
    /// names, MMR, tier, minion types and the lobby's tribes over the game. Tray icon → Exit.
    /// Log: standalone.log next to the exe.
    /// </summary>
    internal static class Program
    {
        private static string _logPath;
        private static readonly object LogLock = new object();

        public static void Log(string line)
        {
            lock (LogLock)
                try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine); } catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool fresh;
            using (var mutex = new Mutex(true, "TantrumsBattlegroundsToolkit.Standalone", out fresh))
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                _logPath = Path.Combine(dir, "standalone.log");
                if (!fresh) { Log("=== refused: another instance is running (args: " + string.Join(" ", args) + ")"); return 1; }
                bool ignoreFocus = Array.IndexOf(args, "--ignore-focus") >= 0;
                Log("=== start" + (ignoreFocus ? " (TEST MODE: --ignore-focus)" : ""));

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var overlay = new OverlayWindow(dir) { IgnoreFocus = ignoreFocus, Log = Log };
                var reader = new GameReader(Log);
                var mmr = new MmrTable(Log);

                var tray = new Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = ignoreFocus ? "Tantrum's BG Toolkit - TEST MODE (draws without focus)" : "Tantrum's BG Toolkit (standalone test)",
                    Visible = true,
                    ContextMenuStrip = new Forms.ContextMenuStrip()
                };
                tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => app.Shutdown());

                // Reader thread: one read a second, results handed to the UI thread.
                var stop = new ManualResetEvent(false);
                var worker = new Thread(() =>
                {
                    string lastSummary = null;
                    while (!stop.WaitOne(1000))
                    {
                        var snap = reader.Read();
                        if (snap?.Region != null) mmr.Ensure(snap.Region);
                        string summary = Summary(snap);
                        if (summary != lastSummary) { Log(summary); lastSummary = summary; }
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            overlay.Update(snap, mmr);
                            // Test aid: drop a file named snap.trigger next to the exe to get snap.png.
                            string trigger = Path.Combine(dir, "snap.trigger");
                            if (File.Exists(trigger))
                            {
                                try { File.Delete(trigger); } catch { }
                                Log("snap: " + overlay.SaveComposite(Path.Combine(dir, "snap.png")));
                            }
                        }));
                    }
                }) { IsBackground = true, Name = "reader" };

                app.Startup += (s, e) => worker.Start();
                app.Exit += (s, e) => { stop.Set(); tray.Visible = false; tray.Dispose(); reader.Dispose(); Log("=== exit"); };
                app.Run();
                return 0;
            }
        }

        // One line per change, so the log shows what the overlay was given.
        private static string Summary(Snapshot s)
        {
            if (s == null) return "no read";
            var sb = new System.Text.StringBuilder("me=" + s.Me + " region=" + s.Region + " lobbyTribes=[" + string.Join(",", s.LobbyTribes) + "]");
            foreach (var t in s.Tiles)
            {
                int c; string tribe = HsbgCardLookup.Data.TribeTally.Dominant(t.Races, out c);
                sb.Append(" | p" + t.PlayerId + " " + (t.Name ?? "?") + " T" + t.Tier + " " + tribe + (c > 0 ? c.ToString() : ""));
            }
            return sb.ToString();
        }
    }
}
