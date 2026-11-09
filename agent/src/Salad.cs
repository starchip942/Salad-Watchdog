using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GpuWatchdog
{
    /// <summary>
    /// Starting, pausing and closing Salad the way a person would, ported from the old GPU Watchdog.
    /// Never touches Salad's files, settings, logins, payments, network traffic or memory.
    /// "Chop Now" and "Pause" press the same Start/Stop buttons the Salad app itself uses, through Salad's
    /// local background-service API on this PC (127.0.0.1), exactly as the old watchdog did.
    /// </summary>
    static class Salad
    {
        public static readonly string[] Processes = { "Salad", "Salad.Bootstrapper", "Salad.Bowl.Service" };
        public const string Service = "SaladBowl";
        public const string Distro = "salad-enterprise-linux";   // WSL VM where Salad's GPU jobs run
        public static string AppPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Salad", "Salad.exe");

        public static bool AppRunning() => Process.GetProcessesByName("Salad").Length > 0;

        public static bool ServiceRunning()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(Service))
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped) return true;
            }
            catch { }
            return Process.GetProcessesByName("Salad.Bootstrapper").Length + Process.GetProcessesByName("Salad.Bowl.Service").Length > 0;
        }

        public static bool AnyRunning() => AppRunning() || ServiceRunning();

        /// <summary>Is Salad's job VM running? (wsl.exe prints UTF-16)</summary>
        public static bool DistroRunning()
        {
            var o = Proc.Run(Proc.System32("wsl.exe"), "-l --running -q", 8000, Encoding.Unicode);
            if (o == null) return false;
            return o.Replace("\0", "").Split('\n').Any(l => l.Trim().Equals(Distro, StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> RunningParts()
        {
            var found = new List<string>();
            if (AppRunning()) found.Add("the Salad app");
            if (ServiceRunning()) found.Add("the Salad background service");
            if (DistroRunning()) found.Add("Salad's GPU jobs");
            return found;
        }

        static void RememberAppPath()
        {
            foreach (var p in Process.GetProcessesByName("Salad"))
            {
                var path = Native.ProcessPath(p.Id);
                if (!string.IsNullOrEmpty(path)) { AppPath = path; return; }
            }
        }

        /// <summary>Close Salad completely: graceful first (up to graceMs), then force. Returns a plain outcome.</summary>
        public static string Close(int graceMs = 3000)
        {
            if (!AnyRunning() && !DistroRunning()) return "Salad is not running.";
            RememberAppPath();
            // 1. ask the app to close, the way clicking its window's X would
            foreach (var p in Process.GetProcessesByName("Salad")) { try { p.CloseMainWindow(); } catch { } }
            // 2. ask Windows to stop the background service
            Proc.Run(Proc.System32("sc.exe"), "stop " + Service, 5000);
            var until = DateTime.Now.AddMilliseconds(graceMs);
            while (DateTime.Now < until && AnyRunning()) Thread.Sleep(250);
            // 3. anything left is force-closed, app first so it can't restart the service
            foreach (var name in Processes)
                foreach (var p in Process.GetProcessesByName(name)) { try { p.Kill(); } catch { } }
            // 4. the GPU jobs themselves run inside Salad's WSL VM
            Proc.Run(Proc.System32("wsl.exe"), "--terminate " + Distro, 15000);
            Thread.Sleep(500);
            var left = RunningParts();
            return left.Count == 0 ? "Salad has been closed." : "Tried to close Salad, but " + Words(left) + " is still running.";
        }

        /// <summary>Start Salad like it starts with Windows: service, then the app with only its tray icon, as you (not as admin).</summary>
        public static string Launch()
        {
            Proc.Run(Proc.System32("sc.exe"), "start " + Service, 8000);
            if (AppRunning()) return "Salad's service started.";
            if (!File.Exists(AppPath)) return "Started Salad's service; couldn't find the Salad app at " + AppPath + ".";
            try
            {
                // A shortcut opened through Explorer runs the app with your normal rights rather than the watchdog's admin rights.
                // --hidden is what Salad's own "start with Windows" option uses. The shortcut lives in config\, not in Temp.
                var lnk = Paths.Cfg("Salad (GPU Watchdog).lnk");
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                sc.TargetPath = AppPath; sc.Arguments = "--hidden"; sc.WorkingDirectory = Path.GetDirectoryName(AppPath);
                sc.Save();
                // Salad sometimes shows its window anyway while it starts: keep it in the tray
                WindowGuard.HideNew(45, "Salad");
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lnk + "\"") { UseShellExecute = false });
                return "Salad is starting in the background (tray only).";
            }
            catch (Exception e) { return "Started Salad's service; couldn't open the app: " + e.Message; }
        }

        public static string Words(List<string> w) => w.Count <= 1 ? string.Join("", w) : string.Join(", ", w.Take(w.Count - 1)) + " and " + w.Last();

        // ---------------- Salad's local service API (same calls the Salad app makes) ----------------
        const string WidgetService = "salad.grpc.salad_bowl_widget.v1alpha.SaladBowlWidgetService";
        public const string StartMethod = "StartActiveWorkloads";
        public static string StopMethod = "StopActiveWorkloads";   // confirmed against the service in Phase 1 discovery

        static int GrpcPort()
        {
            using (var pipe = new NamedPipeClientStream(".", "salad-port", PipeDirection.InOut))
            {
                pipe.Connect(3000);
                var buf = new byte[256];
                ReadTimed(pipe, buf);                                   // "Connected"
                var ask = Encoding.ASCII.GetBytes("?ports\n");
                pipe.Write(ask, 0, ask.Length);
                var reply = Encoding.ASCII.GetString(buf, 0, ReadTimed(pipe, buf)).Trim();   // "+<gRPC-Web port>:<gRPC port>"
                if (!reply.StartsWith("+") || !int.TryParse(reply.Substring(reply.IndexOf(':') + 1), out int port))
                    throw new Exception("unexpected answer from Salad: " + reply);
                return port;
            }
        }
        static int ReadTimed(Stream s, byte[] buf)
        {
            var t = s.ReadAsync(buf, 0, buf.Length);
            if (!t.Wait(3000)) throw new Exception("Salad's service didn't answer");
            return t.Result;
        }

        /// <summary>Calls one of the widget service's methods that take and return google.protobuf.Empty.</summary>
        public static void Call(string method) => Unary("/" + WidgetService + "/" + method, new byte[0], true);

        /// <summary>One gRPC unary call over plain HTTP/2 (hand-written frames; .NET Framework has no HTTP/2 client).</summary>
        public static byte[] Unary(string path, byte[] message, bool needAnswer, int maxMs = 0)
        {
            using (var tcp = new TcpClient())
            {
                tcp.ReceiveTimeout = 5000; tcp.SendTimeout = 5000;
                tcp.Connect("127.0.0.1", GrpcPort());
                var s = tcp.GetStream();
                var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
                s.Write(preface, 0, preface.Length);
                Frame(s, 4, 0, 0, new byte[0]);                         // SETTINGS, defaults
                var h = new MemoryStream();
                Header(h, ":method", "POST"); Header(h, ":scheme", "http"); Header(h, ":path", path);
                Header(h, ":authority", "127.0.0.1"); Header(h, "content-type", "application/grpc"); Header(h, "te", "trailers");
                Frame(s, 1, 4, 1, h.ToArray());                         // HEADERS, END_HEADERS
                var body = new byte[5 + message.Length];
                body[1] = (byte)(message.Length >> 24); body[2] = (byte)(message.Length >> 16); body[3] = (byte)(message.Length >> 8); body[4] = (byte)message.Length;
                Buffer.BlockCopy(message, 0, body, 5, message.Length);
                Frame(s, 0, 1, 1, body);                                // DATA, END_STREAM

                var data = new MemoryStream();
                var until = maxMs > 0 ? DateTime.Now.AddMilliseconds(maxMs) : DateTime.MaxValue;
                if (maxMs > 0) tcp.ReceiveTimeout = maxMs;
                while (DateTime.Now < until)
                {
                    byte[] head;
                    try { head = Exact(s, 9); } catch when (maxMs > 0) { break; }
                    int len = head[0] << 16 | head[1] << 8 | head[2], type = head[3], flags = head[4];
                    int stream = (head[5] & 0x7F) << 24 | head[6] << 16 | head[7] << 8 | head[8];
                    var payload = Exact(s, len);
                    if (type == 4 && (flags & 1) == 0) Frame(s, 4, 1, 0, new byte[0]);
                    if (type == 6 && (flags & 1) == 0) Frame(s, 6, 1, 0, payload);
                    if (type == 3 || type == 7) throw new Exception("Salad's service dropped the request");
                    if (stream == 1 && type == 0) data.Write(payload, 0, payload.Length);
                    if (stream == 1 && (type == 0 || type == 1) && (flags & 1) != 0) break;
                }
                var all = data.ToArray();
                if (needAnswer && all.Length == 0) throw new Exception("Salad's service turned the request down");
                return maxMs > 0 ? all : all.Length > 5 ? all.Skip(5).ToArray() : new byte[0];
            }
        }

        static void Frame(Stream s, byte type, byte flags, int stream, byte[] payload)
        {
            var f = new byte[9 + payload.Length];
            f[0] = (byte)(payload.Length >> 16); f[1] = (byte)(payload.Length >> 8); f[2] = (byte)payload.Length;
            f[3] = type; f[4] = flags;
            f[5] = (byte)(stream >> 24); f[6] = (byte)(stream >> 16); f[7] = (byte)(stream >> 8); f[8] = (byte)stream;
            Buffer.BlockCopy(payload, 0, f, 9, payload.Length);
            s.Write(f, 0, f.Length);
        }
        static void Header(MemoryStream m, string name, string value)
        {
            m.WriteByte(0);   // literal without indexing, no Huffman
            foreach (var text in new[] { name, value })
            {
                var b = Encoding.ASCII.GetBytes(text);
                if (b.Length < 127) m.WriteByte((byte)b.Length);
                else { m.WriteByte(127); int v = b.Length - 127; while (v >= 128) { m.WriteByte((byte)(v % 128 + 128)); v /= 128; } m.WriteByte((byte)v); }
                m.Write(b, 0, b.Length);
            }
        }
        static byte[] Exact(Stream s, int n)
        {
            var buf = new byte[n];
            for (int got = 0; got < n;)
            {
                int r = s.Read(buf, got, n - got);
                if (r == 0) throw new Exception("Salad's service closed the connection");
                got += r;
            }
            return buf;
        }

        /// <summary>Discovery: the service's live state stream (what Salad's own window shows), read for a few seconds. Read-only.</summary>
        public static byte[] MonitorState(int ms) => Unary("/" + WidgetService + "/MonitorState", new byte[0], false, ms);
        public static IEnumerable<string> PrintableRuns(byte[] b) => Strings(b);

        /// <summary>Discovery only: asks the service which methods it offers (gRPC server reflection, if enabled). Read-only.</summary>
        public static List<string> ListMethods()
        {
            var names = new List<string>();
            foreach (var refl in new[] { "grpc.reflection.v1alpha.ServerReflection", "grpc.reflection.v1.ServerReflection" })
            {
                try
                {
                    var services = Strings(Unary("/" + refl + "/ServerReflectionInfo", new byte[] { 0x3A, 0x00 }, true))
                        .Where(x => x.Contains(".") && !x.StartsWith("grpc.reflection")).Distinct().ToList();
                    foreach (var svc in services)
                    {
                        names.Add("service " + svc);
                        var req = new List<byte> { 0x22, (byte)svc.Length }; req.AddRange(Encoding.ASCII.GetBytes(svc));
                        foreach (var str in Strings(Unary("/" + refl + "/ServerReflectionInfo", req.ToArray(), true)))
                            if (str.Length > 3 && char.IsUpper(str[0]) && !str.Contains(".") && !str.Contains("/")) names.Add("  " + str);
                    }
                    if (names.Count > 0) break;
                }
                catch (Exception e) { names.Add(refl + ": " + e.Message); }
            }
            return names.Distinct().ToList();
        }
        static IEnumerable<string> Strings(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (var c in b)
            {
                if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
                else { if (sb.Length >= 4) yield return sb.ToString(); sb.Clear(); }
            }
            if (sb.Length >= 4) yield return sb.ToString();
        }
    }
}
