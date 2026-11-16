using System;
using System.Diagnostics;
using System.Threading;
using Forms = System.Windows.Forms;

namespace Toolkit.Standalone
{
    /// <summary>
    /// The --autostart mode: started with Windows, it only waits. A tray icon, a timer, no card data and
    /// no WPF. When Hearthstone starts it launches the toolkit (--with-hs), which closes itself again
    /// when Hearthstone does. Its own icon hides while the toolkit (with its own icon) runs.
    /// </summary>
    internal static class Watcher
    {
        public static int Run()
        {
            bool fresh;
            using (var mutex = new Mutex(true, Shared.WatcherMutex, out fresh))
            {
                if (!fresh) return 1;
                Shared.LogTag = "[watcher] ";
                Shared.UseOwnDataFolder();
                // Only the language from config.xml, not the whole settings object: XmlSerializer
                // alone would cost the idle process several MB. Likewise only the three tray texts.
                var text = Shared.TrayStrings(Shared.ConfiguredLanguage(),
                    "standalone.tray.waiting", "standalone.tray.startNow", "standalone.tray.exit");
                Shared.Log("waiting for Hearthstone");

                var quit = Shared.OpenQuitSignal();
                var tray = new Forms.NotifyIcon
                {
                    Icon = Shared.TrayIcon(),
                    Text = Trim(text["standalone.tray.waiting"]),
                    Visible = true,
                    ContextMenuStrip = new Forms.ContextMenuStrip()
                };
                int launchedFor = 0;   // the Hearthstone process the toolkit was last started for
                bool trimmed = false;
                Action launch = () =>
                {
                    try { Process.Start(Shared.ExePath, "--with-hs"); Shared.Log("started the toolkit"); }
                    catch (Exception ex) { Shared.Log("could not start the toolkit: " + ex.Message); }
                };
                tray.ContextMenuStrip.Items.Add(text["standalone.tray.startNow"], null, (s, e) => launch());
                tray.ContextMenuStrip.Items.Add(text["standalone.tray.exit"], null, (s, e) => Forms.Application.Exit());

                var timer = new Forms.Timer { Interval = 3000 };
                timer.Tick += (s, e) =>
                {
                    if (quit.WaitOne(0)) { Forms.Application.Exit(); return; }
                    bool toolkitUp = Shared.Exists(Shared.ToolkitMutex);
                    tray.Visible = !toolkitUp;
                    int hs;
                    HsWindow.Find(out hs);
                    // Once per Hearthstone run: a toolkit the player closed stays closed until the next.
                    if (hs != 0 && !toolkitUp && hs != launchedFor) { launchedFor = hs; launch(); trimmed = false; }
                    // Once everything idle-time has run (start-up, the tray, a launch): give the memory back.
                    if (!trimmed) { trimmed = true; Shared.TrimMemory(); }
                };
                timer.Start();
                Forms.Application.Run();
                timer.Stop();
                tray.Visible = false;
                tray.Dispose();
                Shared.Log("exit");
                return 0;
            }
        }

        // NotifyIcon.Text throws past 63 characters.
        private static string Trim(string s) => s.Length > 63 ? s.Substring(0, 63) : s;
    }
}
