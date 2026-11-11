using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GpuWatchdog
{
    /// <summary>
    /// The PC control panel: a small local web page (127.0.0.1 only, with a secret token) shown in an Edge app window,
    /// so it looks like the website. Closing the window only hides the panel; the watchdog keeps running in the tray.
    /// Edge keeps its data in config\edge-panel, not in AppData.
    /// </summary>
    sealed class Panel
    {
        HttpListener http;
        public string Url => $"http://127.0.0.1:{Conf.S.PanelPort}/";

        public void Start()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    http = new HttpListener();
                    http.Prefixes.Add(Url);
                    http.Start();
                    break;
                }
                catch (HttpListenerException) { Conf.S.PanelPort++; Conf.Save(); http = null; }
            }
            if (http == null) { Log.Error("Couldn't start the control panel (no free local port)."); return; }
            Task.Run(Accept);
        }

        async Task Accept()
        {
            while (http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        void Serve(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                // Only this PC, only through the expected host name (stops other websites reaching it via DNS tricks)
                if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address) || req.Url.Host != "127.0.0.1") { res.StatusCode = 403; res.Close(); return; }
                res.Headers["Cache-Control"] = "no-store";
                res.Headers["X-Frame-Options"] = "DENY";
                var path = req.Url.AbsolutePath;
                if (!path.StartsWith("/api/")) { Static(path, res); return; }
                if (req.Headers["X-Panel-Token"] != Conf.S.PanelToken) { Write(res, 403, new { error = "forbidden" }); return; }
                Dictionary<string, object> body = null;
                if (req.HttpMethod == "POST")
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = Json.Obj(r.ReadToEnd());
                Write(res, 200, Api(path, req, body));
            }
            catch (Exception e) { try { Write(res, 500, new { error = e.Message }); } catch { } }
        }

        void Static(string path, HttpListenerResponse res)
        {
            if (path == "/") path = "/index.html";
            var file = Path.GetFullPath(Path.Combine(Paths.Panel, path.TrimStart('/').Replace('/', '\\')));
            var img = Path.GetFullPath(Path.Combine(Paths.Root, "assets", "processed", Path.GetFileName(path)));
            if (path.StartsWith("/img/") && File.Exists(img)) file = img;
            if ((!file.StartsWith(Paths.Panel, StringComparison.OrdinalIgnoreCase) && file != img) || !File.Exists(file)) { res.StatusCode = 404; res.Close(); return; }
            var ext = Path.GetExtension(file).ToLowerInvariant();
            res.ContentType = ext == ".html" ? "text/html; charset=utf-8" : ext == ".js" ? "text/javascript" : ext == ".css" ? "text/css" : ext == ".webp" ? "image/webp" : ext == ".svg" ? "image/svg+xml" : "application/octet-stream";
            var bytes = File.ReadAllBytes(file);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        static void Write(HttpListenerResponse res, int code, object o)
        {
            var b = Encoding.UTF8.GetBytes(Json.Write(o));
            res.StatusCode = code; res.ContentType = "application/json; charset=utf-8"; res.ContentLength64 = b.Length;
            res.OutputStream.Write(b, 0, b.Length); res.Close();
        }

        object Api(string path, HttpListenerRequest req, Dictionary<string, object> b)
        {
            var a = Agent.I; var s = Conf.S;
            switch (path)
            {
                case "/api/state":
                    return new Dictionary<string, object>
                    {
                        { "telemetry", a.Telemetry(false) },
                        { "relay", new Dictionary<string, object> { { "connected", a.Relay.Connected }, { "viewers", a.Relay.Viewers }, { "url", s.RelayUrl }, { "error", a.Relay.LastError } } },
                        { "password", new Dictionary<string, object> { { "synced", s.PwSyncedVer == s.PwVer }, { "isDefault", s.PwVer <= 1 } } },
                        { "settings", new Dictionary<string, object> {
                            { "safetyEnabled", s.SafetyEnabled }, { "safetySensor", s.SafetySensor }, { "safetyCoreLimitC", s.SafetyCoreLimitC },
                            { "safetyHotspotLimitC", s.SafetyHotspotLimitC }, { "safetyCpuLimitC", s.SafetyCpuLimitC }, { "safetySamples", s.SafetySamples },
                            { "gameResumeDelaySec", s.GameResumeDelaySec },
                            { "importSteam", s.ImportSteam }, { "importGameFolders", s.ImportGameFolders },
                            { "screenOffDelaySec", s.ScreenOffDelaySec }, { "screenOffEvenIfLightsFail", s.ScreenOffEvenIfLightsFail },
                            { "showEarningsOnWebsite", s.ShowEarningsOnWebsite }, { "viewRequiresPassword", s.ViewRequiresPassword },
                            { "startHiddenInTray", s.StartHiddenInTray }, { "hideSaladTrayIcon", s.HideSaladTrayIcon }, { "sampleSecondsIdle", s.SampleSecondsIdle },
                            { "relayUrl", s.RelayUrl }, { "dataFolder", Paths.Root } } },
                        { "hold", a.Hold }, { "pausedForGame", a.PausedForGame }, { "oldWatchdog", a.OldWatchdogRunning }, { "pauseUnsupported", a.PauseUnsupported },
                        { "lockout", a.Lock.Active ? new Dictionary<string, object> { { "at", a.Lock.At }, { "tempC", a.Lock.TempC }, { "sensor", a.Lock.Sensor }, { "temps", a.Lock.RecentTemps } } : null },
                        { "lights", new Dictionary<string, object> { { "screenOffAt", Lights.ScreenOffAt }, { "last", Lights.LastLightsResult } } },
                        { "games", new Dictionary<string, object> { { "active", a.ActiveGames.Select(g => g.Name).ToList() }, { "extra", s.GameProcesses }, { "ignore", s.IgnoreProcesses }, { "folders", s.GameFolders } } },
                        { "earningsStatus", a.EarnStatus },
                        { "version", System.IO.File.GetLastWriteTimeUtc(System.Reflection.Assembly.GetExecutingAssembly().Location).Ticks.ToString() },
                    };
                case "/api/library": return Games.Library();
                case "/api/programs": return Games.RunningPrograms();
                case "/api/temps": return a.LockoutTemps();
                case "/api/log":
                    {
                        long before = long.TryParse(req.QueryString["before"], out var bf) ? bf : long.MaxValue;
                        var cat = req.QueryString["cat"];
                        var all = Log.All().Where(e => e.t < before && (string.IsNullOrEmpty(cat) || e.cat == cat)).ToList();
                        var page = all.Skip(Math.Max(0, all.Count - 200)).Reverse().ToList();
                        return new { events = page, more = all.Count > page.Count };
                    }
                case "/api/cmd":
                    {
                        var cmd = Json.Str(b, "cmd");
                        var args = b != null && b.TryGetValue("args", out var x) ? x as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                        var r = Local(cmd, args);
                        return new { ok = r.ok, msg = r.msg };
                    }
            }
            return new { error = "not found" };
        }

        /// <summary>Panel-only actions on top of the shared commands. No password locally: you're at the PC.</summary>
        (bool ok, string msg) Local(string cmd, Dictionary<string, object> args)
        {
            var a = Agent.I; var s = Conf.S;
            switch (cmd)
            {
                case "hold_clear": return a.ClearHold();
                case "lockout_clear": return a.ClearLockout();
                case "lights_off": return (true, Lights.Start());
                case "lights_cancel": return (true, Lights.Cancel());
                case "lights_on": { var m = Lights.SignalRgb(true, out bool ok); return (ok, m); }
                case "open_log": Process.Start("explorer.exe", "\"" + Paths.Logs + "\""); return (true, "Opened.");
                case "salad_open": return (true, SaladTray.OpenWindow());
                case "halt_all":
                    {
                        // Halt: close Salad completely (app, service, its Linux jobs), then close the watchdog itself
                        try { SaladPeek.Release(); } catch { }
                        string res = Salad.Close(0);
                        Log.Warn("Halted from the panel: " + res + " Watchdog closed.");
                        System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(1500); ClosePanelWindows(); Program.ExitApp?.Invoke(); });
                        return (true, res + " The watchdog is closing.");
                    }
                case "open_folder": Process.Start("explorer.exe", "\"" + Paths.Root + "\""); return (true, "Opened.");
                case "password_set":
                    {
                        var pw = Json.Str(args, "password") ?? "";
                        if (pw.Length < 6) return (false, "Use at least 6 characters.");
                        if (pw.Length > 128) return (false, "That's too long.");
                        Password.Set(pw, s); Conf.Save();
                        Log.Info("Website password changed on the PC.", true);
                        a.Relay.SyncPassword();
                        return (true, a.Relay.Connected ? "Saved. Syncing to the website…" : "Saved. It will sync to the website when the relay is reachable.");
                    }
                case "game_add":
                    {
                        var name = (Json.Str(args, "name") ?? "").Trim();
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (false, "Enter a program name, like KingdomCome.exe.");
                        if (!s.GameProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) s.GameProcesses.Add(name);
                        s.IgnoreProcesses.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                        Conf.Save(); Log.Debug($"Game added: {name}.exe."); return (true, name + ".exe now counts as a game.");
                    }
                case "game_remove":
                    {
                        var name = Path.GetFileNameWithoutExtension(Json.Str(args, "name") ?? "");
                        s.GameProcesses.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                        Conf.Save(); return (true, "Removed.");
                    }
                case "ignore_add":
                    {
                        var name = Path.GetFileNameWithoutExtension(Json.Str(args, "name") ?? "");
                        if (name.Length > 0 && !s.IgnoreProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) s.IgnoreProcesses.Add(name);
                        Conf.Save(); return (true, name + ".exe won't count as a game.");
                    }
                case "folder_add":
                    {
                        var f = (Json.Str(args, "path") ?? "").Trim().Trim('"');
                        if (!Directory.Exists(f)) return (false, "That folder doesn't exist.");
                        if (!s.GameFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) s.GameFolders.Add(f);
                        Conf.Save(); Games.Rescan(true); return (true, "Folder added.");
                    }
                case "folder_remove":
                    s.GameFolders.RemoveAll(x => x.Equals(Json.Str(args, "path"), StringComparison.OrdinalIgnoreCase));
                    Conf.Save(); Games.Rescan(true); return (true, "Removed.");
                case "games_rescan": Games.Rescan(true); return (true, "Game list refreshed.");
                case "settings_set": return SetSettings(args);
            }
            return a.Command(cmd, args, "panel");
        }

        (bool ok, string msg) SetSettings(Dictionary<string, object> a)
        {
            var s = Conf.S; var changes = new List<string>();
            int Int(string k, int v, int lo, int hi) { var n = Json.Num(a, k); if (n == null) return v; var c = (int)Math.Max(lo, Math.Min(hi, n.Value)); if (c != v) changes.Add(k); return c; }
            bool B(string k, bool v) { var n = Json.Bool(a, k); if (n == null) return v; if (n.Value != v) changes.Add(k); return n.Value; }
            s.SafetyEnabled = B("safetyEnabled", s.SafetyEnabled);
            var sensor = Json.Str(a, "safetySensor");
            if (sensor == "core" || sensor == "hotspot" || sensor == "either") { if (sensor != s.SafetySensor) changes.Add("safetySensor"); s.SafetySensor = sensor; }
            s.SafetyCoreLimitC = Int("safetyCoreLimitC", s.SafetyCoreLimitC, 60, 95);
            s.SafetyHotspotLimitC = Int("safetyHotspotLimitC", s.SafetyHotspotLimitC, 70, 110);
            s.SafetyCpuLimitC = Int("safetyCpuLimitC", s.SafetyCpuLimitC, 60, 100);
            s.SafetySamples = Int("safetySamples", s.SafetySamples, 1, 30);
            s.GameResumeDelaySec = Int("gameResumeDelaySec", s.GameResumeDelaySec, 0, 600);
            s.ImportSteam = B("importSteam", s.ImportSteam);
            s.ImportGameFolders = B("importGameFolders", s.ImportGameFolders);
            s.ScreenOffDelaySec = Int("screenOffDelaySec", s.ScreenOffDelaySec, 0, 600);
            s.ScreenOffEvenIfLightsFail = B("screenOffEvenIfLightsFail", s.ScreenOffEvenIfLightsFail);
            s.ShowEarningsOnWebsite = B("showEarningsOnWebsite", s.ShowEarningsOnWebsite);
            bool vr = s.ViewRequiresPassword;
            s.ViewRequiresPassword = B("viewRequiresPassword", s.ViewRequiresPassword);
            s.StartHiddenInTray = B("startHiddenInTray", s.StartHiddenInTray);
            bool hadTray = s.HideSaladTrayIcon;
            s.HideSaladTrayIcon = B("hideSaladTrayIcon", s.HideSaladTrayIcon);
            if (hadTray && !s.HideSaladTrayIcon) SaladTray.Show(); else if (!hadTray && s.HideSaladTrayIcon) SaladTray.Hide();
            s.SampleSecondsIdle = Int("sampleSecondsIdle", s.SampleSecondsIdle, 5, 60);
            var url = Json.Str(a, "relayUrl");
            if (url != null && url.Trim() != s.RelayUrl) { s.RelayUrl = url.Trim(); changes.Add("relayUrl"); }
            Conf.Save();
            if (changes.Count == 0) return (true, "No changes.");
            if (changes.Any(c => c.StartsWith("safety"))) Log.Info($"Safety settings changed on the PC: {(s.SafetyEnabled ? "on" : "OFF")}, sensor {s.SafetySensor}, core {s.SafetyCoreLimitC} °C, hotspot {s.SafetyHotspotLimitC} °C, CPU {s.SafetyCpuLimitC} °C, {s.SafetySamples} readings.");
            if (changes.Contains("importSteam") || changes.Contains("importGameFolders")) Games.Rescan(true);
            if (vr != s.ViewRequiresPassword) Agent.I.Relay.Send(new Dictionary<string, object> { { "type", "info" }, { "viewRequiresPassword", s.ViewRequiresPassword } });
            Agent.I.SendTelemetry();
            return (true, "Saved.");
        }

        /// <summary>Closes the panel's own browser window (only the one using the watchdog's private profile in config\edge-panel).</summary>
        public static void ClosePanelWindows()
        {
            try
            {
                var profile = Paths.Cfg("edge-panel");
                using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='brave.exe' OR Name='chrome.exe'"))
                    foreach (System.Management.ManagementObject o in q.Get())
                    {
                        var cl = o["CommandLine"] as string;
                        if (cl == null || cl.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch { }
                    }
            }
            catch (Exception e) { Log.Debug("Couldn't close the panel window: " + e.Message); }
        }

        // ---------------- the window ----------------
        public void Open()
        {
            var url = Url + "?t=" + Uri.EscapeDataString(Conf.S.PanelToken);
            var edge = FindEdge();
            try
            {
                if (edge != null)
                {
                    // via a shortcut opened by Explorer, so Edge runs with your normal rights (not the watchdog's admin rights)
                    var lnk = Paths.Cfg("GPU Watchdog Panel.lnk");
                    var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                    sc.TargetPath = edge;
                    sc.Arguments = $"--app=\"{url}\" --user-data-dir=\"{Paths.Cfg("edge-panel")}\" --no-first-run --no-default-browser-check --disable-features=msEdgeSidebarV2 --window-size=1320,900";
                    sc.Save();
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lnk + "\"") { UseShellExecute = false });
                }
                else Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
            }
            catch (Exception e) { Log.Error("Couldn't open the panel: " + e.Message); }
        }

        static string FindEdge()
        {
            var candidates = new List<string>();
            try { candidates.Add(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe")?.GetValue("") as string); } catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"));
            // no Edge: any Chromium browser can show the panel as its own app window
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"));
            return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
        }

        public void Stop() { try { http?.Stop(); } catch { } }
    }
}
