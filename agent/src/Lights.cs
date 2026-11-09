using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace GpuWatchdog
{
    /// <summary>
    /// "Lights & Screen Off": SignalRGB lighting off at once (through SignalRGB's own documented local API,
    /// PATCH /lighting/enabled), then the monitors off after a delay. Display power only: no sleep, lock or shutdown,
    /// so Salad keeps earning. Moving the mouse or pressing a key wakes the screen as usual.
    /// </summary>
    static class Lights
    {
        public static long ScreenOffAt;        // epoch seconds; 0 = no countdown
        public static string LastLightsResult = "";
        static Timer timer;
        static readonly object Gate = new object();

        public static string Start()
        {
            lock (Gate)
            {
                var lights = SignalRgb(false, out bool ok);
                LastLightsResult = lights;
                if (!ok && !Conf.S.ScreenOffEvenIfLightsFail)
                {
                    Log.Debug(lights + " The screen stays on (setting).");
                    return lights + " The screen stays on.";
                }
                int delay = Math.Max(0, Conf.S.ScreenOffDelaySec);
                ScreenOffAt = Time.Now + delay;
                timer?.Dispose();
                timer = new Timer(_ => ScreenOff(), null, delay * 1000, Timeout.Infinite);
                Log.Debug((ok ? "Lights off, screen off in " : lights + " Screen off in ") + delay + " s.");
                return (ok ? "Lights off. " : lights + " ") + "Screen turns off in " + delay + " s.";
            }
        }

        public static string Cancel()
        {
            lock (Gate)
            {
                if (ScreenOffAt == 0) return "Nothing to cancel.";
                timer?.Dispose(); timer = null; ScreenOffAt = 0;
                Log.Debug("Lights & Screen Off: cancelled. The lights stay off until you turn them on in SignalRGB.");
                return "Cancelled. The screen stays on.";
            }
        }

        static uint offInput;
        /// <summary>The watchdog turned the screen off and nobody has touched mouse/keyboard since.</summary>
        public static bool ScreenIsOff => offInput != 0 && Native.LastInputTick() == offInput;
        static void ScreenOff()
        {
            lock (Gate) { ScreenOffAt = 0; timer?.Dispose(); timer = null; }
            offInput = Native.LastInputTick();
            DisplayOff();
            Log.Debug("Screen off done.");
            // Some monitors wake straight away; send the signal again, but not if the person has moved the mouse since
            uint input = Native.LastInputTick();
            foreach (var wait in new[] { 3000, 6000 })
            {
                Thread.Sleep(wait);
                if (Native.LastInputTick() != input) break;
                DisplayOff();
            }
        }

        public static void DisplayOff()
        {
            const uint WM_SYSCOMMAND = 0x0112; const int SC_MONITORPOWER = 0xF170;
            // Posted (not sent) so a hung window can't block us; 2 = power off
            Native.PostMessage(new IntPtr(0xFFFF), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
        }

        /// <summary>
        /// Turns SignalRGB's lighting off (or on). First SignalRGB's local API (needs SignalRGB Pro: free accounts get 403),
        /// then SignalRGB's own application URL to apply a solid black "static colour" effect, which works on the free version.
        /// </summary>
        public static string SignalRgb(bool enabled, out bool ok)
        {
            ok = false;
            string apiResult;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Conf.S.SignalRgbApi.TrimEnd('/') + "/lighting/enabled");
                req.Method = "PATCH"; req.ContentType = "application/json"; req.Timeout = 4000; req.Proxy = null;
                var body = Encoding.UTF8.GetBytes("{\"enabled\":" + (enabled ? "true" : "false") + "}");
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var res = (HttpWebResponse)req.GetResponse())
                    if ((int)res.StatusCode < 300) { ok = true; return "Lights " + (enabled ? "on." : "off."); }
                apiResult = "API refused";
            }
            catch (WebException e) when (e.Status == WebExceptionStatus.ConnectFailure || e.Status == WebExceptionStatus.Timeout)
            {
                if (System.Diagnostics.Process.GetProcessesByName("SignalRgb").Length == 0 && System.Diagnostics.Process.GetProcessesByName("SignalRGB").Length == 0)
                    return "SignalRGB isn't running, so the lights couldn't be changed.";
                apiResult = "API not reachable";
            }
            catch (WebException e) { apiResult = "API: " + e.Message; }   // 403 = free version
            catch (Exception e) { apiResult = e.Message; }

            if (enabled) return "Turn the lights back on in SignalRGB (pick your effect again). (" + apiResult + ")";
            // Free version: apply a solid black static colour through SignalRGB's application URL
            try
            {
                var url = BlackEffectUrl();
                var before = SignalWindows(true);
                // through Explorer, so SignalRGB gets it as you (not with the watchdog's admin rights)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
                ok = true;
                LastUrl = url;
                // SignalRGB sometimes brings its window up anyway: put back any window that wasn't showing before
                System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 16; i++)
                    {
                        Thread.Sleep(250);
                        foreach (var h in SignalWindows(true)) if (!before.Contains(h)) ShowWindow(h, 0 /*SW_HIDE*/);
                    }
                });
                return "Lights off (solid black in SignalRGB).";
            }
            catch (Exception e) { return "Couldn't turn the lights off: " + e.Message + " (" + apiResult + ")"; }
        }

        public static string LastUrl = "";

        /// <summary>
        /// Finds SignalRGB's solid/static colour effect and its colour setting by reading the effect file (read-only),
        /// so the URL uses the exact names your installed SignalRGB expects. Falls back to "Solid Color" / "color".
        /// </summary>
        public static string BlackEffectUrl()
        {
            if (!string.IsNullOrWhiteSpace(Conf.S.SignalRgbOffUrl)) return Conf.S.SignalRgbOffUrl;
            string effect = "Solid Color", prop = "color";
            try
            {
                var roots = new[] {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortxEngine"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhirlwindFX"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhirlwindFX") };
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*.html", SearchOption.AllDirectories))
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (name.IndexOf("solid", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("static", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var html = File.ReadAllText(f);
                        var title = System.Text.RegularExpressions.Regex.Match(html, "<title>([^<]+)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=\"([^\"]+)\"[^>]*type=\"color\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*type=\"color\"[^>]*property=\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        effect = title.Success ? title.Groups[1].Value.Trim() : name;
                        prop = m.Groups[1].Value;
                        FoundEffectFile = f;
                        goto done;
                    }
                }
            }
            catch { }
            done:
            return "signalrgb://effect/apply/" + Uri.EscapeDataString(effect) + "?-silentlaunch-&" + Uri.EscapeDataString(prop) + "=%23000000";
        }
        public static string FoundEffectFile = "";

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        static System.Collections.Generic.HashSet<IntPtr> SignalWindows(bool visibleOnly)
        {
            var pids = new System.Collections.Generic.HashSet<uint>();
            foreach (var n in new[] { "SignalRgb", "SignalRgbLauncher" }) foreach (var p in System.Diagnostics.Process.GetProcessesByName(n)) pids.Add((uint)p.Id);
            var set = new System.Collections.Generic.HashSet<IntPtr>();
            EnumWindows((h, l) => { GetWindowThreadProcessId(h, out uint pid); if (pids.Contains(pid) && (!visibleOnly || IsWindowVisible(h))) set.Add(h); return true; }, IntPtr.Zero);
            return set;
        }

        /// <summary>Discovery: is SignalRGB's API there, and what does it say?</summary>
        public static string Probe()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Conf.S.SignalRgbApi.TrimEnd('/') + "/lighting");
                req.Timeout = 3000; req.Proxy = null;
                using (var res = req.GetResponse()) using (var r = new StreamReader(res.GetResponseStream())) return r.ReadToEnd();
            }
            catch (Exception e) { return "not reachable: " + e.Message; }
        }
    }
}
