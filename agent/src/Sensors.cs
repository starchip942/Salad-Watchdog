using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace GpuWatchdog
{
    public class FanReading { public double? pct; public double? rpm; }
    public class TempReading { public string name; public double? tempC; }

    public class Reading
    {
        public string GpuName;
        public double? GpuTemp, GpuCore, GpuHotspot, GpuUtil, VramUsedMB, VramTotalMB, GpuPowerW;
        public List<FanReading> Fans = new List<FanReading>();
        public string CpuName;
        public double? CpuTemp, CpuPowerW, CpuLoad;
        public List<TempReading> Ram = new List<TempReading>();
        public List<TempReading> Ssd = new List<TempReading>();
    }

    /// <summary>
    /// Reads sensors. GPU numbers come from NVIDIA's own NVML library (part of the driver); hotspot, CPU, DIMM and
    /// SSD temperatures come from LibreHardwareMonitor (portable DLLs in agent\lib). Anything unreadable stays null.
    /// Not thread-safe: only the agent loop calls it.
    /// </summary>
    sealed class Sensors : IDisposable
    {
        IntPtr gpu = IntPtr.Zero;
        bool nvmlOk;
        Computer lhm;
        DateTime lastStorage = DateTime.MinValue;
        public string NvmlError, LhmError;
        uint fanCount;

        public void Open()
        {
            try
            {
                int r = Nvml.nvmlInit_v2();
                if (r == 0) r = Nvml.nvmlDeviceGetHandleByIndex_v2(0, out gpu);
                nvmlOk = r == 0;
                if (!nvmlOk) NvmlError = "NVML error " + r;
                else
                {
                    if (Nvml.nvmlDeviceGetNumFans(gpu, out fanCount) != 0) fanCount = 0;
                    try { if (Nvml.nvmlDeviceGetPciInfo_v3(gpu, out Nvml.PciInfo pci) == 0) { PawnNvidia.Bus = pci.bus; PawnNvidia.Device = pci.device; } } catch { }
                }
            }
            catch (Exception e) { NvmlError = e.Message; nvmlOk = false; }

            try
            {
                lhm = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = true };
                lhm.Open();
            }
            catch (Exception e) { LhmError = e.Message; lhm = null; }
        }

        public Reading Read()
        {
            var r = new Reading();
            if (nvmlOk) ReadNvml(r);
            if (lhm != null)
            {
                try { ReadLhm(r); } catch (Exception e) { LhmError = e.Message; }
            }
            if (r.GpuHotspot == null) r.GpuHotspot = ExternalSensors.GpuHotspot();
            r.GpuCore = r.GpuCore ?? r.GpuTemp;
            r.GpuTemp = r.GpuTemp ?? r.GpuCore;
            return r;
        }

        void ReadNvml(Reading r)
        {
            var sb = new StringBuilder(96);
            if (Nvml.nvmlDeviceGetName(gpu, sb, 96) == 0) r.GpuName = sb.ToString();
            if (Nvml.nvmlDeviceGetTemperature(gpu, 0, out uint t) == 0) r.GpuTemp = t;
            if (Nvml.nvmlDeviceGetUtilizationRates(gpu, out Nvml.Utilization u) == 0) r.GpuUtil = u.gpu;
            if (Nvml.nvmlDeviceGetMemoryInfo(gpu, out Nvml.Memory m) == 0) { r.VramUsedMB = m.used / 1048576.0; r.VramTotalMB = m.total / 1048576.0; }
            if (Nvml.nvmlDeviceGetPowerUsage(gpu, out uint mw) == 0) r.GpuPowerW = Math.Round(mw / 1000.0, 1);
            for (uint i = 0; i < Math.Max(fanCount, 1u); i++)
            {
                var f = new FanReading();
                if (Nvml.nvmlDeviceGetFanSpeed_v2(gpu, i, out uint pct) == 0) f.pct = pct;
                else if (i == 0 && Nvml.nvmlDeviceGetFanSpeed(gpu, out pct) == 0) f.pct = pct;
                try
                {
                    var info = new Nvml.FanSpeedInfo { version = 12u | (1u << 24), fan = i };
                    if (Nvml.nvmlDeviceGetFanSpeedRPM(gpu, ref info) == 0) f.rpm = info.speed;
                }
                catch (EntryPointNotFoundException) { }
                if (f.pct != null || f.rpm != null) r.Fans.Add(f);
            }
        }

        void ReadLhm(Reading r)
        {
            bool storageDue = (DateTime.Now - lastStorage).TotalSeconds >= 10;   // SMART reads are slower; temps change slowly
            if (storageDue) lastStorage = DateTime.Now;
            foreach (var hw in lhm.Hardware)
            {
                if (hw.HardwareType == HardwareType.Storage && !storageDue) { AddStorage(r, hw, cachedOnly: true); continue; }
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                switch (hw.HardwareType)
                {
                    case HardwareType.GpuNvidia:
                        foreach (var s in All(hw).Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue))
                        {
                            var n = s.Name.ToLowerInvariant();
                            if (n.Contains("hot spot") || n.Contains("hotspot")) r.GpuHotspot = Round(s.Value);
                            else if (n == "gpu core" && r.GpuTemp == null) r.GpuCore = Round(s.Value);
                        }
                        if (r.Fans.All(f => f.rpm == null))
                        {
                            var rpms = All(hw).Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue).Select(s => (double)s.Value.Value).ToList();
                            for (int i = 0; i < rpms.Count; i++) { if (i >= r.Fans.Count) r.Fans.Add(new FanReading()); r.Fans[i].rpm = Math.Round(rpms[i]); }
                        }
                        if (r.GpuName == null) r.GpuName = hw.Name;
                        break;
                    case HardwareType.Cpu:
                        r.CpuName = hw.Name;
                        var temps = All(hw).Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
                        var pkg = temps.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Tdie") || s.Name == "CPU Package" || s.Name == "Core (Tctl/Tdie)");
                        r.CpuTemp = Round(pkg?.Value ?? (temps.Count > 0 ? temps.Where(s => s.Name.StartsWith("Core") || s.Name.StartsWith("CCD")).Select(s => s.Value).DefaultIfEmpty(null).Max() : null));
                        var pw = All(hw).FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value.HasValue && s.Name.Contains("Package"));
                        r.CpuPowerW = Round(pw?.Value, 1);
                        var load = All(hw).FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value.HasValue && s.Name == "CPU Total");
                        r.CpuLoad = load?.Value == null ? (double?)null : Math.Round((double)load.Value.Value);
                        break;
                    case HardwareType.Memory:
                        foreach (var dimm in new[] { hw }.Concat(hw.SubHardware))
                        {
                            var ts = dimm.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
                            if (ts != null) r.Ram.Add(new TempReading { name = DimmName(dimm.Name, r.Ram.Count), tempC = Round(ts.Value, 1) });
                        }
                        break;
                    case HardwareType.Storage:
                        AddStorage(r, hw, false);
                        break;
                }
            }
        }

        readonly Dictionary<string, double?> storageCache = new Dictionary<string, double?>();
        void AddStorage(Reading r, IHardware hw, bool cachedOnly)
        {
            double? t;
            if (cachedOnly) storageCache.TryGetValue(hw.Identifier.ToString(), out t);
            else
            {
                var s = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value.HasValue);
                t = Round(s?.Value);
                storageCache[hw.Identifier.ToString()] = t;
            }
            r.Ssd.Add(new TempReading { name = System.Text.RegularExpressions.Regex.Replace(hw.Name, @"\s*\bSSD\b\s*", " ").Trim(), tempC = t });
        }

        static string DimmName(string n, int i) => n.IndexOf("DIMM", StringComparison.OrdinalIgnoreCase) >= 0 ? n : "DIMM " + (i + 1);
        static IEnumerable<ISensor> All(IHardware hw) => hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));
        static double? Round(float? v, int d = 0) => v.HasValue && !float.IsNaN(v.Value) ? Math.Round((double)v.Value, d) : (double?)null;
        static double? Round(double? v, int d = 0) => v.HasValue && !double.IsNaN(v.Value) ? Math.Round(v.Value, d) : (double?)null;

        /// <summary>Every sensor LibreHardwareMonitor can see, for the Phase 1 report.</summary>
        public IEnumerable<string> Inventory()
        {
            var lines = new List<string>();
            if (lhm == null) { lines.Add("LibreHardwareMonitor: not available (" + LhmError + ")"); return lines; }
            foreach (var hw in lhm.Hardware)
            {
                hw.Update(); foreach (var sub in hw.SubHardware) sub.Update();
                lines.Add($"[{hw.HardwareType}] {hw.Name}");
                foreach (var s in hw.Sensors) lines.Add($"    {s.SensorType,-12} {s.Name,-34} {(s.Value.HasValue ? s.Value.Value.ToString("0.##", Time.Inv) : "(no value)")}");
                foreach (var sub in hw.SubHardware)
                {
                    lines.Add($"  [{sub.HardwareType}] {sub.Name}");
                    foreach (var s in sub.Sensors) lines.Add($"      {s.SensorType,-12} {s.Name,-32} {(s.Value.HasValue ? s.Value.Value.ToString("0.##", Time.Inv) : "(no value)")}");
                }
            }
            return lines;
        }

        public bool NvmlOk => nvmlOk;

        public void ReopenLhm()
        {
            try { lhm?.Close(); } catch { }
            try
            {
                lhm = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = true };
                lhm.Open(); LhmError = null;
            }
            catch (Exception e) { LhmError = e.Message; lhm = null; }
        }

        public void Dispose()
        {
            try { lhm?.Close(); } catch { }
            try { if (nvmlOk) Nvml.nvmlShutdown(); } catch { }
        }
    }

    static class Nvml
    {
        const string Dll = "nvml.dll";
        [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint gpu, memory; }
        [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong total, free, used; }
        [StructLayout(LayoutKind.Sequential)] public struct FanSpeedInfo { public uint version, fan, speed; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] public struct PciInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string busIdLegacy;
            public uint domain, bus, device, pciDeviceId, pciSubSystemId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string busId;
        }
        [DllImport(Dll)] public static extern int nvmlDeviceGetPciInfo_v3(IntPtr dev, out PciInfo p);
        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlShutdown();
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, uint len);
        [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
        [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization u);
        [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out Memory m);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
        [DllImport(Dll)] public static extern int nvmlDeviceGetNumFans(IntPtr dev, out uint n);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint pct);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed_v2(IntPtr dev, uint fan, out uint pct);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeedRPM(IntPtr dev, ref FanSpeedInfo info);
    }
}
