using System;

namespace Toolkit.Standalone
{
    /// <summary>
    /// Tantrum's Battlegrounds Toolkit without HDT. Two modes, one exe:
    ///   (no arguments)  the toolkit, until Exit — what the desktop / Start menu shortcut starts
    ///   --with-hs       the toolkit, closing when Hearthstone closes — what the watcher starts
    ///   --autostart     the watcher: started with Windows, idles in the tray until Hearthstone starts
    /// Main only dispatches; the watcher never touches WPF, so its process stays small.
    /// Log: %APPDATA%\TantrumsBattlegroundsToolkit\standalone.log.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--autostart") >= 0) return Watcher.Run();
            return ToolkitHost.Run(args);
        }
    }
}
