using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GpuWatchdog
{
    /// <summary>config\agent.config.json: local settings and secrets. Git-ignored, never leaves the PC except the password HASH.</summary>
    public class Settings
    {
        public int Version = 1;
        public string RelayUrl = "";            // e.g. https://gpu-watchdog-relay.<name>.workers.dev
        public string AgentKey = "";            // long random key; the same value is a Cloudflare secret

        // website password (hash only, PBKDF2-SHA256)
        public string PwSalt = "";
        public string PwHash = "";
        public int PwIter = 100000;
        public int PwVer = 0;
        public int PwSyncedVer = -1;            // version the relay confirmed

        public bool ViewRequiresPassword = false;
        public bool ShowEarningsOnWebsite = true;
        public bool StartHiddenInTray = false;
        public bool HideSaladTrayIcon = true;    // Salad runs in the background without a tray icon
        public int SampleSecondsIdle = 15;      // telemetry interval when nobody watches (1 s when someone does)

        // safety
        public bool SafetyEnabled = true;
        public string SafetySensor = "core";    // core | hotspot | either
        public int SafetyCoreLimitC = 85;
        public int SafetyCpuLimitC = 90;        // CPU package temperature that also triggers the emergency stop
        public int SafetyHotspotLimitC = 95;
        public int SafetySamples = 3;           // consecutive 1-second readings at/over the limit

        // game mode
        public bool GameModeEnabled = true;
        public string AfterburnerProfile1Name = "Undervolt", AfterburnerProfile2Name = "Stock";
        public int AfterburnerLastProfile;
        public int GameResumeDelaySec = 10;
        public List<string> GameProcesses = new List<string>();      // extra programs that count as games, by name
        public List<string> GameFolders = new List<string>();        // extra folders whose programs count as games
        public List<string> IgnoreProcesses = new List<string>();    // programs in game folders that aren't games
        public bool ImportSteam = true;
        public bool ImportGameFolders = true;   // D:\Games, X:\XboxGames, Epic, GOG...

        // lights & screen
        public int ScreenOffDelaySec = 20;
        public bool ScreenOffEvenIfLightsFail = true;
        public string SignalRgbApi = "http://localhost:16038/api/v1";
        public string SignalRgbOffUrl = "";     // optional override for the lights-off application URL

        public int PanelPort = 47615;
        public string PanelToken = "";

        public static string NewSecret(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public class Lockout
    {
        public bool Active;
        public long At;
        public double TempC;
        public string Sensor = "";
        public List<double[]> RecentTemps = new List<double[]>();   // [epoch, core, hotspot] for the last few minutes
    }

    public class ScheduleItem
    {
        public string Id = "";
        public string Action = "";     // pc_off | salad_off
        public long At;                // UTC epoch seconds
        public bool Missed;
        public string Source = "";     // panel | website
    }

    public class ScheduleFile { public List<ScheduleItem> Items = new List<ScheduleItem>(); }

    static class Conf
    {
        public static Settings S;
        static readonly object Gate = new object();
        public static string FilePath => Paths.Cfg("agent.config.json");

        public static void Load()
        {
            S = Json.Load<Settings>(FilePath);
            bool changed = false;
            if (string.IsNullOrEmpty(S.AgentKey)) { S.AgentKey = Settings.NewSecret(48); changed = true; }
            if (string.IsNullOrEmpty(S.PanelToken)) { S.PanelToken = Settings.NewSecret(24); changed = true; }
            // No password is ever written in the code: on a new PC the panel asks you to set one before website controls work.
            if (changed) Save();
        }

        public static void Save() { lock (Gate) Json.Save(FilePath, S); }
    }

    /// <summary>PBKDF2-SHA256 with 100,000 rounds: the most Cloudflare's WebCrypto accepts, verified there with the same parameters.</summary>
    static class Password
    {
        public static void Set(string plain, Settings s)
        {
            var salt = new byte[16];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(salt);
            s.PwSalt = Convert.ToBase64String(salt);
            s.PwIter = 100000;
            s.PwHash = Convert.ToBase64String(Derive(plain, salt, s.PwIter));
            s.PwVer++;
        }
        public static byte[] Derive(string plain, byte[] salt, int iter)
        {
            using (var k = new Rfc2898DeriveBytes(plain, salt, iter, HashAlgorithmName.SHA256)) return k.GetBytes(32);
        }
        /// <summary>0..4 strength hint for the panel</summary>
        public static int Strength(string p)
        {
            if (string.IsNullOrEmpty(p)) return 0;
            int classes = 0; bool lo = false, up = false, dg = false, sy = false;
            foreach (var c in p) { if (char.IsLower(c)) lo = true; else if (char.IsUpper(c)) up = true; else if (char.IsDigit(c)) dg = true; else sy = true; }
            classes = (lo ? 1 : 0) + (up ? 1 : 0) + (dg ? 1 : 0) + (sy ? 1 : 0);
            int score = p.Length >= 14 ? 3 : p.Length >= 10 ? 2 : p.Length >= 8 ? 1 : 0;
            if (classes >= 3) score++;
            return Math.Max(p.Length >= 6 ? 1 : 0, Math.Min(4, score));
        }
    }
}
