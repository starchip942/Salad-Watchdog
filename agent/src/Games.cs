using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GpuWatchdog
{
    public class GameFolder { public string Path; public string Name; public bool Ignore; public string Source; }
    public class RunningGame { public string Name; public string Program; }

    /// <summary>
    /// Game detection, ported from the old watchdog: any program running from a game folder counts as a game.
    /// Game folders are imported automatically from Steam (every library), Epic, GOG and the usual folders on every drive
    /// (D:\Games, X:\XboxGames ...), plus the owner's own additions. Only the process list is read: no hooks, no injection.
    /// </summary>
    static class Games
    {
        static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "unins000", "VC_redist.x64", "VC_redist.x86", "DXSETUP", "CrashReportClient", "crashpad_handler",
            "UnityCrashHandler32", "UnityCrashHandler64", "BsSndRpt64", "BugSplatHD64", "EpicWebHelper", "EpicGamesLauncher",
            "steam", "steamwebhelper", "steamservice", "GalaxyClient", "GOG Galaxy Notifications Renderer" };
        static readonly string[] NotGameFolders = { "wallpaper_engine", "OBS Studio", "Lossless Scaling", "Steamworks Shared", "Steam Controller Configs", "SteamVR" };

        public static List<GameFolder> Folders = new List<GameFolder>();
        static DateTime scannedAt = DateTime.MinValue;
        static readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();

        public static void Rescan(bool force = false)
        {
            if (!force && (DateTime.Now - scannedAt).TotalMinutes < 10) return;
            scannedAt = DateTime.Now;
            var s = Conf.S;
            var list = new List<GameFolder>();
            void Add(string p, string name, bool ignore, string src)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
                    var full = System.IO.Path.GetFullPath(p).TrimEnd('\\') + "\\";
                    if (list.Any(f => f.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
                    list.Add(new GameFolder { Path = full, Name = name, Ignore = ignore, Source = src });
                }
                catch { }
            }

            if (s.ImportSteam)
            {
                try
                {
                    var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
                                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
                    var vdf = steam == null ? null : System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (vdf != null && File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            var apps = System.IO.Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps");
                            var common = System.IO.Path.Combine(apps, "common");
                            foreach (var d in NotGameFolders) Add(System.IO.Path.Combine(common, d), d, true, "Steam");
                            if (!Directory.Exists(apps)) continue;
                            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                            {
                                var text = File.ReadAllText(acf);
                                var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                                var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                                if (dir.Length > 0 && !NotGameFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                                    Add(System.IO.Path.Combine(common, dir), name, false, "Steam");
                            }
                            Add(common, null, false, "Steam");
                        }
                }
                catch { }
            }

            if (s.ImportGameFolders)
            {
                try
                {
                    var epic = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
                    if (Directory.Exists(epic))
                        foreach (var item in Directory.GetFiles(epic, "*.item"))
                        {
                            var j = Json.Obj(File.ReadAllText(item));
                            Add(Json.Str(j, "InstallLocation"), Json.Str(j, "DisplayName"), false, "Epic");
                        }
                }
                catch { }
                try
                {
                    var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
                    if (gog != null)
                        foreach (var k in gog.GetSubKeyNames())
                        {
                            var g = gog.OpenSubKey(k);
                            Add(g?.GetValue("path") as string, g?.GetValue("gameName") as string, false, "GOG");
                        }
                }
                catch { }
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                    foreach (var dir in new[] { "Games", "XboxGames", "Epic Games", "GOG Games" })
                        Add(System.IO.Path.Combine(drive.RootDirectory.FullName, dir), null, false, drive.Name.TrimEnd('\\') + "\\" + dir);
            }
            foreach (var f in s.GameFolders) Add(f, null, false, "Added by you");
            Folders = list.OrderByDescending(f => f.Path.Length).ToList();   // most specific first
        }

        /// <summary>Everything that would count as a game, for the panel's list.</summary>
        public static List<Dictionary<string, object>> Library()
        {
            var lib = new List<Dictionary<string, object>>();
            foreach (var f in Folders.Where(f => !f.Ignore))
            {
                string name = f.Name;
                if (name == null)
                {
                    // a parent folder like D:\Games: list its game subfolders
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(f.Path))
                            if (!Folders.Any(o => o.Name != null && o.Path.StartsWith(sub.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                                lib.Add(new Dictionary<string, object> { { "name", System.IO.Path.GetFileName(sub) }, { "source", f.Source }, { "path", sub } });
                    }
                    catch { }
                    continue;
                }
                lib.Add(new Dictionary<string, object> { { "name", name }, { "source", f.Source }, { "path", f.Path } });
            }
            foreach (var p in Conf.S.GameProcesses) lib.Add(new Dictionary<string, object> { { "name", p + ".exe" }, { "source", "Added by you" }, { "path", "" }, { "removable", true } });
            return lib.GroupBy(d => ((string)d["name"]).ToLowerInvariant()).Select(g => g.First()).OrderBy(d => (string)d["name"]).ToList();
        }

        /// <summary>Every game running right now (by name). Reads the process list only.</summary>
        public static List<RunningGame> Running()
        {
            var found = new List<RunningGame>();
            var ignore = new HashSet<string>(NotGames.Concat(Conf.S.IgnoreProcesses.Select(x => Regex.Replace(x, "\\.exe$", "", RegexOptions.IgnoreCase))), StringComparer.OrdinalIgnoreCase);
            var extra = new HashSet<string>(Conf.S.GameProcesses.Select(x => Regex.Replace(x, "\\.exe$", "", RegexOptions.IgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var alive = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    alive.Add(p.Id);
                    if (p.Id <= 4 || ignore.Contains(p.ProcessName)) continue;
                    if (!pathCache.TryGetValue(p.Id, out var path)) { path = Native.ProcessPath(p.Id); pathCache[p.Id] = path; }
                    GameFolder folder = null;
                    if (path != null) folder = Folders.FirstOrDefault(f => path.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
                    if ((folder != null && folder.Ignore) || (folder == null && !extra.Contains(p.ProcessName))) continue;
                    var name = GameName(p.ProcessName, path, folder);
                    if (!found.Any(g => g.Name == name)) found.Add(new RunningGame { Name = name, Program = p.ProcessName + ".exe" });
                }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (var dead in pathCache.Keys.Where(k => !alive.Contains(k)).ToList()) pathCache.Remove(dead);
            return found;
        }

        /// <summary>The launcher's name for the game, else the program's description, else its folder name.</summary>
        static string GameName(string program, string path, GameFolder folder)
        {
            if (folder?.Name != null) return folder.Name;
            if (path != null)
            {
                try
                {
                    var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc != "BootstrapPackagedGame") return desc.Trim();
                }
                catch { }
                if (folder != null) return path.Substring(folder.Path.Length).Split('\\')[0];
            }
            return program;
        }

        /// <summary>Programs running now, for "Add from running programs".</summary>
        public static List<string> RunningPrograms()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero && p.SessionId > 0) names.Add(p.ProcessName); } catch { }
                finally { p.Dispose(); }
            }
            return names.OrderBy(n => n).ToList();
        }
    }
}
