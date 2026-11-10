using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace GpuWatchdog
{
    /// <summary>
    /// Switches MSI Afterburner between its saved profiles using Afterburner's own command line (-Profile1 … -Profile5),
    /// exactly like pressing the profile button in Afterburner. Profile 1 = Undervolt, 2 = Stock (names in the config).
    /// Afterburner keeps doing the work; the watchdog never edits Afterburner's files or the GPU settings itself.
    /// </summary>
    static class Afterburner
    {
        public static int Current;      // last profile applied by the watchdog (0 = not changed by us yet)
        public static long AppliedAt;

        static string cached; static DateTime cachedAt;
        public static string ExePath()
        {
            if ((DateTime.Now - cachedAt).TotalSeconds < 60) return cached;
            cachedAt = DateTime.Now;
            return cached = Find();
        }
        static string Find()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Afterburner"))
                    {
                        var loc = k?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(loc, "MSIAfterburner.exe"))) return Path.Combine(loc, "MSIAfterburner.exe");
                    }
                }
                catch { }
            }
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var p = Path.Combine(pf, "MSI Afterburner", "MSIAfterburner.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static bool Available => ExePath() != null;
        public static string Name(int p) => p == 1 ? Conf.S.AfterburnerProfile1Name : p == 2 ? Conf.S.AfterburnerProfile2Name : "Profile " + p;

        public static (bool ok, string msg) Apply(int profile, string who)
        {
            // only the two profiles set up for this (1 Undervolt, 2 Stock): nobody can apply any other saved profile
            if (profile != 1 && profile != 2) return (false, "Unknown profile.");
            var exe = ExePath();
            if (exe == null) return (false, "MSI Afterburner isn't installed on this PC.");
            try
            {
                bool wasRunning = Process.GetProcessesByName("MSIAfterburner").Length > 0;
                // if Afterburner has to start for this, keep its window out of the way (it stays in the tray)
                // Afterburner stays in the tray: any of its windows that comes up because of this is sent back at once
                // (also when it was already running, since passing a profile can bring its window to the front)
                WindowGuard.HideNew(wasRunning ? 6 : 25, false, 30, "MSIAfterburner");
                Process.Start(new ProcessStartInfo(exe, "-Profile" + profile) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                Current = profile; AppliedAt = Time.Now;
                Conf.S.AfterburnerLastProfile = profile; Conf.Save();
                Log.Info($"GPU profile: {Name(profile)} (MSI Afterburner profile {profile}, {who.ToLowerInvariant()}).");
                return (true, Name(profile) + " applied.");
            }
            catch (Exception e) { return (false, "Couldn't switch the Afterburner profile: " + e.Message); }
        }

        public static Dictionary<string, object> Info() => new Dictionary<string, object> {
            { "available", Available }, { "profile", Conf.S.AfterburnerLastProfile > 0 ? (object)Conf.S.AfterburnerLastProfile : null },
            { "names", new[] { Conf.S.AfterburnerProfile1Name, Conf.S.AfterburnerProfile2Name } } };
    }
}
