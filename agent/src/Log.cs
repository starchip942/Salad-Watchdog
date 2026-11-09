using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GpuWatchdog
{
    public class LogEvent
    {
        public string id; public long t; public string level; public string msg;
        public string cat;   // "pc" (temperatures, safety, power), "salad" (Salad's state, games), "earnings"
        [System.Web.Script.Serialization.ScriptIgnore] public string Kind;   // "private" never leaves the PC; "earnings" follows the website setting
    }

    /// <summary>
    /// One log, two views. Same mechanic as the old GPU Watchdog: one line per event in plain text,
    /// "MM/dd/yyyy h:mm tt: message", a dashed line between sessions, a 15-minute GPU temperature average,
    /// and a note when the previous session ended without a closing line. Kept under ~5 MB by rotating.
    /// </summary>
    static class Log
    {
        public static string FilePath => Path.Combine(Paths.Logs, "pc.log");
        static string FileFor(string cat) => Path.Combine(Paths.Logs, (cat ?? "pc") + ".log");
        public static readonly string[] Cats = { "pc", "salad", "earnings" };
        const long RotateBytes = 2 * 1024 * 1024;
        static readonly object Gate = new object();
        static readonly List<LogEvent> Recent = new List<LogEvent>();
        static long seq;
        public static event Action<LogEvent> Added;

        public static void StartSession(string startLine, bool afterUpdate = false)
        {
            if (afterUpdate) { MigrateOld(); LoadTail(); Debug("Restarted into the new version."); return; }   // same session continues
            string last = null;
            try
            {
                if (File.Exists(FilePath))
                    last = File.ReadLines(FilePath, Encoding.UTF8).Where(l => l.Trim().Length > 0).LastOrDefault();
                MigrateOld();
            }
            catch { }
            LoadTail();
            if (last != null) Append("pc", new string('-', 60));
            Info(startLine);
            if (last != null && !last.StartsWith("----------") && !last.Contains("Watchdog closed") && !last.Contains("Watchdog stopped"))
                Warn("The previous session has no closing time, so it ended unexpectedly (power cut, crash or force-closed).");
        }

        public static LogEvent Info(string msg, bool priv = false) => Add("info", msg, priv ? "private" : null);
        public static LogEvent Warn(string msg, bool priv = false) => Add("warn", msg, priv ? "private" : null);
        public static LogEvent Error(string msg, bool priv = false) => Add("error", msg, priv ? "private" : null);
        public static LogEvent Earnings(string msg) => Add("info", msg, "earnings");
        /// <summary>Small things (button presses, connection blips): kept in logs\debug.log only, never shown in the panel or website.</summary>
        public static void Debug(string msg) { lock (Gate) Append("debug", DateTime.Now.ToString("MM/dd/yyyy h:mm tt", Time.Inv) + ": " + msg); }

        static string CatOf(string msg, string kind)
        {
            if (kind == "earnings") return "earnings";
            var m = msg.ToLowerInvariant();
            if (m.Contains("payout") || m.Contains("balance")) return "earnings";
            if ((m.StartsWith("salad") || m.StartsWith("games closed") || m.StartsWith("close salad")) && !m.Contains("°c")) return "salad";
            if (m.Contains("emergency") || m.Contains("lockout") || m.Contains("locked out") || m.Contains("°c") || m.Contains("temperature") || m.Contains("watchdog") || m.Contains("pc ")) return "pc";
            if (m.Contains("salad") || m.Contains("chop") || m.Contains("game") || m.Contains(" started (") || m.Contains(" closed")) return "salad";
            return "pc";
        }

        /// <summary>One-time: the earlier single events.log becomes the start of the PC log.</summary>
        static void MigrateOld()
        {
            try
            {
                var old = Path.Combine(Paths.Logs, "events.log");
                if (File.Exists(old) && !File.Exists(FilePath)) File.Move(old, FilePath);
            }
            catch { }
        }

        public static LogEvent Add(string level, string msg, string kind = null, DateTime? when = null, string whenText = null)
        {
            var now = when ?? DateTime.Now;
            var e = new LogEvent { t = Time.Epoch(now), level = level, msg = msg, Kind = kind, cat = CatOf(msg, kind) };
            lock (Gate)
            {
                e.id = Time.Now.ToString() + "-" + (++seq).ToString();
                Recent.Add(e);
                if (Recent.Count > 2000) Recent.RemoveRange(0, Recent.Count - 2000);
                var prefix = level == "warn" ? "Warning: " : level == "error" ? "Error: " : "";
                Append(e.cat, (whenText ?? now.ToString("MM/dd/yyyy h:mm tt", Time.Inv)) + ": " + prefix + msg);
            }
            try { Added?.Invoke(e); } catch { }
            return e;
        }

        static void Append(string cat, string line)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                var file = FileFor(cat);
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > RotateBytes / 2)
                {
                    var old = Path.Combine(Paths.Logs, cat + ".1.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, line + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>Last events of earlier sessions, so the panel and website show history after a restart.</summary>
        static void LoadTail()
        {
            try
            {
              foreach (var cat in Cats)
              {
                var file = FileFor(cat);
                if (!File.Exists(file)) continue;
                var lines = File.ReadLines(file, Encoding.UTF8).Reverse().Take(400).Reverse();
                foreach (var l in lines)
                {
                    int i = l.IndexOf(": ", StringComparison.Ordinal);
                    if (i < 10 || l.StartsWith("---")) continue;
                    var stamp = l.Substring(0, i);
                    DateTime dt;
                    if (!DateTime.TryParseExact(stamp, "MM/dd/yyyy h:mm tt", Time.Inv, System.Globalization.DateTimeStyles.None, out dt))
                    {
                        // "10/30/2026 1:45-2:00 AM" average lines: use the end time
                        var parts = stamp.Split(' ');
                        if (parts.Length != 3 || !DateTime.TryParseExact(parts[0] + " " + parts[1].Split('-').Last() + " " + parts[2], "MM/dd/yyyy h:mm tt", Time.Inv, System.Globalization.DateTimeStyles.None, out dt)) continue;
                    }
                    var msg = l.Substring(i + 2);
                    var level = msg.StartsWith("Warning: ") ? "warn" : msg.StartsWith("Error: ") ? "error" : "info";
                    if (level != "info") msg = msg.Substring(msg.IndexOf(' ') + 1);
                    if (Trivial(msg)) continue;   // lines from older versions that are now debug-only
                    var c = cat == "pc" ? CatOf(msg, null) : cat;
                    var kind = IsPrivate(msg) ? "private" : c == "earnings" ? "earnings" : null;
                    Recent.Add(new LogEvent { id = "f" + Time.Epoch(dt) + "-" + (++seq), t = Time.Epoch(dt), level = level, msg = msg, Kind = kind, cat = c });
                }
              }
              Recent.Sort((a, b) => a.t.CompareTo(b.t));
            }
            catch { }
        }

        static readonly string[] TrivialStarts = { "Earnings read", "Relay ", "Connected to the relay", "Can't reach the relay", "Lights", "Screen off", "Created the GPU Watchdog shortcut", "Website: ", "Panel: ", "Hardware check written", "Watchdog closed for the update", "Couldn't read earnings", "Sensors:", "Watchdog started (new version)", "A new version", "Website relay set", "Website password synced" };
        static bool Trivial(string msg) => TrivialStarts.Any(t => msg.StartsWith(t, StringComparison.OrdinalIgnoreCase)) || msg.Contains("°C average");

        static bool IsPrivate(string msg)
        {
            var m = msg.ToLowerInvariant();
            return m.Contains("password") || m.Contains("token") || m.Contains("session") || m.Contains("signature") || m.Contains("agent key");
        }

        public static List<LogEvent> All() { lock (Gate) return Recent.ToList(); }

        /// <summary>What the website may see: never password/token/session lines; earnings only when allowed.</summary>
        public static bool Public(LogEvent e) => e.Kind != "private" && !IsPrivate(e.msg) && (e.Kind != "earnings" || Conf.S.ShowEarningsOnWebsite);

        // ----- 15-minute GPU temperature average, like the old watchdog -----
        static DateTime periodStart = DateTime.Now;
        static double sum; static int count; static double max;
        public static void TempSample(double? c)
        {
            if (c == null) return;
            sum += c.Value; count++; if (c.Value > max) max = c.Value;
            var end = periodStart.AddMinutes(15);
            if (DateTime.Now < end) return;
            var startFmt = periodStart.ToString("tt", Time.Inv) == end.ToString("tt", Time.Inv) ? "h:mm" : "h:mm tt";
            var when = periodStart.ToString("MM/dd/yyyy", Time.Inv) + " " + periodStart.ToString(startFmt, Time.Inv) + "-" + end.ToString("h:mm tt", Time.Inv);
            if (false) Add("info", string.Format(Time.Inv, "{0}°C average (peak {1}°C)", Math.Round(sum / count, MidpointRounding.AwayFromZero), Math.Round(max)), null, end, when);
            periodStart = DateTime.Now >= end.AddMinutes(15) ? DateTime.Now : end;
            sum = 0; count = 0; max = 0;
        }
    }
}
