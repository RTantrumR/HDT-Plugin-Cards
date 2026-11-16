using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

        public static string ExePath => System.Reflection.Assembly.GetEntryAssembly().Location;
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

        /// <summary>
        /// A few of the plugin's texts for the watcher's tray, in the configured language, else English,
        /// else the key. Read straight out of <c>data\ui\&lt;lang&gt;.json</c> without Json.NET or the plugin's
        /// string layer: loading all 13 catalogs through them cost the idle watcher ~3 MB.
        /// </summary>
        public static Dictionary<string, string> TrayStrings(string language, params string[] keys)
        {
            var found = new Dictionary<string, string>();
            foreach (var lang in new[] { language, Locales.English })
            {
                if (string.IsNullOrEmpty(lang) || found.Count == keys.Length) continue;
                string json;
                try { json = File.ReadAllText(Path.Combine(ExeDir, "data", "ui", lang + ".json")); }
                catch { continue; }
                foreach (var key in keys)
                {
                    if (found.ContainsKey(key)) continue;
                    var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (m.Success) found[key] = JsonUnescape(m.Groups[1].Value);
                }
            }
            foreach (var key in keys) if (!found.ContainsKey(key)) found[key] = key;
            return found;
        }

        private static string JsonUnescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code;
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        { sb.Append((char)code); i += 4; }
                        else sb.Append('u');
                        break;
                    default: sb.Append(e); break;     // \" \\ \/
                }
            }
            return sb.ToString();
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

        /// <summary>After work the process no longer needs the memory of (start-up, a finished match):
        /// collect, compact the large-object heap once, and hand the pages back to Windows.</summary>
        public static void TrimMemory()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }

        public static bool HearthstoneRunning() => HsWindow.Find() != IntPtr.Zero;

        // ── is an exe running? (Toolhelp: processes only — no per-thread data, unlike Process.GetProcessesByName)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>The id of a running process with this exe name (e.g. "HearthstoneDeckTracker.exe"), else 0.</summary>
        public static int ProcessId(string exeName)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return 0;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    if (string.Equals(e.szExeFile, exeName, StringComparison.OrdinalIgnoreCase)) return (int)e.th32ProcessID;
                return 0;
            }
            finally { CloseHandle(snap); }
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
            if (ProcessId("HearthstoneDeckTracker.exe") == 0) return false;
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
