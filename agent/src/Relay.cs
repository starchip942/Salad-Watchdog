using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GpuWatchdog
{
    /// <summary>
    /// The PC's one outbound connection: a WebSocket to the Cloudflare relay. No open ports on your network.
    /// Reconnects by itself with backoff. Commands must carry a valid signature, be under 30 s old and never repeat.
    /// </summary>
    sealed class Relay
    {
        public volatile bool Connected;
        public volatile int Viewers;
        public long ConnectedSince;
        public string LastError = "";
        public Func<string, Dictionary<string, object>, (bool ok, string msg)> OnCommand;
        public Action OnConnected;

        ClientWebSocket ws;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, long> nonces = new Dictionary<string, long>();
        CancellationTokenSource cts = new CancellationTokenSource();
        int relayPwVer = -1;

        public void Start() => Task.Run(Loop);
        public void Stop() { cts.Cancel(); try { ws?.Abort(); } catch { } }

        async Task Loop()
        {
            int backoff = 1000;
            bool loggedDown = false;
            while (!cts.IsCancellationRequested)
            {
                var url = Conf.S.RelayUrl?.Trim();
                if (string.IsNullOrEmpty(url)) { LastError = "No relay address set yet."; await Task.Delay(5000); continue; }
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + Conf.S.AgentKey);
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    var wsUrl = new Uri(url.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/agent");
                    using (var t = new CancellationTokenSource(15000)) await ws.ConnectAsync(wsUrl, t.Token);
                    Connected = true; ConnectedSince = Time.Now; backoff = 1000; LastError = "";
                    if (loggedDown) Log.Debug("Relay reconnected."); else Log.Debug("Connected to the relay.");
                    loggedDown = false;
                    OnConnected?.Invoke();
                    await Receive();
                }
                catch (Exception e) { LastError = (e.InnerException ?? e).Message; }
                if (Connected) { Log.Debug("Relay disconnected, retrying."); loggedDown = true; }
                else if (!loggedDown && backoff >= 8000) { Log.Debug("Can't reach the relay (" + LastError + "). Retrying."); loggedDown = true; }
                Connected = false; Viewers = 0;
                try { ws?.Dispose(); } catch { }
                await Task.Delay(backoff);
                backoff = Math.Min(backoff * 2, 60000);
            }
        }

        async Task Receive()
        {
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                Dictionary<string, object> m;
                try { m = Json.Obj(sb.ToString()); } catch { continue; }
                Handle(m);
            }
        }

        void Handle(Dictionary<string, object> m)
        {
            switch (Json.Str(m, "type"))
            {
                case "hello":
                    Viewers = (int)(Json.Num(m, "viewers") ?? 0);
                    relayPwVer = (int)(Json.Num(m, "passwordVer") ?? 0);
                    SyncPassword();
                    break;
                case "viewers": Viewers = (int)(Json.Num(m, "n") ?? 0); break;
                case "password_ack":
                    if (Json.Bool(m, "ok") == true)
                    {
                        relayPwVer = (int)(Json.Num(m, "ver") ?? 0);
                        if (Conf.S.PwSyncedVer != relayPwVer) { Conf.S.PwSyncedVer = relayPwVer; Conf.Save(); Log.Info("Website password synced; old website sessions were signed out.", true); }
                    }
                    else Log.Warn("The relay didn't accept the password hash: " + Json.Str(m, "msg"), true);
                    break;
                case "command": Task.Run(() => RunCommand(m)); break;
            }
        }

        /// <summary>Sends the password HASH (never the password) when the relay's copy is older than ours.</summary>
        public void SyncPassword()
        {
            var s = Conf.S;
            if (!Connected || relayPwVer == s.PwVer && s.PwSyncedVer == s.PwVer) return;
            Send(new Dictionary<string, object> { { "type", "password_hash" }, { "salt", s.PwSalt }, { "hash", s.PwHash }, { "iter", s.PwIter }, { "ver", s.PwVer } });
        }

        void RunCommand(Dictionary<string, object> m)
        {
            string id = Json.Str(m, "id"), cmd = Json.Str(m, "cmd"), args = Json.Str(m, "args") ?? "{}", nonce = Json.Str(m, "nonce"), sig = Json.Str(m, "sig");
            long ts = (long)(Json.Num(m, "ts") ?? 0);
            string why = Verify(id, cmd, args, ts, nonce, sig);
            if (why != null)
            {
                Log.Warn("Rejected a website command (" + why + ").", true);
                Send(new Dictionary<string, object> { { "type", "result" }, { "id", id }, { "ok", false }, { "msg", "Rejected: " + why } });
                return;
            }
            (bool ok, string msg) res;
            try { res = OnCommand(cmd, Json.Obj(args) ?? new Dictionary<string, object>()); }
            catch (Exception e) { res = (false, "Error: " + e.Message); }
            Send(new Dictionary<string, object> { { "type", "result" }, { "id", id }, { "ok", res.ok }, { "msg", res.msg } });
        }

        public string Verify(string id, string cmd, string args, long ts, string nonce, string sig)
        {
            if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(nonce)) return "unsigned";
            var msg = string.Join("|", id, cmd, args, ts.ToString(), nonce);
            // the relay signs with our key, or (when the PC claimed the relay) with the SHA-256 of our key
            if (!FixedEq(Hmac(Conf.S.AgentKey, msg), sig) && !FixedEq(Hmac(Sha256Hex(Conf.S.AgentKey), msg), sig)) return "bad signature";
            if (Math.Abs(Time.Now - ts) > 30) return "too old";
            lock (nonces)
            {
                foreach (var k in nonces.Where(kv => kv.Value < Time.Now - 120).Select(kv => kv.Key).ToList()) nonces.Remove(k);
                if (nonces.ContainsKey(nonce)) return "replayed";
                nonces[nonce] = Time.Now;
            }
            return null;
        }

        public static string Hmac(string key, string msg)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(msg)).Select(b => b.ToString("x2")));
        }
        static string Sha256Hex(string s)
        {
            using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }
        static bool FixedEq(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        public void Send(object msg)
        {
            var w = ws;
            if (w == null || w.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(Json.Write(msg));
            Task.Run(async () =>
            {
                await sendLock.WaitAsync();
                try { if (w.State == WebSocketState.Open) await w.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { }
                finally { sendLock.Release(); }
            });
        }
    }
}
