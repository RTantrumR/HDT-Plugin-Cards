using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HsbgCardLookup;
using HsbgCardLookup.Host;
using Forms = System.Windows.Forms;

namespace Toolkit.Standalone
{
    /// <summary>
    /// The toolkit without HDT: our memory reader feeds the host seams, our own window over Hearthstone
    /// is the overlay surface, and the plugin's whole ToolkitApp runs on top, unchanged — F3 search,
    /// the in-match features, settings, data/art refresh, update notices. Settings come from the
    /// plugin's own config.xml. Tray icon → Card search / Settings / Exit. Log: standalone.log.
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
                var ui = app.Dispatcher;
                var overlay = new OverlayWindow { IgnoreFocus = ignoreFocus, Log = Log };

                // The host seams, then the plugin's own pieces exactly as its OnLoad builds them.
                var game = new StandaloneGameState();
                HostServices.Use(game, new StandaloneMemory(game), new StandaloneOverlay(overlay), new StandaloneShell());

                // Three parts, as the plugin reports it: the updater compares and stores this string.
                var v = typeof(ToolkitApp).Assembly.GetName().Version;
                var toolkit = new ToolkitApp(new Version(v.Major, v.Minor, v.Build), Log);

                var reader = new GameReader(Log);
                var tray = new Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = ignoreFocus ? "Tantrum's BG Toolkit - TEST MODE (draws without focus)" : "Tantrum's BG Toolkit (standalone test)",
                    Visible = true,
                    ContextMenuStrip = new Forms.ContextMenuStrip()
                };
                tray.ContextMenuStrip.Items.Add("Card search", null, (s, e) => toolkit.ToggleSearch());
                tray.ContextMenuStrip.Items.Add("Settings", null, (s, e) => ui.BeginInvoke(new Action(toolkit.OpenSettings)));
                tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => app.Shutdown());

                // Reader thread: a snapshot every 250 ms, handed to the UI thread.
                var stop = new ManualResetEvent(false);
                var worker = new Thread(() =>
                {
                    string lastSummary = null;
                    while (!stop.WaitOne(250))
                    {
                        var snap = reader.Read();
                        string summary = Summary(snap);
                        if (summary != lastSummary) { Log(summary); lastSummary = summary; }
                        ui.BeginInvoke(new Action(() => game.Publish(snap)));
                    }
                }) { IsBackground = true, Name = "reader" };

                // The toolkit's tick, every 100 ms on the UI thread — what HDT's OnUpdate does for the plugin.
                var poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(100) };
                poll.Tick += (s, e) =>
                {
                    try { toolkit.Update(); } catch (Exception ex) { Log("ToolkitApp.Update: " + ex.Message); }
                    // Test aid: drop a file named snap.trigger next to the exe to get snap.png.
                    string trigger = Path.Combine(dir, "snap.trigger");
                    if (File.Exists(trigger))
                    {
                        try { File.Delete(trigger); } catch { }
                        Log("snap: " + overlay.SaveComposite(Path.Combine(dir, "snap.png")));
                    }
                };

                app.Startup += (s, e) => { toolkit.Start(ui); worker.Start(); poll.Start(); };
                app.Exit += (s, e) =>
                {
                    stop.Set(); poll.Stop();
                    try { toolkit.Stop(); } catch (Exception ex) { Log("ToolkitApp.Stop: " + ex.Message); }
                    tray.Visible = false; tray.Dispose(); reader.Dispose(); Log("=== exit");
                };
                app.Run();
                return 0;
            }
        }

        private static int Turn(Snapshot s)
        {
            EntitySnap g; int t;
            return s.Entities.TryGetValue(s.GameEntityId, out g) && g.Tags.TryGetValue(20, out t) ? t : -1;   // TURN
        }

        // One line per change of what matters, so the log shows what the features were given.
        private static string Summary(Snapshot s)
        {
            if (s == null) return "no read";
            if (s.GameStateAddr == 0) return "region=" + s.Region + " no game";
            return "region=" + s.Region + " game 0x" + s.GameStateAddr.ToString("X") + " tiles=" + s.Tiles.Count
                + " duoTeams=" + s.TeamsWithTwo + " lobby=" + s.Lobby.Count + " tribes=[" + string.Join(",", s.LobbyTribes) + "]"
                + " entities~" + (s.Entities.Count / 25 * 25) + " turn=" + Turn(s) + " readMs~" + (s.ReadMs / 10 * 10);
        }
    }
}
