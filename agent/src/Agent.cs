using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GpuWatchdog
{
    /// <summary>The watchdog's brain: sensors, safety stop, Salad modes, Game Mode, schedules, telemetry.</summary>
    sealed class Agent
    {
        public static Agent I;
        public readonly Relay Relay = new Relay();
        readonly Sensors sensors = new Sensors();
        readonly object G = new object();          // guards the state below
        readonly object ActionGate = new object();  // one Salad action at a time

        public Reading Last = new Reading();
        public string SaladState = "unknown";
        public bool Hold;                           // manual pause/close or scheduled off: Game Mode won't restart Salad
        public bool PausedByUs;
        public bool PausedForGame;
        public List<RunningGame> ActiveGames = new List<RunningGame>();
        public Lockout Lock = new Lockout();
        public ScheduleFile Sched = new ScheduleFile();
        public long ShutdownAt;
        public EarningsData Earn;
        public string EarnStatus = "Not read yet.";
        public bool SensorFail;
        public bool OldWatchdogRunning;
        public bool PauseUnsupported;
        readonly List<double[]> recent = new List<double[]>();   // [t, core, hotspot] for ~10 minutes

        long gamesClosedAt;
        long chopUntil, chopNextTry;
        int overLimit, nullTemps, lhmMisses;
        long lastTelemetry, lastSaladCheck, lastGameCheck, lastLockoutCheck, lastOldCheck;
        bool distroRunning;
        Thread loop;
        volatile bool stopping;

        public event Action<string, string> Notify;   // (title, text) for the tray balloon

        // ---------------- startup ----------------
        public void Start()
        {
            I = this;
            Lock = Json.Load<Lockout>(Paths.Cfg("lockout.json"));
            Sched = Json.Load<ScheduleFile>(Paths.Cfg("schedules.json"));
            var st = Json.Load<Dictionary<string, object>>(Paths.Cfg("state.json"));
            Hold = Json.Bool(st, "hold") ?? false;
            PausedForGame = Json.Bool(st, "pausedForGame") ?? false;
            var e0 = Json.Load<EarningsData>(Paths.Cfg("earnings.json"));
            if (e0.balanceUSD != null) { LastPayoutUSD = e0.lastPayoutUSD; LastPayoutAt = e0.lastPayoutAt; if (e0.nextHourAt > 0 && e0.nextHourFrom == "salad" && false) { nhLow = e0.estNextHourLowUSD; nhHigh = e0.estNextHourHighUSD; nhAt = e0.nextHourAt; } Earn = e0; EarnStatus = "Last read " + Time.Short(e0.updatedAt) + "."; }

            foreach (var s in Sched.Items.Where(s => !s.Missed && s.At < Time.Now - 60))
            {
                s.Missed = true;
                Log.Warn($"Missed schedule: {Label(s.Action)} at {Time.Short(s.At)} (the Watchdog wasn't running then). It was not done late.");
            }
            SaveSchedules();

            sensors.Open();
            var r = sensors.Read();
            Last = r;
            Log.Debug($"Sensors: GPU {(sensors.NvmlOk ? "OK" : "unavailable (" + sensors.NvmlError + ")")}, hotspot {(r.GpuHotspot != null ? "OK" : "N/A")}, CPU {(r.CpuTemp != null ? "OK" : "N/A")}, " +
                     $"RAM {r.Ram.Count(x => x.tempC != null)}/2, SSD {r.Ssd.Count(x => x.tempC != null)}/{Math.Max(2, r.Ssd.Count)}.");
            if (Lock.Active) Log.Error($"Emergency lockout is still active (GPU reached {Lock.TempC:0} °C at {Time.Short(Lock.At)}). Salad stays off until you clear it in the panel.");
            if (Hold) Log.Info("Salad hold still active from earlier: it won't restart after games until you press Start chopping.");
            Games.Rescan(true);

            Relay.OnCommand = (cmd, args) => Command(cmd, args, "website");
            Relay.OnConnected = () =>
            {
                Relay.Send(new Dictionary<string, object> { { "type", "info" }, { "viewRequiresPassword", Conf.S.ViewRequiresPassword } });
                Relay.Send(new Dictionary<string, object> { { "type", "events_sync" }, { "events", Log.All().Where(Log.Public).Skip(Math.Max(0, Log.All().Count - 300)).ToList() } });
                SendTelemetry();
            };
            Log.Added += e => { if (Log.Public(e)) Relay.Send(new Dictionary<string, object> { { "type", "event" }, { "event", e } }); };
            Relay.Start();

            loop = new Thread(Loop) { IsBackground = true, Name = "watchdog" };
            loop.Start();
            Task.Run(EarningsLoop);
            // Phase 1 report, written once per version in the background so it can be read without asking you to run anything
            Task.Run(async () =>
            {
                await Task.Delay(20000);
                var rep = Path.Combine(Paths.Logs, "discovery-report.txt");
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (File.Exists(rep) && File.GetLastWriteTime(rep) > File.GetLastWriteTime(exe)) return;
                try { Discovery.Run(true, sensors); Log.Debug("Hardware check written to logs\\discovery-report.txt."); } catch (Exception e) { Log.Warn("Hardware check failed: " + e.Message); }
            });
        }

        public void Stop()
        {
            stopping = true;
            try { SaladPeek.Release(); } catch { }
            Relay.Stop();
            try { loop?.Join(3000); } catch { }
            sensors.Dispose();
        }

        // ---------------- main loop (1 Hz, always: the safety check never sleeps) ----------------
        void Loop()
        {
            while (!stopping)
            {
                var t0 = DateTime.Now;
                try { Tick(); } catch (Exception e) { Log.Error("Something went wrong: " + e.Message); }
                var wait = 1000 - (int)(DateTime.Now - t0).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        void Tick()
        {
            long now = Time.Now;
            Reading r;
            lock (sensors) r = sensors.Read();
            // LibreHardwareMonitor sometimes loses its driver access (e.g. another tool closed it): reopen it
            if (r.CpuTemp == null && r.Ram.Count == 0) { if (++lhmMisses == 15) { lock (sensors) sensors.ReopenLhm(); Log.Warn("CPU/RAM/SSD sensors stopped answering; reconnected them."); } }
            else lhmMisses = 0;
            lock (G)
            {
                Last = r;
                recent.Add(new[] { now, r.GpuCore ?? double.NaN, r.GpuHotspot ?? double.NaN });
                if (recent.Count > 600) recent.RemoveAt(0);
            }
            Log.TempSample(r.GpuTemp);
            Safety(r, now);

            if (now - lastOldCheck >= 10) { lastOldCheck = now; CheckOldWatchdog(); CheckForUpdate(); }
            // Salad check every 5 s; right away when the GPU suddenly goes quiet (load or temperature drop), so a stop shows at once
            lock (G)
            {
                loadHistory.Add(new[] { (double)now, r.GpuUtil ?? 0, r.GpuPowerW ?? 0, r.GpuTemp ?? 0 });
                loadHistory.RemoveAll(x => x[0] < now - 30);
            }
            bool suddenDrop = false;
            lock (G)
            {
                var past = loadHistory.Where(x => x[0] <= now - 3 && x[0] >= now - 12).ToList();
                if (past.Count > 0 && r.GpuUtil != null)
                    suddenDrop = (past.Max(x => x[1]) >= 50 && r.GpuUtil < 15) || (past.Max(x => x[2]) >= 150 && r.GpuPowerW < 80)
                                 || (r.GpuTemp != null && past.Max(x => x[3]) - r.GpuTemp.Value >= 4)
                                 || (past.Max(x => x[1]) < 15 && r.GpuUtil >= 50 && SaladState != "chopping");   // and when it starts again
            }
            if (now - lastSaladCheck >= 5 || (suddenDrop && now - lastSaladCheck >= 1)) { lastSaladCheck = now; distroRunning = Salad.DistroRunning(); UpdateSaladState(); }
            if (now - lastGameCheck >= 2) { lastGameCheck = now; GameCheck(now); }
            if (Lock.Active && now - lastLockoutCheck >= 3) { lastLockoutCheck = now; EnforceLockout(); }
            ChopRetry(now);
            Schedules(now);
            if (ShutdownAt > 0 && now > ShutdownAt + 120) ShutdownAt = 0;

            int interval = Relay.Viewers > 0 ? 1 : Math.Max(5, Conf.S.SampleSecondsIdle);
            if (now - lastTelemetry >= interval) SendTelemetry();
        }

        // ---------------- safety stop ----------------
        void Safety(Reading r, long now)
        {
            var s = Conf.S;
            // CPU: its own check, so a missing GPU reading never hides a hot CPU
            if (s.SafetyEnabled && !Lock.Active && r.CpuTemp != null && r.CpuTemp >= s.SafetyCpuLimitC)
            {
                if (++cpuOver >= Math.Max(1, s.SafetySamples)) { cpuOver = 0; Emergency("cpu", r.CpuTemp.Value, now); return; }
            }
            else cpuOver = 0;
            double? core = r.GpuCore, hot = r.GpuHotspot;
            bool noData = s.SafetySensor == "hotspot" ? hot == null : s.SafetySensor == "either" ? core == null && hot == null : core == null;
            if (noData)
            {
                // Never act on missing data: warn instead
                overLimit = 0;
                if (++nullTemps == 3) { SensorFail = true; Log.Warn("Can't read the GPU temperature, so the safety stop can't check it. Salad is not stopped on missing data."); Notify?.Invoke("GPU Watchdog", "Can't read the GPU temperature."); }
                return;
            }
            if (nullTemps >= 3) Log.Info("GPU temperature readable again.");
            nullTemps = 0; SensorFail = false;
            if (!s.SafetyEnabled || Lock.Active) { overLimit = 0; return; }

            bool over = false; string which = ""; double val = 0;
            if ((s.SafetySensor == "core" || s.SafetySensor == "either") && core >= s.SafetyCoreLimitC) { over = true; which = "core"; val = core.Value; }
            if ((s.SafetySensor == "hotspot" || s.SafetySensor == "either") && hot >= s.SafetyHotspotLimitC) { over = true; which = "hotspot"; val = hot.Value; }
            overLimit = over ? overLimit + 1 : 0;
            if (overLimit < Math.Max(1, s.SafetySamples)) return;
            overLimit = 0;
            Emergency(which, val, now);
        }

        int cpuOver;
        static string What(string sensor) => sensor == "cpu" ? "CPU" : "GPU " + sensor;
        void Emergency(string sensor, double temp, long now)
        {
            lock (G)
            {
                Lock = new Lockout { Active = true, At = now, TempC = temp, Sensor = sensor, RecentTemps = recent.Skip(Math.Max(0, recent.Count - 300)).ToList() };
                Hold = true; PausedForGame = false; chopUntil = 0;
                Json.Save(Paths.Cfg("lockout.json"), Lock);
                SaveState();
            }
            Log.Error($"{What(sensor)} reached {temp:0} °C (limit {(sensor == "cpu" ? Conf.S.SafetyCpuLimitC : sensor == "hotspot" ? Conf.S.SafetyHotspotLimitC : Conf.S.SafetyCoreLimitC)} °C). Emergency stop: closing Salad and all WSL/Linux tasks.");
            string res;
            lock (ActionGate) { res = Salad.Close(3000); res += " " + Salad.ShutdownAllWsl(); }
            Log.Error($"{res} Salad locked out at {temp:0} °C. Check cooling (fans, dust, airflow), then clear it in the panel on this PC.");
            Notify?.Invoke("GPU Watchdog: Salad locked out", $"{What(sensor)} reached {temp:0} °C. Salad was closed. Check cooling, then clear the lockout in the panel.");
            UpdateSaladState();
            SendTelemetry();
        }

        void EnforceLockout()
        {
            if (!Salad.AnyRunning() && !distroRunning) return;
            Log.Warn("Salad started again during the emergency lockout; closing it again.");
            lock (ActionGate) { Salad.Close(1000); Salad.ShutdownAllWsl(); }
        }

        public (bool ok, string msg) ClearLockout()
        {
            lock (G)
            {
                if (!Lock.Active) return (true, "No lockout is active.");
                Lock = new Lockout();
                Json.Save(Paths.Cfg("lockout.json"), Lock);
            }
            Log.Info("Emergency lockout cleared on the PC (cooling checked). Salad may run again; press Start chopping to restart it.");
            UpdateSaladState(); SendTelemetry();
            return (true, "Lockout cleared. Salad may run again; it's still on hold until you press Start chopping.");
        }

        public List<double[]> LockoutTemps() { lock (G) return Lock.Active ? Lock.RecentTemps : recent.ToList(); }

        // ---------------- Salad state & actions ----------------
        void UpdateSaladState()
        {
            string st;
            if (Lock.Active) st = "locked_out";
            else if (!Salad.AnyRunning()) st = "not_running";
            else if (PausedByUs) st = "paused";
            // chopping = Salad's job VM is up AND the GPU is actually working (stopping a job in Salad leaves the VM running)
            else if (distroRunning && GpuBusy()) st = "chopping";
            else st = "idle";
            if (st != SaladState && SaladState != "unknown" && st == "not_running" && !Lock.Active) PausedByUs = false;
            if (st != SaladState && SaladState != "unknown")
            {
                SaladState = st;
                stateSince = Time.Now;
                SendTelemetry();      // tell the website straight away
            }
            SaladState = st;
            // the log only gets a state once it has held for 5 minutes (short blips between jobs aren't worth a line)
            if (st != loggedState && st != "unknown" && Time.Now - stateSince >= 300)
            {
                if (loggedState != null) Log.Info("Salad: " + Nice(loggedState) + " → " + Nice(st) + ".");
                loggedState = st;
            }
        }

        long stateSince = Time.Now; string loggedState;
        static string Nice(string st) => st == "not_running" ? "not running" : st == "locked_out" ? "locked out" : st;

        /// <summary>GPU load over the last ~20 seconds says whether a Salad job is really running.</summary>
        bool GpuBusy()
        {
            lock (G)
            {
                var cut = Time.Now - 20;
                return loadHistory.Any(x => x[0] >= cut && (x[1] >= 35 || x[2] >= 120));
            }
        }
        readonly List<double[]> loadHistory = new List<double[]>();   // [t, util %, power W]

        public (bool ok, string msg) ChopNow(string who)
        {
            if (Lock.Active) return (false, "Refused: Salad is locked out after the GPU overheated. Clear it on the PC first.");
            if (OldWatchdogRunning) Log.Warn("The old GPU Watchdog is also running; it may stop Salad again.");
            string msg;
            lock (ActionGate)
            {
                Hold = false; PausedByUs = false; PausedForGame = false; SaveState();
                if (!Salad.AnyRunning())
                {
                    msg = Salad.Launch();
                    // the service needs about half a minute to sign in, and a Start sent before that may be lost
                    chopNextTry = Time.Now + 45; chopUntil = Time.Now + 240;
                    Log.Info($"Salad starting ({who.ToLowerInvariant()}); chopping in about a minute.");
                    return (true, msg + " It will start chopping in about a minute.");
                }
                try
                {
                    WindowGuard.HideNew(8, "Salad");
                    Salad.Call(Salad.StartMethod);
                    Log.Info($"Salad: start chopping ({who.ToLowerInvariant()}).");
                    chopUntil = 0; chopVerifyUntil = Time.Now + 600; chopNextTry = Time.Now + 60;
                    msg = "Salad has started chopping.";
                }
                catch (Exception e)
                {
                    chopNextTry = Time.Now + 5; chopUntil = Time.Now + 60;
                    Log.Debug($"{who}: Start chopping not taken yet ({e.Message}); retrying.");
                    msg = "Asked Salad to start; retrying for a minute.";
                }
            }
            lastSaladCheck = 0; UpdateSaladState();
            return (true, msg);
        }

        long chopVerifyUntil, lastGameKill;
        void ChopRetry(long now)
        {
            if (chopVerifyUntil > 0 && chopUntil == 0)
            {
                if (Lock.Active || Hold || ActiveGames.Count > 0 || distroRunning || now > chopVerifyUntil) chopVerifyUntil = 0;   // running, or give up after 10 min
                else if (now >= chopNextTry) { chopUntil = now + 1; chopNextTry = now; }   // resend Start below
            }
            if (chopUntil == 0 || now < chopNextTry) return;
            if (Lock.Active || Hold) { chopUntil = 0; return; }
            lock (ActionGate)
            {
                try
                {
                    WindowGuard.HideNew(8, "Salad");
                    Salad.Call(Salad.StartMethod);
                    Log.Debug("Salad accepted Start.");
                    PausedByUs = false;
                    // accepted isn't always acted on (Salad still signing in): check it really starts, else send Start again
                    if (chopVerifyUntil == 0) chopVerifyUntil = now + 600;
                    chopUntil = 0; chopNextTry = now + 60;
                }
                catch (Exception e)
                {
                    if (now >= chopUntil)
                    {
                        if (chopVerifyUntil == 0) Log.Warn("Couldn't start Salad chopping (Chop Now): " + e.Message); else Log.Debug("Chop Now not taken yet: " + e.Message);
                        chopUntil = 0; chopNextTry = now + 20;
                    }
                    else chopNextTry = now + 10;
                }
            }
        }

        /// <summary>Stop chopping, Salad stays open. Falls back to closing Salad if this Salad version has no Stop.</summary>
        public (bool ok, string msg) Pause(string who, bool setHold)
        {
            lock (ActionGate)
            {
                chopUntil = 0;
                if (setHold) { Hold = true; SaveState(); }
                if (!Salad.AnyRunning()) { return (true, "Salad is not running."); }
                try
                {
                    Salad.Call(Salad.StopMethod);
                    PausedByUs = true;
                    Log.Info($"Salad: chopping paused ({who.TrimEnd(':', ' ', '→').ToLowerInvariant()}).");
                    UpdateSaladState();
                    return (true, "Chopping paused. Salad stays open.");
                }
                catch (Exception e)
                {
                    PauseUnsupported = true;
                    var res = Salad.Close();
                    Log.Warn($"{who} Pause chopping isn't available from Salad's service ({e.Message}), so Salad was closed instead. {res}");
                    UpdateSaladState();
                    return (true, "Salad couldn't pause, so it was closed instead. " + res);
                }
            }
        }

        public (bool ok, string msg) CloseSalad(string who)
        {
            string res;
            lock (ActionGate)
            {
                chopUntil = 0; Hold = true; PausedByUs = false; PausedForGame = false; SaveState();
                res = Salad.Close();
            }
            Log.Info($"Salad closed ({who.ToLowerInvariant()}).");
            lastSaladCheck = 0; distroRunning = false; UpdateSaladState();
            return (true, res);
        }

        public (bool ok, string msg) ClearHold()
        {
            Hold = false; SaveState();
            Log.Debug("Hold cleared on the PC.");
            return (true, "Hold cleared.");
        }

        // ---------------- Game Mode ----------------
        void GameCheck(long now)
        {
            Games.Rescan();
            // games always stop Salad (no on/off switch)
            var games = Games.Running();
            var before = ActiveGames;
            ActiveGames = games;
            if (OldWatchdogRunning) return;   // two watchdogs would fight over Salad

            foreach (var g in games.Where(g => !before.Any(b => b.Name == g.Name)))
            {
                gamesClosedAt = 0;
                if (Lock.Active) { Log.Debug($"{g.Name} started; Salad is locked out."); continue; }
                if (Salad.AnyRunning() || distroRunning)
                {
                    // a game always knocks Salad out completely (app, service and its GPU jobs), at once
                    string res; lock (ActionGate) { chopUntil = 0; res = Salad.Close(0); }
                    PausedForGame = true; SaveState();
                    Log.Info($"{g.Name} started: Salad force-closed.");
                    lastSaladCheck = 0; distroRunning = Salad.DistroRunning(); UpdateSaladState(); SendTelemetry();
                }
                else Log.Debug($"{g.Name} started; Salad wasn't running.");
            }
            // Salad came back while a game is still open (e.g. it restarted itself): close it again
            if (games.Count > 0 && PausedForGame && !Lock.Active && Salad.AnyRunning() && now - lastGameKill > 15)
            {
                lastGameKill = now;
                lock (ActionGate) Salad.Close(0);
                Log.Debug($"Salad came back while {games[0].Name} is open; closed it again.");
            }
            foreach (var g in before.Where(b => !games.Any(x => x.Name == b.Name)))
                Log.Debug(games.Count == 0 ? $"{g.Name} closed; waiting {Conf.S.GameResumeDelaySec} s." : $"{g.Name} closed; {games[0].Name} still running.");
            if (before.Count > 0 && games.Count == 0) gamesClosedAt = now;

            if (games.Count == 0 && gamesClosedAt > 0 && now - gamesClosedAt >= Conf.S.GameResumeDelaySec)
            {
                gamesClosedAt = 0;
                if (!PausedForGame) return;
                if (Lock.Active) { Log.Info("Games closed; Salad stays off (locked out)."); PausedForGame = false; SaveState(); return; }
                if (Hold) { Log.Info("Games closed; Salad stays paused (you paused it)."); PausedForGame = false; SaveState(); return; }
                PausedForGame = false;
                Log.Info("Games closed → Salad started in Chop Now.");
                ChopNow("Games closed");
            }
        }

        static readonly DateTime Started = DateTime.Now;
        public event Action UpdateReady;
        bool updateSignalled;
        /// <summary>A newer GPU-Watchdog-Agent.exe was put in place: restart into it by ourselves (no prompt needed, we already run as admin).</summary>
        void CheckForUpdate()
        {
            // relay address written into config\agent.config.json from outside (setup): pick it up without a restart
            try
            {
                var onDisk = Json.Load<Settings>(Conf.FilePath);
                if (!string.IsNullOrWhiteSpace(onDisk.RelayUrl) && onDisk.RelayUrl != Conf.S.RelayUrl)
                {
                    Conf.S.RelayUrl = onDisk.RelayUrl.Trim();
                    Log.Debug("Website relay set to " + Conf.S.RelayUrl + ".");
                }
            }
            catch { }
            if (updateSignalled) return;
            try
            {
                var exe = Path.Combine(Paths.AgentDir, "GPU-Watchdog-Agent.exe");
                if (File.Exists(exe) && File.GetLastWriteTime(exe) > Started.AddSeconds(5) && (DateTime.Now - File.GetLastWriteTime(exe)).TotalSeconds > 5)
                { updateSignalled = true; Log.Debug("New version: restarting into it."); UpdateReady?.Invoke(); }
            }
            catch { }
        }

        void CheckOldWatchdog()
        {
            bool running = false;
            try { if (Mutex.TryOpenExisting("Global\\SaladGpuWatchdog", out var m)) { running = true; m.Dispose(); } } catch (UnauthorizedAccessException) { running = true; }
            if (running && !OldWatchdogRunning)
            {
                Log.Warn("The old GPU Watchdog is running. Close it (tray icon → Exit): two watchdogs would fight over Salad.");
                Notify?.Invoke("GPU Watchdog", "The old GPU Watchdog is running. Please close it from its tray icon.");
            }
            else if (!running && OldWatchdogRunning) Log.Debug("The old GPU Watchdog was closed.");
            OldWatchdogRunning = running;
        }

        // ---------------- schedules & shutdown ----------------
        void Schedules(long now)
        {
            List<ScheduleItem> due;
            lock (G) due = Sched.Items.Where(s => !s.Missed && s.At <= now).ToList();
            foreach (var s in due)
            {
                lock (G) Sched.Items.Remove(s);
                SaveSchedules();
                if (s.Action == "pc_off") { Log.Info("Scheduled shutdown time reached."); PcOff("Schedule"); }
                else
                {
                    var r = CloseSalad("Schedule");
                }
            }
        }

        public (bool ok, string msg) PcOff(string who)
        {
            var o = Proc.Run(Proc.System32("shutdown.exe"), "/s /t 30 /c \"GPU Watchdog: shutting down in 30 seconds. Cancel it from the GPU Watchdog panel or website.\"", 8000) ?? "";
            if (o.Contains("1190")) return (false, "A shutdown is already scheduled.");
            ShutdownAt = Time.Now + 30;
            Log.Warn($"PC turning off in 30 s ({who.ToLowerInvariant()}).");
            SendTelemetry();
            return (true, "Shutting down in 30 seconds. You can cancel it.");
        }

        public (bool ok, string msg) CancelShutdown(string who)
        {
            Proc.Run(Proc.System32("shutdown.exe"), "/a", 8000);
            bool had = ShutdownAt > 0;
            ShutdownAt = 0;
            Log.Info($"PC shutdown cancelled ({who.ToLowerInvariant()}).");
            SendTelemetry();
            return (true, had ? "Shutdown cancelled." : "No shutdown was pending (sent a cancel anyway).");
        }

        public (bool ok, string msg) AddSchedule(Dictionary<string, object> a, string who)
        {
            var action = Json.Str(a, "action");
            if (action != "pc_off" && action != "salad_off") return (false, "Unknown action.");
            long at;
            var atLocal = Json.Str(a, "atLocal");
            if (!string.IsNullOrEmpty(atLocal))
            {
                if (!TimeSpan.TryParse(atLocal, Time.Inv, out var tod) || tod.TotalHours >= 24) return (false, "Couldn't read that time.");
                var when = DateTime.Today + tod;
                if (when <= DateTime.Now) when = when.AddDays(1);
                at = Time.Epoch(when);
            }
            else if (Json.Num(a, "inMinutes") is double m && m >= 1 && m <= 1440) at = Time.Now + (long)(m * 60);
            else if (Json.Num(a, "at") is double e && e > Time.Now) at = (long)e;
            else return (false, "Pick a time.");
            var item = new ScheduleItem { Id = Settings.NewSecret(6), Action = action, At = at, Source = who };
            lock (G) { Sched.Items.Add(item); Sched.Items.Sort((x, y) => x.At.CompareTo(y.At)); }
            SaveSchedules();
            Log.Info($"{Label(action)} scheduled for {Time.Short(at)}.");
            SendTelemetry();
            return (true, $"{Label(action)} scheduled for {Time.Short(at)}." + (action == "pc_off" ? " Once off, the PC can't be turned back on remotely." : ""));
        }

        public (bool ok, string msg) CancelSchedule(string id, string who)
        {
            ScheduleItem s;
            lock (G) { s = Sched.Items.FirstOrDefault(x => x.Id == id); if (s != null) Sched.Items.Remove(s); }
            if (s == null) return (false, "That schedule no longer exists.");
            SaveSchedules();
            Log.Debug($"{who}: cancelled {Label(s.Action)} at {Time.Short(s.At)}.");
            SendTelemetry();
            return (true, "Schedule cancelled.");
        }

        static string Label(string action) => action == "pc_off" ? "Turn PC off" : "Close Salad";
        void SaveSchedules() { lock (G) Json.Save(Paths.Cfg("schedules.json"), Sched); }
        void SaveState() => Json.Save(Paths.Cfg("state.json"), new Dictionary<string, object> { { "hold", Hold }, { "pausedForGame", PausedForGame } });

        // ---------------- commands (website and panel share these) ----------------
        public (bool ok, string msg) Command(string cmd, Dictionary<string, object> args, string who)
        {
            string w = who == "website" ? "Website" : "Panel";
            switch (cmd)
            {
                case "pc_off_now": return PcOff(w);
                case "shutdown_cancel": return CancelShutdown(w);
                case "salad_chop_now": return ChopNow(w);
                case "salad_pause": return Pause(w + ":", true);
                case "salad_off_now": return CloseSalad(w);
                case "afterburner_profile": { var r = Afterburner.Apply((int)(Json.Num(args, "profile") ?? 0), w); SendTelemetry(); return r; }
                case "schedule_add": return AddSchedule(args, w);
                case "schedule_cancel": return CancelSchedule(Json.Str(args, "id"), w);
            }
            return (false, "Unknown command.");
        }

        // ---------------- earnings (every 60 s, read-only) ----------------
        async Task EarningsLoop()
        {
            await Task.Delay(8000);
            while (!stopping)
            {
                try
                {
                    EarningsData d = null; string why = null;
                    if (Salad.ServiceRunning())
                    {
                        // 1) Salad's background service: works with Salad hidden in the tray
                        try { d = Earnings.FromSaladService(); } catch (Exception e) { why = "Salad's service: " + e.Message; }
                        // 2) the Earn tab, if its window is open (gives the exact next-hour range shown there)
                        if (Salad.AppRunning())
                        {
                            var text = Earnings.ReadSaladText();
                            var ui = text == null ? null : Earnings.Parse(text);
                            if (ui != null && Earnings.Complete(ui))
                            {
                                if (d == null) d = ui;
                            }
                            if (ui?.estNextHourHighUSD != null) SetNextHour(ui);
                            else if ((DateTime.Now - lastNhDiag).TotalMinutes >= 10)
                            {
                                lastNhDiag = DateTime.Now;
                                var at = text?.IndexOf("Next Hour", StringComparison.OrdinalIgnoreCase) ?? -1;
                                Log.Debug("Next hour not on Salad's page: " + (text == null ? "no text (" + Earnings.LastError + ")" : at < 0 ? text.Length + " chars, no 'Next Hour'" : "unparsed: " + text.Substring(at, Math.Min(60, text.Length - at))));
                            }
                            // Est. Next Hour: keep Salad's window parked invisibly so it can be read (see SaladPeek)
                            if (ui?.estNextHourHighUSD == null && ShouldPeek())
                            {
                                lastPeek = DateTime.Now;
                                var ok = SaladPeek.EnsureParked();
                                Log.Debug("Next-hour window: " + SaladPeek.LastResult);
                                bool got = false;
                                if (ok)
                                    for (int i = 0; i < 25 && !got; i++)
                                    {
                                        await Task.Delay(1000);
                                        var t2 = Earnings.ReadSaladText(); var pk = t2 == null ? null : Earnings.Parse(t2);
                                        if (pk?.estNextHourHighUSD != null) { SetNextHour(pk); got = true; }
                                    }
                                Log.Debug("Next hour from Salad: " + (got ? string.Format(Time.Inv, "${0:0.000}–${1:0.000}", nhLow, nhHigh) : "not readable (" + (Earnings.LastError ?? "no number on the page") + ")"));
                                // don't keep trying (and opening Salad's window) if it doesn't work on this PC
                                if (got) peekFails = 0; else if (++peekFails >= 3) { lastPeek = DateTime.Now.AddHours(2); peekFails = 0; }
                            }
                        }
                    }
                    else why = "Salad isn't running.";
                    if (d != null && Earnings.Complete(d))
                    {
                        d.updatedAt = Time.Now;
                        bool changed = Earn == null || Earn.balanceUSD != d.balanceUSD;
                        bool paid = RecordBalance(d.balanceUSD.Value);
                        // the next-hour range from Salad's window, kept until a newer one is read (shown for up to 2 hours)
                        if (nhAt > 0 && Time.Now - nhAt < 7200) { d.estNextHourLowUSD = nhLow; d.estNextHourHighUSD = nhHigh; d.nextHourAt = nhAt; d.nextHourFrom = "salad"; }
                        else
                        {
                            d.estNextHourLowUSD = d.estNextHourHighUSD = null; d.nextHourAt = 0;   // only Salad's own number is shown
                        }
                        d.lastPayoutUSD = LastPayoutUSD; d.lastPayoutAt = LastPayoutAt;
                        Earn = d; EarnStatus = "OK";
                        try { Json.Save(Paths.Cfg("earnings.json"), d); } catch { }
                        if (paid) Log.Earnings(string.Format(Time.Inv, "Payout +${0:0.0000} → balance ${1:0.0000}", LastPayoutUSD, d.balanceUSD));
                    }
                    else
                    {
                        why = why ?? "Salad didn't send its balance yet.";
                        if (why != EarnStatus) Log.Debug("Couldn't read earnings: " + why);
                        EarnStatus = why;
                    }
                }
                catch (Exception e) { EarnStatus = e.Message; }
                await Task.Delay(60000);
            }
        }

        double? nhLow, nhHigh; long nhAt; DateTime lastPeek = DateTime.MinValue; int peekFails; DateTime lastNhDiag = DateTime.MinValue;
        void SetNextHour(EarningsData x) { nhLow = x.estNextHourLowUSD; nhHigh = x.estNextHourHighUSD; nhAt = Time.Now; }
        /// <summary>Only while you're away (no mouse/keyboard for 3 min), no game running, Salad chopping, at most every 15 min.</summary>
        bool ShouldPeek()
        {
            if (SaladPeek.Parked) return (DateTime.Now - lastPeek).TotalMinutes >= 5;   // parked but no number yet: re-check now and then
            if ((DateTime.Now - lastPeek).TotalMinutes < 10) return false;
            return ActiveGames.Count == 0 && (SaladState == "chopping" || SaladState == "idle");
        }

        // ---------------- balance history (for the next-hour estimate) ----------------
        List<double[]> balances;
        public double? LastPayoutUSD; public long LastPayoutAt;
        bool RecordBalance(double b)
        {
            bool paid = false;
            if (balances == null) balances = Json.Load<List<double[]>>(Paths.Cfg("balance-history.json")) ?? new List<double[]>();
            long now = Time.Now;
            if (balances.Count > 0)
            {
                var prev = balances[balances.Count - 1][1];
                if (b < prev - 0.0001) balances.Clear();                       // you redeemed/withdrew: start over
                else if (b > prev + 0.00001) { LastPayoutUSD = Math.Round(b - prev, 4); LastPayoutAt = now; paid = true; }   // Salad credited you
            }
            balances.Add(new double[] { now, b });
            balances.RemoveAll(x => x[0] < now - 4 * 3600);
            try { Json.Save(Paths.Cfg("balance-history.json"), balances); } catch { }
            return paid;
        }

        /// <summary>Earned per hour over the last 1, 2 and 3 full hours (lowest and highest), or null with under an hour of data.</summary>
        Tuple<double, double> HourlyFromHistory()
        {
            if (balances == null || balances.Count < 2) return null;
            long now = Time.Now; double cur = balances[balances.Count - 1][1];
            var rates = new List<double>();
            for (int h = 1; h <= 3; h++)
            {
                var start = balances.FirstOrDefault(x => x[0] <= now - h * 3600 + 120 && x[0] >= now - h * 3600 - 600);
                var end = balances.LastOrDefault(x => x[0] <= now - (h - 1) * 3600 + 60);
                if (start == null || end == null || end[0] - start[0] < 3000) continue;
                rates.Add((end[1] - start[1]) * 3600 / (end[0] - start[0]));
            }
            if (rates.Count == 0) return null;
            return Tuple.Create(Math.Round(rates.Min(), 4), Math.Round(rates.Max(), 4));
        }

        // ---------------- telemetry ----------------
        public Dictionary<string, object> Telemetry(bool forWebsite)
        {
            var r = Last;
            var s = Conf.S;
            object earnings = null;
            if (Earn != null && (!forWebsite || s.ShowEarningsOnWebsite))
                earnings = new Dictionary<string, object> { { "balanceUSD", Earn.balanceUSD }, { "estNextHourLowUSD", Earn.estNextHourLowUSD }, { "estNextHourHighUSD", Earn.estNextHourHighUSD }, { "last24hUSD", Earn.last24hUSD }, { "updatedAt", Earn.updatedAt }, { "lastPayoutUSD", Earn.lastPayoutUSD }, { "lastPayoutAt", Earn.lastPayoutAt > 0 ? (object)Earn.lastPayoutAt : null }, { "nextHourFrom", Earn.nextHourFrom }, { "nextHourAt", Earn.nextHourAt > 0 ? (object)Earn.nextHourAt : null } };
            List<ScheduleItem> sched; lock (G) sched = Sched.Items.ToList();
            var t = new Dictionary<string, object>
            {
                { "v", 1 }, { "t", Time.Now },
                { "pc", new Dictionary<string, object> { { "tz", Time.IanaZone() }, { "tzOffsetMin", Time.OffsetMin() } } },
                { "gpu", new Dictionary<string, object> {
                    { "name", r.GpuName }, { "tempC", r.GpuTemp }, { "coreC", r.GpuCore }, { "hotspotC", r.GpuHotspot }, { "util", r.GpuUtil },
                    { "vramUsedMB", Rnd(r.VramUsedMB) }, { "vramTotalMB", Rnd(r.VramTotalMB) }, { "powerW", r.GpuPowerW },
                    { "fans", r.Fans.Select(f => new Dictionary<string, object> { { "pct", f.pct }, { "rpm", f.rpm } }).ToList() } } },
                { "cpu", new Dictionary<string, object> { { "name", r.CpuName }, { "tempC", r.CpuTemp }, { "powerW", r.CpuPowerW } } },
                { "ram", r.Ram.Select(x => new Dictionary<string, object> { { "name", x.name }, { "tempC", x.tempC } }).ToList() },
                { "ssd", r.Ssd.Select(x => new Dictionary<string, object> { { "name", x.name }, { "tempC", x.tempC } }).ToList() },
                { "salad", new Dictionary<string, object> {
                    { "state", SaladState }, { "hold", Hold },
                    { "gameMode", new Dictionary<string, object> { { "activeGame", ActiveGames.FirstOrDefault()?.Name } } },
                    { "earnings", earnings } } },
                { "safety", new Dictionary<string, object> {
                    { "limitC", s.SafetySensor == "hotspot" ? s.SafetyHotspotLimitC : s.SafetyCoreLimitC }, { "hotspotLimitC", s.SafetyHotspotLimitC },
                    { "sensor", s.SafetySensor }, { "enabled", s.SafetyEnabled }, { "sensorFail", SensorFail },
                    { "lockout", Lock.Active ? new Dictionary<string, object> { { "at", Lock.At }, { "tempC", Lock.TempC }, { "sensor", Lock.Sensor } } : null } } },
                { "afterburner", Afterburner.Info() },
                { "shutdown", new Dictionary<string, object> { { "pendingAt", ShutdownAt > 0 ? (object)ShutdownAt : null } } },
                { "schedules", sched.Select(x => new Dictionary<string, object> { { "id", x.Id }, { "action", x.Action }, { "at", x.At }, { "missed", x.Missed } }).ToList() },
            };
            return t;
        }
        static double? Rnd(double? v) => v.HasValue ? Math.Round(v.Value) : (double?)null;

        public void SendTelemetry()
        {
            lastTelemetry = Time.Now;
            if (Relay.Connected) Relay.Send(new Dictionary<string, object> { { "type", "telemetry" }, { "data", Telemetry(true) } });
        }
    }
}
