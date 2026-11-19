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
        /// <summary>Closes the watchdog completely (set once the tray exists).</summary>
        public static Action ExitApp;
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
                Log.Cap(Path.Combine(Paths.Logs, "crash.txt"));
                File.AppendAllText(Path.Combine(Paths.Logs, "crash.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (fatal ? " FATAL " : " ") + e + "\r\n\r\n");
                Log.Error("Unexpected problem: " + e?.GetBaseException().Message);
            }
            catch { }
            // no message box on a fatal crash: it would keep this copy alive (and the watchdog off) until someone clicks OK,
            // and nobody may be at the PC. Closing instead lets the "GPU Watchdog keep-alive" task start it again within 5 minutes.
            if (fatal) try { Log.Error("GPU Watchdog closed after a problem; it starts again by itself within 5 minutes. Details are in logs\\crash.txt."); } catch { }
        }

        /// <summary>Written when you close the watchdog on purpose (Quit, Halt), so the keep-alive task leaves it closed.</summary>
        static string QuitFlag => Paths.Cfg("quit-on-purpose.flag");

        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        static int RunApp()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // One copy only: a second launch just brings the panel forward
            var mutex = new Mutex(false, MutexName, out bool created);
            bool owned;
            // after an update the old copy is still closing: wait for it instead of just showing its panel
            var restarted = Environment.GetCommandLineArgs().Contains("--restarted");
            // started by the keep-alive task (every 5 minutes): only fills in if the watchdog isn't running, silently
            var keepalive = Environment.GetCommandLineArgs().Contains("--keepalive");
            if (keepalive && File.Exists(QuitFlag)) return 0;   // you closed it on purpose: stay closed until you start it
            try { owned = mutex.WaitOne(restarted ? 20000 : 0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                if (keepalive) return 0;   // already running: don't pop its panel up
                try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                return 0;
            }
            // started by you or at sign-in: the keep-alive task may bring it back again from now on
            if (!keepalive) try { File.Delete(QuitFlag); } catch { }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                MessageBox.Show("GPU Watchdog works best as administrator (CPU, memory and SSD temperatures, stopping Salad's service).\n\nUse the desktop shortcut, or right-click → Run as administrator.",
                    "GPU Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Paths.Ensure();
            Conf.Load();
            Log.StartSession("Watchdog started.", restarted);
            Shortcut.EnsureDesktopShortcut();

            var agent = new Agent();
            var panel = new Panel();
            var tray = new Tray(agent, panel);
            agent.Notify += (t, m) => tray.Balloon(t, m);
            ExitApp = () => tray.Invoke(new Action(() => tray.ExitQuietly()));
            agent.Start();
            panel.Start();
            // Keeps Windows from putting the PC back to sleep ~2 minutes after the 2:00 AM alarm wakes it from sleep with nobody at it.
            // You can still choose Sleep or Shut down yourself, and the screen still turns off as usual. Held for as long as the watchdog runs.
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

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
            if (!restarting && !SessionEnd.Ended) Panel.ClosePanelWindows();   // closing the watchdog closes its panel window too
            if (!SessionEnd.Ended) { if (restarting) Log.Debug("Watchdog closed for the update."); else Log.Info("Watchdog closed."); }
            if (!restarting && !SessionEnd.Ended) try { Paths.Ensure(); File.WriteAllText(QuitFlag, DateTime.Now.ToString("s")); } catch { }
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
                Log.Debug("Created the GPU Watchdog shortcut on the Desktop.");
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
