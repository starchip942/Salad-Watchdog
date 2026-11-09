using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("GPU Watchdog Agent")]
[assembly: AssemblyProduct("GPU Watchdog")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace GpuWatchdog
{
    static class Program
    {
        const string MutexName = "Global\\GpuWatchdogAgent2";
        const string ShowEventName = "Global\\GpuWatchdogAgent2.ShowPanel";

        [STAThread]
        static int Main(string[] args)
        {
            // Libraries live in agent\lib
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var f = Path.Combine(Paths.Lib, name + ".dll");
                return File.Exists(f) ? Assembly.LoadFrom(f) : null;
            };
            if (args.Contains("--discover")) return Discovery.Run();
            if (args.Contains("--selftest")) return SelfTest.Run();
            // Never die silently: anything unexpected is written to logs\crash.txt and shown
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception, true);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception, false);
            try { return RunApp(); }
            catch (Exception e) { Crash(e, true); return 1; }
        }

        static void Crash(Exception e, bool fatal)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(Path.Combine(Paths.Logs, "crash.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (fatal ? " FATAL " : " ") + e + "\r\n\r\n");
                Log.Error("Unexpected problem: " + e?.GetBaseException().Message);
            }
            catch { }
            if (fatal) try { MessageBox.Show("GPU Watchdog hit a problem and has to close:\n\n" + e?.GetBaseException().Message + "\n\nDetails are in logs\\crash.txt.", "GPU Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }

        static int RunApp()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // One copy only: a second launch just brings the panel forward
            var mutex = new Mutex(false, MutexName, out bool created);
            bool owned;
            // after an update the old copy is still closing: wait for it instead of just showing its panel
            var restarted = Environment.GetCommandLineArgs().Contains("--restarted");
            try { owned = mutex.WaitOne(restarted ? 20000 : 0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                return 0;
            }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                MessageBox.Show("GPU Watchdog works best as administrator (CPU, memory and SSD temperatures, stopping Salad's service).\n\nUse the desktop shortcut, or right-click → Run as administrator.",
                    "GPU Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Paths.Ensure();
            Conf.Load();
            Log.StartSession($"Watchdog started (new version). It will close Salad for good if the GPU {(Conf.S.SafetySensor == "hotspot" ? "hotspot" : "core")} reaches {(Conf.S.SafetySensor == "hotspot" ? Conf.S.SafetyHotspotLimitC : Conf.S.SafetyCoreLimitC)}°C, and pause it while a game is running.");
            Shortcut.EnsureDesktopShortcut();

            var agent = new Agent();
            var panel = new Panel();
            var tray = new Tray(agent, panel);
            agent.Notify += (t, m) => tray.Balloon(t, m);
            agent.Start();
            panel.Start();

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() => { while (show.WaitOne()) tray.Invoke(panel.Open); }) { IsBackground = true }.Start();

            bool restarting = false;
            agent.UpdateReady += () => tray.Invoke(() =>
            {
                restarting = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Paths.AgentDir, "GPU-Watchdog-Agent.exe"), "--restarted") { UseShellExecute = false }); } catch (Exception e) { Log.Error("Couldn't restart into the new version: " + e.Message); restarting = false; return; }
                tray.ExitQuietly();
            });
            if (!Conf.S.StartHiddenInTray && !Environment.GetCommandLineArgs().Contains("--restarted")) panel.Open();
            SessionEnd.Hook(() => { Log.Info("PC is shutting down or restarting. Watchdog closed."); agent.Stop(); });
            Application.Run(tray);

            agent.Stop();
            panel.Stop();
            if (!SessionEnd.Ended) Log.Info(restarting ? "Watchdog closed for the update." : "Watchdog closed.");
            mutex.ReleaseMutex();
            return 0;
        }
    }

    /// <summary>Creates "GPU Watchdog" on the Desktop (runs as administrator) the first time, if it isn't there.</summary>
    static class Shortcut
    {
        public static void EnsureDesktopShortcut()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var lnk = Path.Combine(desktop, "GPU Watchdog.lnk");
                var exe = Assembly.GetExecutingAssembly().Location;
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                // keep a working shortcut; repair one whose target is missing or wrong
                if (File.Exists(lnk) && string.Equals((string)sc.TargetPath, exe, StringComparison.OrdinalIgnoreCase)) return;
                sc.TargetPath = exe; sc.WorkingDirectory = Path.GetDirectoryName(exe);
                sc.Description = "GPU Watchdog: live GPU data, Salad control, safety stop";
                sc.IconLocation = exe + ",0";
                sc.Save();
                // Set the shortcut's "Run as administrator" flag (byte 0x15, bit 0x20)
                var bytes = File.ReadAllBytes(lnk);
                bytes[0x15] |= 0x20;
                File.WriteAllBytes(lnk, bytes);
                Log.Info("Created the GPU Watchdog shortcut on the Desktop.");
            }
            catch (Exception e) { Log.Warn("Couldn't create the Desktop shortcut: " + e.Message); }
        }
    }

    /// <summary>Notices Windows shutting down / signing out so the closing line gets logged (like the old watchdog).</summary>
    static class SessionEnd
    {
        public static bool Ended;
        public static void Hook(Action onEnd)
        {
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => { if (Ended) return; Ended = true; onEnd(); };
        }
    }
}
