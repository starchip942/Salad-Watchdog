using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GpuWatchdog
{
    /// <summary>config\auto-off.json: the daily shutdown for the current Windows session (kept so a watchdog restart doesn't undo a cancel).</summary>
    public class AutoOffState
    {
        public long BootAt;          // when Windows started (epoch); another boot means a fresh 7:00
        public long At;              // next shutdown (UTC epoch seconds), 0 = none
        public bool Cancelled;       // skipped for this Windows session only
        public string By = "";       // who cancelled / delayed last
        public int Delays;           // how many times "Delay 1 hour" was pressed
    }

    /// <summary>
    /// Daily auto-shutdown (default 7:00 AM, local time). Every time the watchdog starts after a boot it sets the next 7:00;
    /// 5 minutes before, a big always-on-top warning appears on the PC and on the website (Cancel / Delay 1 hour);
    /// at the time, Salad is closed properly and Windows shuts down fully (not sleep), so the BIOS RTC alarm can wake it at 2:00.
    /// The time is checked against the real clock every second, so sleep or a busy PC can't make it drift.
    /// </summary>
    sealed class AutoOff
    {
        readonly object g = new object();
        AutoOffState s = new AutoOffState();
        public bool ShuttingDown { get; private set; }
        /// <summary>Runs the real shutdown on a background thread (the agent closes Salad, then Windows).</summary>
        public Action OnDue;
        public Action Changed;

        /// <summary>Seconds a missed time may be late and still run (more means the PC was asleep or the watchdog was off).</summary>
        public const int Grace = 120;

        static string FilePath => Paths.Cfg("auto-off.json");
        static bool Enabled => Conf.S.AutoOffEnabled;
        public static int WarnSeconds => Math.Max(60, Math.Min(3600, Conf.S.AutoOffWarnMinutes * 60));

        public long At { get { lock (g) return s.At; } }
        public bool Cancelled { get { lock (g) return s.Cancelled; } }
        public int Delays { get { lock (g) return s.Delays; } }

        // ---------------- pure time logic (also used by the self-test) ----------------
        public static bool TryTime(string hhmm, out TimeSpan tod)
        {
            if (TimeSpan.TryParseExact((hhmm ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out tod) && tod.TotalHours < 24 && tod >= TimeSpan.Zero) return true;
            tod = new TimeSpan(7, 0, 0);
            return false;
        }
        static TimeSpan Tod() { TryTime(Conf.S.AutoOffTime, out var t); return t; }

        /// <summary>The next time of day after "now" (local): today if it's still ahead, otherwise tomorrow.</summary>
        public static DateTime NextLocal(DateTime nowLocal, TimeSpan tod)
        {
            var when = nowLocal.Date + tod;
            return when > nowLocal ? when : when.AddDays(1);
        }

        /// <summary>What to do on a tick: "wait", "warn", "due" (shut down now) or "missed" (too late: pick the next day).</summary>
        public static string Decide(long now, long at, bool cancelled, int warnSec)
        {
            if (cancelled || at <= 0) return "wait";
            if (now >= at) return now - at > Grace ? "missed" : "due";
            return at - now <= warnSec ? "warn" : "wait";
        }

        // ---------------- lifecycle ----------------
        public void Start()
        {
            long boot = Time.Now - Time.UptimeSeconds();
            var saved = Json.Load<AutoOffState>(FilePath);
            lock (g)
            {
                // the same Windows session (watchdog restarted or updated): keep what was set, including a cancel
                if (Math.Abs(saved.BootAt - boot) <= 120 && (saved.At > 0 || saved.Cancelled)) { s = saved; s.BootAt = boot; }
                else s = new AutoOffState { BootAt = boot };
            }
            if (!Enabled) { lock (g) s.At = 0; Save(); return; }
            if (At == 0 && !Cancelled) Arm(null);
            else Log.Debug(Cancelled ? "Daily shutdown stays cancelled for this Windows session." : "Daily shutdown still set for " + Time.Short(At) + ".");
        }

        /// <summary>Sets the next shutdown (the next 7:00 from now). who = null when it's set by itself.</summary>
        public void Arm(string who)
        {
            if (!Enabled) return;
            long at = Time.Epoch(NextLocal(DateTime.Now, Tod()));
            lock (g) { s.At = at; s.Cancelled = false; s.Delays = 0; s.By = who ?? ""; }
            Save();
            Log.Info($"Daily shutdown set for {Time.Short(at)}" + (who != null ? $" ({who.ToLowerInvariant()})." : "."));
            Changed?.Invoke();
        }

        /// <summary>Called every second by the agent's main loop.</summary>
        public void Tick(long now)
        {
            if (!Enabled || ShuttingDown) return;
            long at; bool cancelled;
            lock (g) { at = s.At; cancelled = s.Cancelled; }
            var d = Decide(now, at, cancelled, WarnSeconds);
            if (d == "missed")
            {
                Log.Warn($"The daily shutdown at {Time.Short(at)} was missed (the PC was asleep or the watchdog wasn't running), so the PC stays on now.");
                Arm(null);
            }
            else if (d == "due")
            {
                ShuttingDown = true;
                Changed?.Invoke();
                System.Threading.Tasks.Task.Run(() => { try { OnDue?.Invoke(); } catch (Exception e) { Log.Error("Daily shutdown failed: " + e.Message); EndShutdown(); } });
            }
        }

        /// <summary>True while the shutdown should still go ahead (nobody cancelled or delayed it in the meantime).</summary>
        public bool StillDue() { lock (g) return !s.Cancelled && s.At > 0 && s.At <= Time.Now + 5; }

        /// <summary>The shutdown didn't happen after all (cancelled while Salad was closing, or Windows refused).</summary>
        public void EndShutdown() { ShuttingDown = false; Changed?.Invoke(); }

        /// <summary>Test mode only: the "shutdown" was logged instead, so set the next day.</summary>
        public void DoneTest() { ShuttingDown = false; Arm(null); }

        public (bool ok, string msg) Cancel(string who)
        {
            if (!Enabled) return (false, "The daily shutdown is switched off.");
            long at;
            lock (g)
            {
                if (s.Cancelled) return (true, "The shutdown is already cancelled.");
                if (s.At == 0) return (false, "No shutdown is scheduled.");
                at = s.At; s.Cancelled = true; s.By = who;
            }
            Save();
            Log.Info($"Shutdown at {Time.Short(at)} cancelled ({who.ToLowerInvariant()}). The PC stays on; the next 7:00 shutdown is set the next time Windows starts.");
            Changed?.Invoke();
            return (true, "Shutdown cancelled. The PC stays on until you turn it off.");
        }

        public (bool ok, string msg) Delay(string who, int minutes = 60)
        {
            if (!Enabled) return (false, "The daily shutdown is switched off.");
            long at;
            lock (g)
            {
                if (s.Cancelled || s.At == 0) return (false, "The shutdown is cancelled. Use \"Schedule again\" first.");
                // pressed while it's already due (Salad closing): count from now, not from the passed time
                s.At = Math.Max(s.At, Time.Now) + minutes * 60L; s.Delays++; s.By = who; at = s.At;
            }
            Save();
            Log.Info($"Shutdown delayed by {minutes} min to {Time.Short(at)} ({who.ToLowerInvariant()}).");
            Changed?.Invoke();
            return (true, "Shutdown moved to " + Time.Local(at).ToString("HH:mm", Time.Inv) + ".");
        }

        public (bool ok, string msg) Resume(string who)
        {
            if (!Enabled) return (false, "The daily shutdown is switched off.");
            if (!Cancelled && At > 0) return (true, "Shutdown already set for " + Time.Local(At).ToString("HH:mm", Time.Inv) + ".");
            Arm(who);
            return (true, "Shutdown set for " + Time.Local(At).ToString("HH:mm", Time.Inv) + ".");
        }

        /// <summary>The config file was edited (time, on/off): apply it without a restart.</summary>
        public void SettingsChanged()
        {
            if (!Enabled) { bool had; lock (g) { had = s.At > 0; s.At = 0; } if (had) { Save(); Log.Info("Daily shutdown switched off."); Changed?.Invoke(); } return; }
            if (!Cancelled && !ShuttingDown) Arm(null);
        }

        void Save() { try { lock (g) Json.Save(FilePath, s); } catch (Exception e) { Log.Debug("Couldn't save auto-off.json: " + e.Message); } }

        public Dictionary<string, object> Info()
        {
            lock (g)
                return new Dictionary<string, object> {
                    { "enabled", Enabled }, { "at", Enabled && s.At > 0 ? (object)s.At : null }, { "cancelled", Enabled && s.Cancelled },
                    { "delays", s.Delays }, { "warnSec", WarnSeconds }, { "busy", ShuttingDown }, { "time", Time.Local(0).Date.Add(Tod()).ToString("HH:mm", Time.Inv) },
                    { "test", Conf.S.AutoOffDryRun } };
        }

        /// <summary>The warning window should be open: within the warning time, or while shutting down.</summary>
        public bool ShowWarning()
        {
            if (!Enabled) return false;
            if (ShuttingDown) return true;
            lock (g) return Decide(Time.Now, s.At, s.Cancelled, WarnSeconds) == "warn";
        }
    }

    /// <summary>
    /// A calm, plain warning in the normal Windows style: "This PC will shut down in 5 minutes." with OK, Delay 1 hour and
    /// Cancel shutdown. OK (or the X) just closes the message; the shutdown goes ahead. Stays on top so it isn't missed.
    /// </summary>
    sealed class AutoOffWindow : Form
    {
        readonly AutoOff off;
        readonly Timer timer = new Timer { Interval = 1000 };
        /// <summary>The shutdown time this window is about; pressing OK hides the warning for that time only.</summary>
        public readonly long For;

        public AutoOffWindow(AutoOff o)
        {
            off = o; For = o.At;
            Text = "GPU Watchdog";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10.5f);
            BackColor = SystemColors.Window;
            ClientSize = new Size(440, 150);
            try { Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }

            long mins = Math.Max(1, (long)Math.Ceiling(Math.Max(0, For - Time.Now) / 60.0));
            var text = new Label
            {
                AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 12f),
                Text = $"This PC will shut down in {mins} minute{(mins == 1 ? "" : "s")}." + (Conf.S.AutoOffDryRun ? "\n(Test: it won't really shut down.)" : ""),
            };
            text.SetBounds(24, 18, 392, 64);

            var bar = new Panel { BackColor = SystemColors.Control };
            bar.SetBounds(0, 96, 440, 54);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, UseVisualStyleBackColor = true };
            var delay = new Button { Text = "Delay 1 hour", UseVisualStyleBackColor = true };
            var cancel = new Button { Text = "Cancel shutdown", UseVisualStyleBackColor = true };
            ok.SetBounds(18, 12, 96, 30); delay.SetBounds(124, 12, 136, 30); cancel.SetBounds(270, 12, 152, 30);
            ok.Click += (s, e) => Close();
            delay.Click += (s, e) => { off.Delay("PC"); Close(); };
            cancel.Click += (s, e) => { off.Cancel("PC"); Close(); };
            bar.Controls.AddRange(new Control[] { ok, delay, cancel });
            Controls.AddRange(new Control[] { text, bar });
            AcceptButton = ok;

            // closes by itself once it no longer applies (cancelled or delayed elsewhere, e.g. from the phone)
            timer.Tick += (s, e) => { if (!off.ShowWarning() || off.At != For) Close(); };
            timer.Start();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }
        protected override void OnFormClosed(FormClosedEventArgs e) { timer.Stop(); base.OnFormClosed(e); }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
