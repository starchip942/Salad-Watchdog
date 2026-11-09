using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace GpuWatchdog
{
    /// <summary>Where everything lives: all inside Desktop\GPU-Watchdog, next to the exe. Nothing in AppData or the registry.</summary>
    static class Paths
    {
        public static readonly string AgentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Root = Directory.GetParent(AgentDir).FullName;
        public static readonly string Config = Path.Combine(Root, "config");
        public static readonly string Logs = Path.Combine(Root, "logs");
        public static readonly string Lib = Path.Combine(AgentDir, "lib");
        public static readonly string Panel = Path.Combine(AgentDir, "panel");
        public static string Cfg(string name) => Path.Combine(Config, name);

        public static void Ensure()
        {
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(Logs);
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
        public static string Write(object o) => S.Serialize(o);
        public static T Read<T>(string s) => S.Deserialize<T>(s);
        public static Dictionary<string, object> Obj(string s) => S.Deserialize<Dictionary<string, object>>(s);

        public static T Load<T>(string path) where T : new()
        {
            try { if (File.Exists(path)) return Read<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T(); }
            catch (Exception e) { Log.Warn("Couldn't read " + Path.GetFileName(path) + " (" + e.Message + "); using defaults.", true); }
            return new T();
        }

        /// <summary>Write via a temp file in the same folder, so a crash never leaves half a file.</summary>
        public static void Save(string path, object o)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Write(o), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        public static double? Num(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
        public static bool? Bool(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : (bool?)null;
    }

    static class Time
    {
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public static DateTime Local(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
        public static long Epoch(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>"Mon 7:00 AM", same style as the old watchdog</summary>
        public static string Short(long epoch) => Local(epoch).ToString("ddd h:mm tt", Inv);

        /// <summary>Windows' time zone as an IANA name for the website (e.g. "Turkey Standard Time" -> "Europe/Istanbul").</summary>
        public static string IanaZone()
        {
            var id = TimeZoneInfo.Local.Id;
            switch (id)
            {
                case "Turkey Standard Time": return "Europe/Istanbul";
                case "GMT Standard Time": return "Europe/London";
                case "W. Europe Standard Time": return "Europe/Berlin";
                case "Central Europe Standard Time": return "Europe/Budapest";
                case "Romance Standard Time": return "Europe/Paris";
                case "E. Europe Standard Time": return "Europe/Chisinau";
                case "GTB Standard Time": return "Europe/Bucharest";
                case "FLE Standard Time": return "Europe/Kiev";
                case "Russian Standard Time": return "Europe/Moscow";
                case "Eastern Standard Time": return "America/New_York";
                case "Central Standard Time": return "America/Chicago";
                case "Mountain Standard Time": return "America/Denver";
                case "Pacific Standard Time": return "America/Los_Angeles";
                case "UTC": return "UTC";
            }
            // Fallback the browser understands: a fixed offset zone ("Etc/GMT-3" means UTC+3)
            var h = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours);
            return h == 0 ? "UTC" : "Etc/GMT" + (h > 0 ? "-" : "+") + Math.Abs(h);
        }
        public static int OffsetMin() => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
    }

    static class Proc
    {
        /// <summary>Runs a console tool hidden and returns its output (stdout + stderr), or null on timeout.</summary>
        public static string Run(string exe, string args, int timeoutMs = 10000, Encoding enc = null)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                if (enc != null) { psi.StandardOutputEncoding = enc; psi.StandardErrorEncoding = enc; }
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                    return outTask.Result + errTask.Result;
                }
            }
            catch (Exception e) { return "ERROR: " + e.Message; }
        }

        public static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
    }

    /// <summary>Keeps an app in the background: any of its windows that pops up in the next few seconds is put back in the tray.</summary>
    static class WindowGuard
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);

        public static HashSet<IntPtr> Visible(params string[] processNames)
        {
            var pids = new HashSet<uint>();
            foreach (var n in processNames) foreach (var p in Process.GetProcessesByName(n)) pids.Add((uint)p.Id);
            var set = new HashSet<IntPtr>();
            EnumWindows((h, l) => { GetWindowThreadProcessId(h, out uint pid); if (pids.Contains(pid) && IsWindowVisible(h) && GetWindowTextLength(h) > 0) set.Add(h); return true; }, IntPtr.Zero);
            return set;
        }

        /// <summary>For `seconds`, hide any window of these apps that wasn't showing when we started.</summary>
        public static void HideNew(int seconds, params string[] processNames)
        {
            var before = Visible(processNames);
            System.Threading.Tasks.Task.Run(() =>
            {
                var until = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < until)
                {
                    System.Threading.Thread.Sleep(300);
                    foreach (var h in Visible(processNames))
                    {
                        if (before.Contains(h)) continue;
                        // you clicked the tray icon / a shortcut yourself: leave that window alone and stop guarding
                        if (SaladPeek.MouseOnTaskbar()) return;
                        ShowWindow(h, 0);   // SW_HIDE: back to the tray
                    }
                }
            });
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Full path of a running program, or null if Windows won't say (protected processes). Read-only.</summary>
        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        public static uint LastInputTick() { var i = new LASTINPUTINFO { cbSize = 8 }; return GetLastInputInfo(ref i) ? i.dwTime : 0; }
    }
}
