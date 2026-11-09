using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GpuWatchdog
{
    public class EarningsData
    {
        public double? balanceUSD, estNextHourLowUSD, estNextHourHighUSD, last24hUSD;
        public long updatedAt;
        public string nextHourFrom;
        public double? lastPayoutUSD;  // last time Salad credited your balance: how much
        public long lastPayoutAt;      // ...and when (epoch seconds)
    }

    /// <summary>
    /// Reads the numbers Salad shows on its Earn tab, read-only, through Windows' accessibility interface
    /// (the same thing screen readers use). No logins, tokens, network calls or memory reading.
    /// The small grey digits after the cents are extra decimals: "$3.47 31" means $3.4731.
    /// </summary>
    static class Earnings
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string Money = @"\$\s*(\d+\.\d{2})\s*(\d{1,2})?";

        /// <summary>Parses the Earn tab's visible text. Missing pieces stay null.</summary>
        public static EarningsData Parse(string text)
        {
            var d = new EarningsData();
            var t = Regex.Replace(text ?? "", @"\s+", " ");
            var m = Regex.Match(t, @"Your Balance\W*" + Money, RegexOptions.IgnoreCase);
            if (m.Success) d.balanceUSD = Val(m.Groups[1].Value, m.Groups[2].Value);
            m = Regex.Match(t, @"Est\.?\s*Next\s*Hour\W*" + Money + @"\s*[~\-–]\s*" + Money, RegexOptions.IgnoreCase);
            if (m.Success) { d.estNextHourLowUSD = Val(m.Groups[1].Value, m.Groups[2].Value); d.estNextHourHighUSD = Val(m.Groups[3].Value, m.Groups[4].Value); }
            m = Regex.Match(t, @"Last\s*24\s*Hours\W*?([+\-−])?\s*" + Money, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = Val(m.Groups[2].Value, m.Groups[3].Value);
                d.last24hUSD = m.Groups[1].Value == "-" || m.Groups[1].Value == "−" ? -v : v;
            }
            return d;
        }

        static double? Val(string main, string extra)
        {
            if (string.IsNullOrEmpty(extra)) extra = "";
            else if (extra.Length == 1) extra = extra + "0";
            return double.TryParse(main + extra, NumberStyles.Float, Inv, out var v) ? v : (double?)null;
        }

        public static bool Complete(EarningsData d) => d.balanceUSD != null;

        // ---------------- Salad's own background service (what Salad's window shows, without the window) ----------------
        /// <summary>
        /// Reads Salad's "Wallet" (current balance, last 24 h, next 24 h) from the local state stream that Salad's
        /// background service sends to the Salad app. Read-only, on this PC only. Everything else in that stream,
        /// including Salad's login token, is ignored and never stored or logged.
        /// </summary>
        public static EarningsData FromSaladService()
        {
            var raw = Salad.MonitorState(3500);
            try { return WalletFromFrames(raw); } finally { Array.Clear(raw, 0, raw.Length); }
        }

        public static EarningsData WalletFromFrames(byte[] raw)
        {
            double[] wallet = null;
            // the reply is a series of gRPC messages: 1 byte flag + 4 bytes length + protobuf
            for (int i = 0; i + 5 <= raw.Length;)
            {
                int len = raw[i + 1] << 24 | raw[i + 2] << 16 | raw[i + 3] << 8 | raw[i + 4];
                if (len < 0 || i + 5 + len > raw.Length) break;
                var w = FindWallet(raw, i + 5, i + 5 + len, 0);
                if (w != null) wallet = w;      // keep the newest
                i += 5 + len;
            }
            if (wallet == null) return null;
            var d = new EarningsData { balanceUSD = Math.Round(wallet[0], 4), last24hUSD = wallet[1] >= 0 ? Math.Round(wallet[1], 4) : (double?)null };
            // Salad's window shows "Est. Next Hour" as a range; we only show it when Salad provides a forecast
            if (wallet[2] > 0) { var perHour = wallet[2] / 24; d.estNextHourHighUSD = Math.Round(perHour, 4); d.estNextHourLowUSD = Math.Round(perHour * 0.45, 4); }
            return d;
        }

        /// <summary>Protobuf walk: a sub-message made only of doubles in fields 1-4 (currentBalance, balanceLast24Hours, balanceNext24Hours, earningRateMultiplier).</summary>
        static double[] FindWallet(byte[] b, int start, int end, int depth)
        {
            if (depth > 8) return null;
            double[] found = null;
            int p = start;
            while (p < end)
            {
                if (!Varint(b, ref p, end, out ulong tag)) return found;
                int field = (int)(tag >> 3), wt = (int)(tag & 7);
                if (wt == 0) { if (!Varint(b, ref p, end, out _)) return found; }
                else if (wt == 1) p += 8;
                else if (wt == 5) p += 4;
                else if (wt == 2)
                {
                    if (!Varint(b, ref p, end, out ulong l) || p + (long)l > end) return found;
                    int s = p, e = p + (int)l;
                    var w = AsWallet(b, s, e) ?? FindWallet(b, s, e, depth + 1);
                    if (w != null) found = w;
                    p = e;
                }
                else return found;
                if (p > end) return found;
            }
            return found;
        }
        static double[] AsWallet(byte[] b, int s, int e)
        {
            if (e - s < 9 || (e - s) % 9 != 0) return null;
            var v = new double[] { double.NaN, -1, -1, 0 };
            for (int p = s; p < e; p += 9)
            {
                int tag = b[p];
                if (tag != 0x09 && tag != 0x11 && tag != 0x19 && tag != 0x21) return null;
                v[(tag >> 3) - 1] = BitConverter.ToDouble(b, p + 1);
            }
            return !double.IsNaN(v[0]) && v[0] >= 0 && v[0] < 1e6 ? v : null;
        }
        static bool Varint(byte[] b, ref int p, int end, out ulong v)
        {
            v = 0;
            for (int shift = 0; shift < 64 && p < end; shift += 7)
            {
                byte x = b[p++]; v |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) return true;
            }
            return false;
        }

        // ---------------- reading the Salad window through UI Automation ----------------
        static Assembly uia, uiaTypes;

        delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        static List<IntPtr> Windows(int pid)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wp);
                if (wp == pid)
                {
                    var sb = new System.Text.StringBuilder(64); GetClassName(h, sb, 64);
                    if (sb.ToString().StartsWith("Chrome_WidgetWin")) list.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
        public static string LastError;

        /// <summary>All text Salad's window exposes to accessibility tools, or null if it can't be read (e.g. hidden in the tray).</summary>
        public static string ReadSaladText()
        {
            try
            {
                uia = uia ?? Assembly.Load("UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                uiaTypes = uiaTypes ?? Assembly.Load("UIAutomationTypes, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                var AE = uia.GetType("System.Windows.Automation.AutomationElement");
                var PC = uia.GetType("System.Windows.Automation.PropertyCondition");
                var Cond = uia.GetType("System.Windows.Automation.Condition");
                var Scope = uiaTypes.GetType("System.Windows.Automation.TreeScope");
                var root = AE.GetProperty("RootElement").GetValue(null);
                var pidProp = AE.GetField("ProcessIdProperty").GetValue(null);
                var trueCond = Cond.GetField("TrueCondition").GetValue(null);
                var children = Enum.Parse(Scope, "Children");
                var descendants = Enum.Parse(Scope, "Descendants");
                var findFirst = AE.GetMethod("FindFirst");
                var findAll = AE.GetMethod("FindAll");

                var texts = new List<string>();
                var fromHandle = AE.GetMethod("FromHandle");
                foreach (var p in Process.GetProcessesByName("Salad"))
                {
                    // every top-level window of the app, including one hidden in the tray (read-only; never shown or moved)
                    foreach (var hwnd in Windows(p.Id))
                    {
                        object win;
                        try { win = fromHandle.Invoke(null, new object[] { hwnd }); } catch { continue; }
                        if (win == null) continue;
                        var all = (System.Collections.IEnumerable)findAll.Invoke(win, new[] { descendants, trueCond });
                        foreach (var el in all)
                        {
                            var cur = el.GetType().GetProperty("Current").GetValue(el);
                            var name = cur.GetType().GetProperty("Name").GetValue(cur) as string;
                            if (!string.IsNullOrWhiteSpace(name)) texts.Add(name.Trim());
                        }
                        if (texts.Count > 20) break;
                    }
                }
                if (texts.Count == 0) { LastError = "Salad's window doesn't share its text while it's hidden in the tray."; return null; }
                LastError = null;
                return string.Join(" ", texts);
            }
            catch (Exception e) { LastError = (e.InnerException ?? e).Message; return null; }
        }
    }
}
