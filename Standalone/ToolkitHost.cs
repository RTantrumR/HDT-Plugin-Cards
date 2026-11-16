using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HsbgCardLookup;
using HsbgCardLookup.Config;
using HsbgCardLookup.Host;
using HsbgCardLookup.Localization;
using Forms = System.Windows.Forms;

namespace Toolkit.Standalone
{
    /// <summary>
    /// The toolkit mode: our memory reader feeds the host seams, our own window over Hearthstone is the
    /// overlay surface, and the plugin's whole ToolkitApp runs on top, unchanged. Started by hand (a
    /// shortcut) it stays until Exit; started by the watcher (--with-hs) it closes when Hearthstone does.
    /// </summary>
    internal static class ToolkitHost
    {
        public static int Run(string[] args)
        {
            bool fresh;
            using (var mutex = new Mutex(true, Shared.ToolkitMutex, out fresh))
            {
                Shared.UseOwnDataFolder();
                if (!fresh) { Shared.Log("=== refused: another instance is running (args: " + string.Join(" ", args) + ")"); return 1; }
                bool ignoreFocus = Array.IndexOf(args, "--ignore-focus") >= 0;
                bool withHs = Array.IndexOf(args, "--with-hs") >= 0;
                Shared.Log("=== start" + (withHs ? " (with Hearthstone)" : "") + (ignoreFocus ? " (TEST MODE: --ignore-focus)" : ""));
                Action<string> log = Shared.Log;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var ui = app.Dispatcher;
                var overlay = new OverlayWindow { IgnoreFocus = ignoreFocus, Log = log };

                // The host seams, then the plugin's own pieces exactly as its OnLoad builds them.
                var game = new StandaloneGameState();
                HostServices.Use(game, new StandaloneMemory(game), new StandaloneOverlay(overlay), new StandaloneShell());

                // Three parts, as the plugin reports it: the updater compares and stores this string.
                var v = typeof(ToolkitApp).Assembly.GetName().Version;
                var toolkit = new ToolkitApp(new Version(v.Major, v.Minor, v.Build), log);

                var reader = new GameReader(log);
                var tray = new Forms.NotifyIcon
                {
                    Icon = Shared.TrayIcon(),
                    Text = ignoreFocus ? "TEST MODE (draws without focus)" : ToolkitApp.DisplayName,
                    Visible = true,
                    ContextMenuStrip = new Forms.ContextMenuStrip()
                };
                Forms.ToolStripItem searchItem = null, settingsItem = null, exitItem = null;
                Action relabel = () =>
                {
                    searchItem.Text = Strings.T("plugin.menu.openOverlay");
                    settingsItem.Text = Strings.T("plugin.settings");
                    exitItem.Text = Strings.T("standalone.tray.exit");
                };
                searchItem = tray.ContextMenuStrip.Items.Add("", null, (s, e) => toolkit.ToggleSearch());
                settingsItem = tray.ContextMenuStrip.Items.Add("", null, (s, e) => ui.BeginInvoke(new Action(toolkit.OpenSettings)));
                exitItem = tray.ContextMenuStrip.Items.Add("", null, (s, e) => app.Shutdown());
                tray.DoubleClick += (s, e) => toolkit.ToggleSearch();
                toolkit.LanguageChanged += relabel;

                // Setup's "close down" signal.
                var quit = Shared.OpenQuitSignal();
                var stop = new ManualResetEvent(false);
                var quitWatch = new Thread(() =>
                {
                    if (WaitHandle.WaitAny(new WaitHandle[] { quit, stop }) == 0)
                    {
                        Shared.Log("Setup asked the toolkit to close");
                        ui.BeginInvoke(new Action(app.Shutdown));
                    }
                }) { IsBackground = true, Name = "quit" };

                // Reader thread: a snapshot every 250 ms during a Battlegrounds game, handed to the UI thread;
                // every second otherwise (menus, other modes, no Hearthstone), when it only watches for one.
                var worker = new Thread(() =>
                {
                    string lastSummary = null;
                    int wait = 250;
                    while (!stop.WaitOne(wait))
                    {
                        var snap = reader.Read();
                        wait = snap != null && snap.IsBattlegrounds ? 250 : 1000;
                        string summary = Summary(snap);
                        if (summary != lastSummary) { Shared.Log(summary); lastSummary = summary; }
                        ui.BeginInvoke(new Action(() => game.Publish(snap)));
                    }
                }) { IsBackground = true, Name = "reader" };

                // The toolkit's tick, every 100 ms on the UI thread — what HDT's OnUpdate does for the plugin.
                // Every 5 s also: close with Hearthstone (when the watcher started us), and warn once per
                // HDT run when HDT has this toolkit's plugin on too (two of everything otherwise).
                DateTime lastSlow = DateTime.MinValue, hsGoneSince = DateTime.MinValue, trimAt = DateTime.UtcNow.AddSeconds(30);
                bool sawHs = false, wasBg = false;
                int warnedForHdt = 0;
                // The stream view for OBS follows its setting; closing the window switches the setting off.
                StreamView stream = null;
                Action syncStream = () =>
                {
                    var cfg = toolkit.Config;
                    bool want = cfg != null && cfg.ShowStreamView;
                    if (want && stream == null)
                    {
                        stream = new StreamView(overlay, cfg.StreamViewKey);
                        var opened = stream;
                        stream.Closed += (s2, e2) =>
                        {
                            if (stream == opened) stream = null;
                            if (!opened.UserClosed) return;
                            cfg.ShowStreamView = false;
                            try { cfg.Save(); } catch { }
                            Shared.Log("stream view closed by its window: switched off");
                        };
                        stream.Show();
                        Shared.Log("stream view opened (" + cfg.StreamViewKey + ")");
                    }
                    else if (!want && stream != null)
                    {
                        var s0 = stream; stream = null; s0.Close();
                        Shared.Log("stream view closed");
                    }
                    else if (stream != null) stream.SetKey(cfg.StreamViewKey);
                };

                var poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(100) };
                poll.Tick += (s, e) =>
                {
                    try { toolkit.Update(); } catch (Exception ex) { Shared.Log("ToolkitApp.Update: " + ex.Message); }
                    try { syncStream(); } catch (Exception ex) { Shared.Log("stream view: " + ex.Message); }
                    if ((DateTime.UtcNow - lastSlow).TotalSeconds < 5) return;
                    lastSlow = DateTime.UtcNow;

                    // Give memory back once start-up has settled, and again a little after each
                    // Battlegrounds game (its snapshots and the overlay's match elements are garbage then).
                    bool bg = game.IsBattlegroundsMatch;
                    if (wasBg && !bg) trimAt = DateTime.UtcNow.AddSeconds(10);
                    wasBg = bg;
                    if (trimAt != DateTime.MinValue && DateTime.UtcNow >= trimAt && !bg)
                    {
                        trimAt = DateTime.MinValue;
                        Shared.TrimMemory();
                        Shared.Log("memory trimmed: " + (GC.GetTotalMemory(false) >> 20) + " MB managed");
                    }

                    if (withHs)
                    {
                        if (Shared.HearthstoneRunning()) { sawHs = true; hsGoneSince = DateTime.MinValue; }
                        else if (sawHs)
                        {
                            if (hsGoneSince == DateTime.MinValue) hsGoneSince = DateTime.UtcNow;
                            else if ((DateTime.UtcNow - hsGoneSince).TotalSeconds >= 10) { Shared.Log("Hearthstone closed: closing too"); app.Shutdown(); return; }
                        }
                    }

                    int hdtPid = Shared.ProcessId("HearthstoneDeckTracker.exe");
                    if (hdtPid != 0 && hdtPid != warnedForHdt && Shared.HdtPluginActive())
                    {
                        warnedForHdt = hdtPid;
                        Shared.Log("both running: HDT has the toolkit's plugin on");
                        tray.ShowBalloonTip(15000, ToolkitApp.DisplayName, Strings.T("standalone.bothRunning"), Forms.ToolTipIcon.Warning);
                    }

                    // Test aid: drop a file named snap.trigger next to the exe to get snap.png.
                    string trigger = Path.Combine(Shared.ExeDir, "snap.trigger");
                    if (File.Exists(trigger))
                    {
                        try { File.Delete(trigger); } catch { }
                        Shared.Log("snap: " + overlay.SaveComposite(Path.Combine(Shared.ExeDir, "snap.png")));
                    }
                };

                app.Startup += (s, e) => { toolkit.Start(ui); relabel(); worker.Start(); quitWatch.Start(); poll.Start(); };
                app.Exit += (s, e) =>
                {
                    stop.Set(); poll.Stop();
                    try { toolkit.Stop(); } catch (Exception ex) { Shared.Log("ToolkitApp.Stop: " + ex.Message); }
                    tray.Visible = false; tray.Dispose(); reader.Dispose(); Shared.Log("=== exit");
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
