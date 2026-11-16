using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using HsbgCardLookup.Config;
using HsbgCardLookup.Localization;

namespace Toolkit.Standalone
{
    /// <summary>
    /// What both of the exe's modes need, with no WPF in sight (the idle watcher must stay small):
    /// names shared with the Setup wizard, the data folder, the log, autostart, and the checks for
    /// Hearthstone, the toolkit and the HDT plugin.
    /// </summary>
    internal static class Shared
    {
        // Names the Setup wizard uses too — change them in both places.
        public const string ToolkitMutex = "TantrumsBattlegroundsToolkit.Standalone";
        public const string WatcherMutex = "TantrumsBattlegroundsToolkit.Watcher";
        public const string QuitEvent = "TantrumsBattlegroundsToolkit.Quit";      // Setup sets it: every instance exits
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "TantrumsBattlegroundsToolkit";
        public const string DataFolderName = "TantrumsBattlegroundsToolkit";

        public static string ExePath => Process.GetCurrentProcess().MainModule.FileName;
        public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory;

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);

        private static readonly object LogLock = new object();
        public static string LogTag = "";

        public static void Log(string line)
        {
            lock (LogLock)
                try
                {
                    Directory.CreateDirectory(DataDir);
                    File.AppendAllText(Path.Combine(DataDir, "standalone.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + LogTag + line + Environment.NewLine);
                }
                catch { }
        }

        /// <summary>Point the toolkit at the standalone's own data folder; on its first run there, take
        /// the HDT plugin's settings and caches along once (nothing that is already there is replaced).</summary>
        public static void UseOwnDataFolder()
        {
            PluginConfig.DataDirOverride = DataDir;
            try
            {
                string from = PluginConfig.PluginDataDir;
                if (File.Exists(Path.Combine(DataDir, "config.xml")) || !Directory.Exists(from)) return;
                int files = 0;
                foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(from.Length).TrimStart('\\');
                    if (rel.StartsWith(@"uninstall\", StringComparison.OrdinalIgnoreCase)
                        || rel.Equals("spike.log", StringComparison.OrdinalIgnoreCase)
                        || rel.Equals("setup-language.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(DataDir, rel);
                    if (File.Exists(dst)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst);
                    files++;
                }
                Log("first run: copied " + files + " files from the HDT plugin's folder " + from);
            }
            catch (Exception ex) { Log("copy from the HDT plugin's folder failed: " + ex.Message); }
        }

        /// <summary>The plugin's own texts in the configured language (English when it follows a host,
        /// since the standalone has none).</summary>
        public static void LoadStrings(string language)
        {
            Strings.Load();
            Strings.SetLocale(string.IsNullOrEmpty(language) ? Locales.English : language);
        }

        /// <summary>config.xml's Language ("" when unset or unreadable), read as text.</summary>
        public static string ConfiguredLanguage()
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(Path.Combine(DataDir, "config.xml")), "<Language>([^<]*)</Language>");
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            catch { return ""; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        /// <summary>After start-up work an idle process no longer needs: collect, and hand the pages back.</summary>
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }

        public static bool HearthstoneRunning() => Process.GetProcessesByName("Hearthstone").Length > 0;

        public static int HearthstonePid()
        {
            var p = Process.GetProcessesByName("Hearthstone");
            return p.Length > 0 ? p[0].Id : 0;
        }

        public static bool Exists(string mutexName)
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(mutexName, out m)) return false;
            m.Dispose();
            return true;
        }

        // ── autostart ────────────────────────────────────────────────────────────────────────────
        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(RunValue) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunValue, "\"" + ExePath + "\" --autostart");
                    else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue);
                }
                Log("start with Windows: " + value);
            }
        }

        // ── the HDT plugin, for the both-running warning ─────────────────────────────────────────
        /// <summary>HDT is running and has this toolkit's plugin switched on (its plugins.xml).</summary>
        public static bool HdtPluginActive()
        {
            if (Process.GetProcessesByName("HearthstoneDeckTracker").Length == 0) return false;
            try
            {
                string xml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HearthstoneDeckTracker", "plugins.xml");
                if (!File.Exists(xml)) return false;
                return XDocument.Load(xml).Descendants("PluginSettings").Any(p =>
                    ((string)p.Element("FileName") ?? "").IndexOf("HsbgCardLookup", StringComparison.OrdinalIgnoreCase) >= 0
                    && (string)p.Element("IsEnabled") == "true");
            }
            catch { return false; }
        }

        /// <summary>The Setup wizard's "close down" signal, opened (or made) and reset for this run.</summary>
        public static EventWaitHandle OpenQuitSignal()
        {
            var e = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEvent);
            e.Reset();
            return e;
        }

        public static System.Drawing.Icon TrayIcon()
        {
            try { return new System.Drawing.Icon(Path.Combine(ExeDir, "toolkit.ico"), System.Windows.Forms.SystemInformation.SmallIconSize); }
            catch { return System.Drawing.SystemIcons.Application; }
        }
    }
}
