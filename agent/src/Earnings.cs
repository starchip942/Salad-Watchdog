using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
        public long nextHourAt;        // when the next-hour range was last read from Salad's window
    }

    /// <summary>
    /// Reads the numbers Salad shows on its Earn tab, read-only, through Windows' accessibility interface
    /// (the same thing screen readers use). No logins, tokens, network calls or memory reading.
    /// The small grey digits after the cents are extra decimals: "$3.47 31" means $3.4731.
    /// </summary>
    static class Earnings
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string Money = @"\$\s*(\d+\s*\.\s*\d{2})\s*(\d{1,2})?";   // Salad splits it up: "$ 0 . 06 22" = $0.0622

        /// <summary>Parses the Earn tab's visible text. Missing pieces stay null.</summary>
        public static EarningsData Parse(string text)
        {
            var d = new EarningsData();
            var t = Regex.Replace(text ?? "", @"\s+", " ");
            var m = Regex.Match(t, @"Your Balance(?:\s*Learn more)?\W*" + Money, RegexOptions.IgnoreCase);
            if (m.Success) d.balanceUSD = Val(m.Groups[1].Value, m.Groups[2].Value);
            m = Regex.Match(t, @"Est\.?\s*Next\s*Hour\W*" + Money + @"\s*[~\-–]\s*" + Money, RegexOptions.IgnoreCase);
            if (m.Success) { d.estNextHourLowUSD = Val(m.Groups[1].Value, m.Groups[2].Value); d.estNextHourHighUSD = Val(m.Groups[3].Value, m.Groups[4].Value); }
            else if ((m = Regex.Match(t, @"Est\.?\s*Next\s*Hour\W*" + Money, RegexOptions.IgnoreCase)).Success)
                d.estNextHourLowUSD = d.estNextHourHighUSD = Val(m.Groups[1].Value, m.Groups[2].Value);   // Salad shows one number ($0.00) when there's no job
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
            main = Regex.Replace(main ?? "", @"\s+", "");
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
        /// <summary>
        /// Salad's own "active workload" state from the MonitorState stream (event field 11, activeWorkloadState).
        /// Salad's app treats 2 as "a workload is running" (that's what its Chop Now / Pause-until-idle display uses).
        /// </summary>
        public static List<int> ActiveWorkloadStates(byte[] raw)
        {
            var list = new List<int>();
            int i = 0;
            while (i + 5 <= raw.Length)
            {
                int len = raw[i + 1] << 24 | raw[i + 2] << 16 | raw[i + 3] << 8 | raw[i + 4];
                if (len < 0 || i + 5 + len > raw.Length) break;
                Walk(raw, i + 5, i + 5 + len, 0, list);
                i += 5 + len;
            }
            return list;
        }
        /// <summary>Only messages that parse cleanly to the end are trusted (so random bytes aren't read as fields).</summary>
        static bool Walk(byte[] b, int start, int end, int depth, List<int> found)
        {
            if (depth > 6) return false;
            var mine = new List<int>(); var subs = new List<int[]>();
            int p = start;
            while (p < end)
            {
                if (!Varint(b, ref p, end, out ulong tag) || tag == 0) return false;
                int field = (int)(tag >> 3), wt = (int)(tag & 7);
                if (wt == 0) { if (!Varint(b, ref p, end, out ulong v)) return false; if (field == 11 && v <= 10) mine.Add((int)v); }
                else if (wt == 1) p += 8;
                else if (wt == 5) p += 4;
                else if (wt == 2) { if (!Varint(b, ref p, end, out ulong l) || p + (long)l > end) return false; subs.Add(new[] { p, p + (int)l }); p += (int)l; }
                else return false;
            }
            if (p != end) return false;
            found.AddRange(mine);
            foreach (var sb in subs) { var tmp = new List<int>(); if (Walk(b, sb[0], sb[1], depth + 1, tmp)) found.AddRange(tmp); }
            return true;
        }

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

                var texts = new List<string>(); object earnEl = null;
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
                            if (earnEl == null && name != null && name.Trim().Equals("EARN", StringComparison.OrdinalIgnoreCase)) earnEl = el;
                        }
                        if (texts.Count > 20) break;
                    }
                }
                if (texts.Count == 0) { LastError = "Salad's window doesn't share its text while it's hidden in the tray."; return null; }
                LastError = null;
                var joined = string.Join(" ", texts);
                // the invisible (parked) window may be on another page: switch it to the Earn page, where "Est. Next Hour" is
                if (earnEl != null && SaladPeek.Parked && joined.IndexOf("Next Hour", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    try
                    {
                        var IP = uia.GetType("System.Windows.Automation.InvokePattern");
                        var pat = IP.GetField("Pattern").GetValue(null);
                        var p = AE.GetMethod("GetCurrentPattern").Invoke(earnEl, new[] { pat });
                        p.GetType().GetMethod("Invoke").Invoke(p, null);
                    }
                    catch { }
                }
                return joined;
            }
            catch (Exception e) { LastError = (e.InnerException ?? e).Message; return null; }
        }
    }

    /// <summary>
    /// "Est. Next Hour" only exists in Salad's own window (Salad's servers send it to the window, not to the background service).
    /// So the watchdog opens Salad's window once, invisibly (fully transparent, off-screen, no taskbar button), and leaves it
    /// "parked" there so the number can be read every minute like a screen reader would. Nothing in Salad is clicked or changed.
    /// The moment you open Salad yourself (tray icon), the window is put back to normal so you see it as usual.
    /// </summary>
    static class SaladPeek
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
        /// <summary>Is the mouse over the taskbar / tray (where you'd click Salad's icon)?</summary>
        public static bool MouseOnTaskbar()
        {
            if (!GetCursorPos(out var p)) return false;
            var root = GetAncestor(WindowFromPoint(p), 2 /*GA_ROOT*/);
            var c = ClassOf(root);
            // taskbar/tray icon, Start menu, or a desktop shortcut
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c.StartsWith("Xaml")
                || c == "Windows.UI.Core.CoreWindow" || c == "Progman" || c == "WorkerW";
        }
        static int saladProcs; static DateTime ourLaunchAt;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        const uint SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, LWA_ALPHA = 2;

        public static string LastResult = "";
        static IntPtr parked; static RECT orig; static int origEx; static DateTime parkedAt; static uint inputAtPark;
        static readonly object Gate = new object();
        static Thread guard;

        public static bool Parked { get { lock (Gate) return parked != IntPtr.Zero && IsWindow(parked); } }

        static HashSet<uint> Pids() => new HashSet<uint>(Process.GetProcessesByName("Salad").Select(p => (uint)p.Id));
        static List<IntPtr> SaladWindows(bool visible, string cls = null)
        {
            var pids = Pids(); var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid) || (visible && !IsWindowVisible(h))) return true;
                if (cls != null) { var sb = new System.Text.StringBuilder(64); GetClassName(h, sb, 64); if (sb.ToString() != cls) return true; }
                list.Add(h); return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>Opens and parks Salad's window if it isn't parked yet. Returns true when a parked window is ready to read.</summary>
        public static bool EnsureParked()
        {
            if (Parked) return true;
            if (!Salad.AppRunning()) { LastResult = "Salad isn't running."; return false; }
            if (SaladWindows(true).Count > 0) { LastResult = "Salad's window is open; read directly."; return false; }
            StartGuard();
            var fg = GetForegroundWindow();
            IntPtr found = IntPtr.Zero;
            var watchUntil = DateTime.Now.AddSeconds(14);
            // No flicker: make Salad's window see-through BEFORE it is shown.
            // a) windows Salad keeps hidden in the tray are made invisible now, while still hidden
            foreach (var h in SaladWindows(false, "Chrome_WidgetWin_1")) PreStyle(h);
            // b) a window Salad creates fresh is caught the moment Windows creates it (before it's shown or painted)
            var hookThread = new Thread(() => HookNewWindows(watchUntil, () => found != IntPtr.Zero)) { IsBackground = true };
            hookThread.Start();
            Thread.Sleep(100);
            // watch for the window from the very first moment, so it's made invisible before it's painted
            var watcher = new Thread(() =>
            {
                while (DateTime.Now < watchUntil && found == IntPtr.Zero)
                {
                    var w = SaladWindows(true, "Chrome_WidgetWin_1");
                    if (w.Count > 0) { found = w[0]; Park(found, fg); break; }
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            watcher.Start();
            try
            {
                // 1) open Salad again (as you, through Explorer): a running Salad shows its window
                // 2) the same message Windows sends when you click Salad's tray icon
                foreach (var host in SaladWindows(false, "Electron_NotifyIconHostWindow"))
                    for (int id = 1; id <= 4 && found == IntPtr.Zero; id++)
                    {
                        PostMessage(host, 0x8001 /*WM_APP+1*/, new IntPtr(id), new IntPtr(0x0201 /*WM_LBUTTONDOWN*/));
                        PostMessage(host, 0x8001, new IntPtr(id), new IntPtr(0x0202 /*WM_LBUTTONUP*/));
                        Thread.Sleep(150);
                    }
                if (watcher.Join(4000) && found != IntPtr.Zero) { LastResult = "opened (tray)"; return true; }
                foreach (var host in SaladWindows(false, "Electron_NotifyIconHostWindow"))
                    for (int id = 1; id <= 4 && found == IntPtr.Zero; id++) { PostMessage(host, 0x8001, new IntPtr(id), new IntPtr(0x0203 /*WM_LBUTTONDBLCLK*/)); Thread.Sleep(150); }
                watcher.Join(5000);
                LastResult = found != IntPtr.Zero ? "opened (tray, double-click)" : "Salad's window didn't open.";
                return found != IntPtr.Zero;
            }
            catch (Exception e) { LastResult = e.Message; return false; }
            finally
            {
                // any window made see-through in advance that didn't become the parked one goes back to normal
                lock (Gate)
                    foreach (var kv in preStyled.ToList())
                        if (kv.Key != parked) { if (IsWindow(kv.Key)) { SetLayeredWindowAttributes(kv.Key, 0, 255, LWA_ALPHA); SetWindowLong(kv.Key, GWL_EXSTYLE, kv.Value); } preStyled.Remove(kv.Key); }
            }
        }

        static readonly Dictionary<IntPtr, int> preStyled = new Dictionary<IntPtr, int>();
        static void PreStyle(IntPtr h)
        {
            lock (Gate)
            {
                if (preStyled.ContainsKey(h)) return;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                preStyled[h] = ex;
                SetWindowLong(h, GWL_EXSTYLE, (ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW);
                SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
            }
        }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
        static WinEventProc keepAlive;
        /// <summary>Watches Windows' "window created" events for Salad and makes a new Salad window see-through instantly.</summary>
        static void HookNewWindows(DateTime until, Func<bool> done)
        {
            var pids = Pids();
            keepAlive = (hook, ev, hwnd, idObject, idChild, thread, time) =>
            {
                try
                {
                    if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (!pids.Contains(pid) || GetAncestor(hwnd, 2) != hwnd || ClassOf(hwnd) != "Chrome_WidgetWin_1") return;
                    PreStyle(hwnd);
                }
                catch { }
            };
            // EVENT_OBJECT_CREATE (0x8000) .. EVENT_OBJECT_SHOW (0x8002), out of context, skip our own process
            var h = SetWinEventHook(0x8000, 0x8002, IntPtr.Zero, keepAlive, 0, 0, 0x0000 | 0x0002);
            try
            {
                while (DateTime.Now < until && !done())
                {
                    while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
                    Thread.Sleep(2);
                }
            }
            finally { if (h != IntPtr.Zero) UnhookWinEvent(h); }
        }

        static void Park(IntPtr h, IntPtr fg)
        {
            lock (Gate)
            {
                GetWindowRect(h, out orig);
                origEx = preStyled.TryGetValue(h, out var pre) ? pre : GetWindowLong(h, GWL_EXSTYLE);
                preStyled.Remove(h);
                // fully transparent and click-through, but ON screen and on top: Salad only draws (and shares) its page
                // while Windows reports the window as visible, so it can't be hidden off-screen or behind other windows
                SetWindowLong(h, GWL_EXSTYLE, (origEx | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW);
                SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
                SetWindowPos(h, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, SWP_NOSIZE | 2 /*SWP_NOMOVE*/ | SWP_NOACTIVATE);
                parkFg = fg;
                parked = h; parkedAt = DateTime.Now; inputAtPark = Native.LastInputTick();
                saladProcs = Process.GetProcessesByName("Salad").Length;
            }
            GiveFocusBack(fg);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        static IntPtr parkFg;
        static void GiveFocusBack(IntPtr fg)
        {
            if (fg == IntPtr.Zero) fg = parkFg;
            if (fg == IntPtr.Zero || !IsWindow(fg)) fg = FindWindow("Shell_TrayWnd", null);
            if (fg == IntPtr.Zero) return;
            try
            {
                uint me = GetCurrentThreadId(), cur = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                AttachThreadInput(me, cur, true); SetForegroundWindow(fg); AttachThreadInput(me, cur, false);
            }
            catch { }
        }

        /// <summary>Puts the window back exactly as it was (visible, normal position).</summary>
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        /// <summary>hide: send the window back to the background first (watchdog closing/updating), so it never pops up on screen.</summary>
        public static void Release(bool hide = false)
        {
            lock (Gate)
            {
                if (parked == IntPtr.Zero) return;
                if (IsWindow(parked))
                {
                    if (hide) ShowWindow(parked, 0 /*SW_HIDE*/);
                    SetWindowPos(parked, new IntPtr(-2) /*HWND_NOTOPMOST*/, orig.L, orig.T, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
                    SetLayeredWindowAttributes(parked, 0, 255, LWA_ALPHA);
                    SetWindowLong(parked, GWL_EXSTYLE, origEx);
                }
                parked = IntPtr.Zero;
            }
        }

        static IntPtr lastFg;
        static void StartGuard()
        {
            if (guard != null) return;
            guard = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(200);
                    try
                    {
                        IntPtr h; lock (Gate) h = parked;
                        if (h == IntPtr.Zero) { lastFg = GetForegroundWindow(); continue; }
                        if (!IsWindow(h)) { lock (Gate) parked = IntPtr.Zero; continue; }
                        if (!IsWindowVisible(h)) { Release(); Log.Debug("Salad hid its window itself."); continue; }          // Salad hid it to the tray itself
                        var fgNow = GetForegroundWindow();
                        bool becameFg = fgNow == h && lastFg != h;
                        // you opened Salad yourself: clicked its tray icon, or started Salad again from a shortcut
                        // you opened Salad yourself: it came to the front right after a mouse click / key press of yours
                        bool userOpened = becameFg && (DateTime.Now - parkedAt).TotalSeconds > 3 && (Native.UserActiveWithinMs(2500) || MouseOnTaskbar());
                        if (userOpened) { Release(); Log.Debug("Salad opened by you; window shown normally."); continue; }
                        // never keep the invisible window in front, but never take focus away while you're using the PC
                        if (fgNow == h && !Native.UserActiveWithinMs(2500)) GiveFocusBack(lastFg != h && lastFg != IntPtr.Zero ? lastFg : IntPtr.Zero);
                        else lastFg = fgNow;
                    }
                    catch { }
                }
            }) { IsBackground = true };
            guard.Start();
        }
    }
}
