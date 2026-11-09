using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace GpuWatchdog
{
    /// <summary>
    /// Phase 1: "GPU-Watchdog-Agent.exe --discover" writes logs\discovery-report.txt: hardware, every sensor,
    /// how Salad runs, what its local service offers, games found, SignalRGB, and what can be read from Salad's window.
    /// Read-only: it changes nothing.
    /// </summary>
    static class Discovery
    {
        public static int Run(bool auto = false, Sensors shared = null)
        {
            Paths.Ensure();
            if (!auto) Conf.S = Json.Load<Settings>(Conf.FilePath);
            var o = new StringBuilder();
            void H(string t) { o.AppendLine(); o.AppendLine("== " + t + " =="); }
            void L(string t) => o.AppendLine(t);
            L("GPU Watchdog discovery report · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + Environment.MachineName);
            L("Running as administrator: " + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            L("Windows: " + Environment.OSVersion + " · .NET " + Environment.Version + " · time zone " + TimeZoneInfo.Local.Id + " -> " + Time.IanaZone());

            H("Hardware (WMI)");
            Wmi(o, "Win32_Processor", "Name");
            Wmi(o, "Win32_PhysicalMemory", "Manufacturer", "PartNumber", "Capacity", "ConfiguredClockSpeed", "DeviceLocator");
            Wmi(o, "Win32_DiskDrive", "Model", "Size", "InterfaceType");
            Wmi(o, "Win32_VideoController", "Name", "DriverVersion");
            Wmi(o, "Win32_BaseBoard", "Manufacturer", "Product");

            H("Sensors as the watchdog reads them");
            // when the watchdog is running, use its own sensor reader: a second one would shut the first one's driver access
            var own = shared == null ? new Sensors() : null;
            var s = shared ?? own;
            try
            {
              lock (s)
              {
                if (own != null) { own.Open(); System.Threading.Thread.Sleep(1500); }
                var r = s.Read();
                L("NVML: " + (s.NvmlOk ? "OK" : "FAILED " + s.NvmlError) + "   LibreHardwareMonitor: " + (s.LhmError ?? "OK"));
                L($"GPU {r.GpuName}: temp {V(r.GpuTemp)} core {V(r.GpuCore)} HOTSPOT {V(r.GpuHotspot)} util {V(r.GpuUtil)}% power {V(r.GpuPowerW)} W VRAM {V(r.VramUsedMB)}/{V(r.VramTotalMB)} MB");
                L("GPU fans: " + string.Join(", ", r.Fans.Select((f, i) => $"#{i + 1} {V(f.pct)}% {V(f.rpm)} rpm")) + (r.Fans.Count == 0 ? "none" : ""));
                L($"CPU {r.CpuName}: temp {V(r.CpuTemp)} power {V(r.CpuPowerW)} W");
                L("RAM: " + string.Join(", ", r.Ram.Select(x => x.name + " " + V(x.tempC))) + (r.Ram.Count == 0 ? "no DIMM temperatures" : ""));
                L("SSD: " + string.Join(", ", r.Ssd.Select(x => x.name + " " + V(x.tempC))) + (r.Ssd.Count == 0 ? "none" : ""));
                H("Every sensor LibreHardwareMonitor can see");
                foreach (var l in s.Inventory()) L(l);
              }
            }
            finally { own?.Dispose(); }
            L("GPU hotspot from HWiNFO / GPU-Z shared memory: " + V(ExternalSensors.GpuHotspot(), 1) + (ExternalSensors.HotspotSource.Length > 0 ? " (" + ExternalSensors.HotspotSource + ")" : " (none)") + $" · PawnIO module: bus {PawnNvidia.Bus} device {PawnNvidia.Device} {PawnNvidia.Error}");
            L("PawnIO driver (needed by LibreHardwareMonitor for CPU/RAM on new versions): " + (ServiceExists("PawnIO") ? "installed" : "NOT installed"));

            H("Salad");
            L("App path: " + Salad.AppPath + " exists=" + File.Exists(Salad.AppPath));
            foreach (var p in Process.GetProcesses().Where(p => p.ProcessName.IndexOf("salad", StringComparison.OrdinalIgnoreCase) >= 0))
                L($"process {p.ProcessName} pid {p.Id} path {Native.ProcessPath(p.Id)}");
            Wmi(o, "Win32_Service WHERE Name LIKE '%Salad%'", "Name", "State", "StartMode", "PathName");
            L("WSL distros running: " + (Proc.Run(Proc.System32("wsl.exe"), "-l -v", 8000, Encoding.Unicode) ?? "").Replace("\0", "").Trim());
            L("Salad job VM running: " + Salad.DistroRunning());
            try { L("Local service methods (gRPC reflection):"); foreach (var m in Salad.ListMethods()) L("  " + m); }
            catch (Exception e) { L("  couldn't list: " + e.Message); }
            var text = Earnings.ReadSaladText();
            L("Salad window text via accessibility: " + (text == null ? "unreadable (" + Earnings.LastError + ")" : text.Length + " chars"));
            if (text != null)
            {
                L("  text: " + (text.Length > 1500 ? text.Substring(0, 1500) + "…" : text));
                var d = Earnings.Parse(text);
                L($"  parsed: balance {V(d.balanceUSD, 4)} next hour {V(d.estNextHourLowUSD, 4)}–{V(d.estNextHourHighUSD, 4)} last 24h {V(d.last24hUSD, 4)}");
            }

            H("Games found (Steam, Epic, GOG, X:\\Games ...)");
            Games.Rescan(true);
            foreach (var f in Games.Folders) L((f.Ignore ? "  ignore " : "  ") + f.Source + ": " + f.Path + (f.Name != null ? "  (" + f.Name + ")" : ""));
            L("Library entries: " + Games.Library().Count);
            foreach (var g in Games.Library().Take(80)) L("  " + g["name"] + "  [" + g["source"] + "]");
            L("Running now: " + string.Join(", ", Games.Running().Select(g => g.Name + " (" + g.Program + ")")));

            H("SignalRGB");
            L("Local API " + Conf.S.SignalRgbApi + "/lighting: " + Lights.Probe());
            L("Lights-off URL (free version): " + Lights.BlackEffectUrl() + "  effect file: " + Lights.FoundEffectFile);
            foreach (var pr in Process.GetProcesses().Where(p => p.ProcessName.IndexOf("signal", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("hwinfo", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("gpu-z", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("nexus", StringComparison.OrdinalIgnoreCase) >= 0))
                L("process " + pr.ProcessName + " " + Native.ProcessPath(pr.Id));

            H("Nexus (how it gets the GPU hotspot) - read-only look");
            try
            {
                var nexusPids = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Nexus", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("nexus", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id.ToString()).ToList();
                var ns = Proc.Run(Proc.System32("netstat.exe"), "-ano -p TCP", 10000) ?? "";
                foreach (var line in ns.Split('\n')) { var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); if (parts.Length >= 5 && nexusPids.Contains(parts[4]) && parts[3] == "LISTENING") L("Nexus listens on " + parts[1]); }
                var dir = @"C:\Program Files\Nexus";
                if (Directory.Exists(dir))
                {
                    var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
                    L("Nexus files: " + files.Count);
                    foreach (var f in files.Where(f => !f.Contains("\\locales\\")).Take(60)) L("  " + f.Substring(dir.Length) + "  " + new FileInfo(f).Length);
                    var needles = new[] { "hotspot", "hot spot", "HWiNFO_SENS", "GPUZShMem", "PawnIO", "WinRing0", "nvapi", "NvAPI_GPU", "0xAD0A90", "MMIO", ".sys", "LibreHardwareMonitor", "OpenHardwareMonitor", "inpoutx64", "127.0.0.1:", "localhost:" };
                    foreach (var f in files.Where(f => new FileInfo(f).Length < 120_000_000 && (f.EndsWith(".exe") || f.EndsWith(".dll") || f.EndsWith(".asar") || f.EndsWith(".sys") || f.EndsWith(".js") || f.EndsWith(".json") || f.EndsWith(".node"))))
                    {
                        var hits = Hits(f, needles);
                        if (hits.Length > 0) L("  " + Path.GetFileName(f) + ": " + hits);
                    }
                }
            }
            catch (Exception e) { L("Nexus: " + e.Message); }

            H("Salad app: local service names (read-only look at the app's own files)");
            try
            {
                var res = @"C:\Program Files\Salad\resources";
                foreach (var f in Directory.Exists(res) ? Directory.EnumerateFiles(res, "*", SearchOption.AllDirectories).Where(x => new FileInfo(x).Length < 200_000_000).ToList() : new List<string>())
                {
                    var names = GrpcNames(f);
                    if (names.Count > 0) { L("  " + f.Substring(res.Length) + ":"); foreach (var n in names.Take(80)) L("    " + n); }
                }
            }
            catch (Exception e) { L("Salad files: " + e.Message); }

            H("Salad service live state (MonitorState, read-only, 4 s)");
            try
            {
                // only the wallet numbers are kept; the rest of the stream (it includes Salad's login token) is never written anywhere
                var w = Earnings.FromSaladService();
                L(w == null ? "wallet: not found in the stream" : string.Format(Time.Inv, "wallet: balance {0} · last 24 h {1} · next hour {2}–{3}", V(w.balanceUSD, 4), V(w.last24hUSD, 4), V(w.estNextHourLowUSD, 4), V(w.estNextHourHighUSD, 4)));
            }
            catch (Exception e) { L("MonitorState: " + e.Message); }

            H("Salad app: where Est. Next Hour comes from (app code only)");
            try
            {
                var code = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(@"C:\Program Files\Salad\resources\app.asar"));
                foreach (var word in new[] { "estimatedEarningsPerHour:", "estimatedEarningsPerHour=", "predictedEarnings", "earningsPerHour", "balanceNext24Hours" })
                {
                    int i = code.IndexOf(word), n = 0;
                    while (i >= 0 && n++ < 4)
                    {
                        L("[" + word + "] " + new string(code.Substring(Math.Max(0, i - 300), Math.Min(600, code.Length - Math.Max(0, i - 300))).Select(c => c >= 32 && c < 127 ? c : ' ').ToArray()));
                        i = code.IndexOf(word, i + word.Length);
                    }
                }
            }
            catch (Exception e) { L("asar: " + e.Message); }

            H("Nexus local web API");
            try
            {
                var routes = new List<string>();
                var dir = @"C:\Program Files\Nexus";
                foreach (var f in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories).ToList() : new List<string>())
                {
                    var js = File.ReadAllText(f);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(js, @"(/api/[A-Za-z0-9_/\-]+)"))
                        if (!routes.Contains(m.Groups[1].Value)) routes.Add(m.Groups[1].Value);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(js, @"[""'`](/(ws|hub|signalr|events|stream)[A-Za-z0-9_/\-]*)"))
                        if (!routes.Contains(m.Groups[1].Value)) routes.Add(m.Groups[1].Value);
                }
                L("routes in Nexus UI: " + string.Join(" ", routes.Take(300)));
                foreach (var f in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
                {
                    var js = File.ReadAllText(f);
                    foreach (var word in new[] { "Authorization", "x-api-key", "X-Nexus", "apiToken", "hotSpot", "hotspot", "new WebSocket", "EventSource", "signalr" })
                    {
                        int i = js.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                        if (i >= 0) L("  " + Path.GetFileName(f) + " [" + word + "] " + js.Substring(Math.Max(0, i - 160), Math.Min(340, js.Length - Math.Max(0, i - 160))).Replace("\n", " "));
                    }
                }
                foreach (var port in new[] { 9401, 9400 })
                    foreach (var route in new[] { "/" }.Concat(routes.Where(r => !r.Contains("$") && !r.Contains("{")).Where(r => r.Contains("sensor") || r.Contains("hardware") || r.Contains("monitor") || r.Contains("telemetry") || r.Contains("status") || r.Contains("gpu")).Take(8)))
                    {
                        try
                        {
                            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:" + port + route);
                            req.Timeout = 3000; req.Proxy = null;
                            using (var res = (System.Net.HttpWebResponse)req.GetResponse())
                            using (var r = new StreamReader(res.GetResponseStream()))
                            {
                                var body = r.ReadToEnd();
                                L($"GET :{port}{route} -> {(int)res.StatusCode} {res.ContentType} {body.Length} chars: " + body.Substring(0, Math.Min(700, body.Length)).Replace("\n", " "));
                                int h = body.IndexOf("ot", StringComparison.OrdinalIgnoreCase);
                                int hs = body.IndexOf("hotspot", StringComparison.OrdinalIgnoreCase); if (hs < 0) hs = body.IndexOf("hot spot", StringComparison.OrdinalIgnoreCase);
                                if (hs >= 0) L("   HOTSPOT near: " + body.Substring(Math.Max(0, hs - 200), Math.Min(500, body.Length - Math.Max(0, hs - 200))));
                            }
                        }
                        catch (System.Net.WebException e) { L($"GET :{port}{route} -> {e.Message}"); }
                    }
            }
            catch (Exception e) { L("Nexus API: " + e.Message); }

            H("Old GPU Watchdog");
            bool old = false;
            try { if (System.Threading.Mutex.TryOpenExisting("Global\\SaladGpuWatchdog", out var m)) { old = true; m.Dispose(); } } catch { old = true; }
            L("Running: " + old);

            var path = Path.Combine(Paths.Logs, "discovery-report.txt");
            File.WriteAllText(path, o.ToString(), new UTF8Encoding(false));
            Console.WriteLine(o.ToString());
            if (!auto) try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
            return 0;
        }

        static string Hits(string file, string[] needles)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var a = System.Text.Encoding.ASCII.GetString(bytes); var u = System.Text.Encoding.Unicode.GetString(bytes);
                var found = needles.Where(n => a.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || u.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                // for the hotspot word, also show the text around it
                var i = a.IndexOf("hotspot", StringComparison.OrdinalIgnoreCase); if (i < 0) i = a.IndexOf("hot spot", StringComparison.OrdinalIgnoreCase);
                var ctx = i >= 0 ? " | near: " + new string(a.Substring(Math.Max(0, i - 120), Math.Min(260, a.Length - Math.Max(0, i - 120))).Select(c => c >= 32 && c < 127 ? c : ' ').ToArray()) : "";
                return string.Join(", ", found) + ctx;
            }
            catch { return ""; }
        }

        /// <summary>gRPC paths and method names the Salad app uses (e.g. "/salad.grpc....Service/Method").</summary>
        static List<string> GrpcNames(string file)
        {
            var list = new List<string>();
            try
            {
                var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file));
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"salad\.grpc\.[A-Za-z0-9_.]+(/[A-Za-z0-9_]+)?"))
                    if (!list.Contains(m.Value)) list.Add(m.Value);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"SaladBowlWidgetService[^\n]{0,600}"))
                {
                    foreach (System.Text.RegularExpressions.Match w in System.Text.RegularExpressions.Regex.Matches(m.Value, @"\b(Get|Watch|Stream|List|Start|Stop)[A-Z][A-Za-z0-9]+"))
                        if (!list.Contains("method? " + w.Value)) list.Add("method? " + w.Value);
                }
            }
            catch { }
            return list;
        }

        static string V(double? v, int d = 0) => v.HasValue ? v.Value.ToString("F" + d, Time.Inv) : "N/A";

        static bool ServiceExists(string name)
        {
            try { using (var sc = new System.ServiceProcess.ServiceController(name)) { var _ = sc.Status; return true; } } catch { return false; }
        }

        static void Wmi(StringBuilder o, string cls, params string[] props)
        {
            try
            {
                var q = cls.Contains(" WHERE ") ? "SELECT * FROM " + cls : "SELECT * FROM " + cls;
                using (var s = new ManagementObjectSearcher(q))
                    foreach (ManagementObject m in s.Get())
                        o.AppendLine(cls.Split(' ')[0] + ": " + string.Join(" · ", props.Select(p => p + "=" + (m[p] ?? "").ToString().Trim())));
            }
            catch (Exception e) { o.AppendLine(cls + ": " + e.Message); }
        }
    }

    /// <summary>"--selftest": checks the pure logic (earnings parser, command signatures) without touching anything.</summary>
    static class SelfTest
    {
        public static int Run()
        {
            int fails = 0;
            void Eq(string what, object got, object want)
            {
                bool ok = Equals(got, want) || (got is double g && want is double w && Math.Abs(g - w) < 1e-9);
                Console.WriteLine((ok ? "ok   " : "FAIL ") + what + " => " + got + (ok ? "" : " (want " + want + ")"));
                if (!ok) fails++;
            }
            var d = Earnings.Parse("EARN PERFORMANCE Status Salad is chopping Chopping Power 4/4 Running Job Your Balance $3.47 31 Est. Next Hour $0.11 77 ~ $0.24 86 Last 24 Hours + $3.04 07");
            Eq("balance 3.47 + 31", d.balanceUSD, 3.4731);
            Eq("next hour low 0.11 77", d.estNextHourLowUSD, 0.1177);
            Eq("next hour high 0.24 86", d.estNextHourHighUSD, 0.2486);
            Eq("last 24h 3.04 07", d.last24hUSD, 3.0407);
            Eq("display low (3 decimals)", Math.Round(d.estNextHourLowUSD.Value, 3).ToString("0.000", Time.Inv), "0.118");
            Eq("display high (3 decimals)", Math.Round(d.estNextHourHighUSD.Value, 3).ToString("0.000", Time.Inv), "0.249");
            var split = Earnings.Parse("Your Balance\n$3.47\n31\nEst. Next Hour\n$0.11\n77\n~\n$0.24\n86\nLast 24 Hours\n+\n$3.04\n07");
            Eq("separate elements balance", split.balanceUSD, 3.4731);
            Eq("separate elements 24h", split.last24hUSD, 3.0407);
            var none = Earnings.Parse("Salad is chopping");
            Eq("missing balance stays null", none.balanceUSD, null);

            Conf.S = new Settings { AgentKey = "test-agent-key-0123456789abcdef0123456789abcdef" };
            var relay = new Relay();
            long now = Time.Now;
            string Sig(string id, string cmd, string a, long ts, string n) => Relay.Hmac(Conf.S.AgentKey, string.Join("|", id, cmd, a, ts.ToString(), n));
            Eq("valid command", relay.Verify("1", "salad_pause", "{}", now, "n1", Sig("1", "salad_pause", "{}", now, "n1")), null);
            Eq("replayed command", relay.Verify("1", "salad_pause", "{}", now, "n1", Sig("1", "salad_pause", "{}", now, "n1")), "replayed");
            Eq("old command", relay.Verify("2", "salad_pause", "{}", now - 40, "n2", Sig("2", "salad_pause", "{}", now - 40, "n2")), "too old");
            Eq("tampered command", relay.Verify("3", "pc_off_now", "{}", now, "n3", Sig("3", "salad_pause", "{}", now, "n3")), "bad signature");
            var salt = new byte[16];
            Eq("pbkdf2 length", Password.Derive("123456", salt, 1000).Length, 32);
            Eq("strength 6 digits", Password.Strength("123456"), 1);
            // Salad wallet inside a nested message of a gRPC frame (balance 4.5365, last 24 h 1.6263, no forecast)
            var wallet = new byte[] { 0x2A, 0x1B, 0x09, 0x8E, 0xC1, 0x28, 0x58, 0x60, 0x25, 0x12, 0x40, 0x11, 0x17, 0x0F, 0xA2, 0xCD, 0x5D, 0x05, 0xFA, 0x3F, 0x19, 0, 0, 0, 0, 0, 0, 0xF0, 0xBF, 0x48, 0x01 };
            var outer = new List<byte> { 0x0A, (byte)wallet.Length }; outer.AddRange(wallet);
            var frame = new List<byte> { 0, 0, 0, 0, (byte)outer.Count }; frame.AddRange(outer);
            var wd = Earnings.WalletFromFrames(frame.ToArray());
            Eq("wallet balance", wd?.balanceUSD, 4.5365);
            Eq("wallet last 24h", wd?.last24hUSD, 1.6263);
            Eq("wallet no forecast", wd?.estNextHourHighUSD, null);
            Console.WriteLine(fails == 0 ? "ALL AGENT SELF-TESTS PASSED" : fails + " FAILED");
            return fails == 0 ? 0 : 1;
        }
    }
}
