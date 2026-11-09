using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Runtime.InteropServices;

namespace GpuWatchdog
{
    /// <summary>
    /// GPU hotspot on RTX 50 cards: NVIDIA's public APIs don't report it, so tools that do (HWiNFO, GPU-Z and
    /// overlay apps built on them) read it with their own kernel driver. Instead of adding a driver of our own,
    /// we read the value those tools already publish in their documented shared memory, read-only.
    /// </summary>
    /// <summary>
    /// RTX 50 hotspot the way LibreHardwareMonitor's current version and your Nexus app do it: the PawnIO driver
    /// (already installed on this PC) runs LibreHardwareMonitor's signed, read-only "Nvidia" module, which reads the
    /// GPU's six thermal sensor registers. Hotspot = the hottest valid one. Module: agent\lib\PawnIO-Nvidia.bin (MPL-2.0).
    /// </summary>
    static class PawnNvidia
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr ov);
        const uint DeviceType = 41394u << 16, LoadBinary = DeviceType | (0x821 << 2), Execute = DeviceType | (0x841 << 2);
        static Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        static bool failed;
        public static uint Bus, Device;
        public static string Error = "";

        public static double? Hotspot()
        {
            if (failed || Bus == 0 && Device == 0) return null;
            try
            {
                if (handle == null)
                {
                    var mod = System.IO.Path.Combine(Paths.Lib, "PawnIO-Nvidia.bin");
                    if (!System.IO.File.Exists(mod)) { failed = true; Error = "module file missing"; return null; }
                    var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (h.IsInvalid) { failed = true; Error = "PawnIO driver not available (" + Marshal.GetLastWin32Error() + ")"; return null; }
                    var bin = System.IO.File.ReadAllBytes(mod);
                    if (!DeviceIoControl(h, LoadBinary, bin, (uint)bin.Length, null, 0, out _, IntPtr.Zero)) { h.Dispose(); failed = true; Error = "module refused (" + Marshal.GetLastWin32Error() + ")"; return null; }
                    handle = h;
                }
                var input = new byte[32 + 3 * 8];
                var name = Encoding.ASCII.GetBytes("ioctl_read_thermal_registers");
                Buffer.BlockCopy(name, 0, input, 0, Math.Min(31, name.Length));
                BitConverter.GetBytes((long)Bus).CopyTo(input, 32);
                BitConverter.GetBytes((long)Device).CopyTo(input, 40);
                var output = new byte[6 * 8];
                if (!DeviceIoControl(handle, Execute, input, (uint)input.Length, output, (uint)output.Length, out uint got, IntPtr.Zero)) { Error = "read failed (" + Marshal.GetLastWin32Error() + ")"; return null; }
                double? max = null;
                for (int i = 0; i < Math.Min(6, (int)got / 8); i++)
                {
                    uint raw = (uint)BitConverter.ToInt64(output, i * 8);
                    if ((raw & (1u << 30)) == 0) continue;
                    double t = (raw & 0xFFFF) / 256.0;
                    if (t > 0 && t < 130 && (max == null || t > max)) max = t;
                }
                return max == null ? (double?)null : Math.Round(max.Value, 1);
            }
            catch (Exception e) { Error = e.Message; failed = true; return null; }
        }
    }

    static class ExternalSensors
    {
        public static string HotspotSource = "";

        public static double? GpuHotspot()
        {
            var v = PawnNvidia.Hotspot();
            if (v != null) { HotspotSource = "PawnIO"; return v; }
            v = FromHwinfo();
            if (v != null) { HotspotSource = "HWiNFO"; return v; }
            v = FromGpuZ();
            if (v != null) { HotspotSource = "GPU-Z"; return v; }
            HotspotSource = "";
            return null;
        }

        static bool Hot(string label) { var l = label.ToLowerInvariant(); return l.Contains("hot spot") || l.Contains("hotspot") || l.Contains("junction temp") && !l.Contains("memory"); }

        /// <summary>HWiNFO "Shared Memory Support" (Global\HWiNFO_SENS_SM2), documented layout.</summary>
        static double? FromHwinfo()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read))
                using (var a = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    if (a.ReadUInt32(0) != 0x53695748) return null;            // "HWiS"
                    uint offS = a.ReadUInt32(20), sizeS = a.ReadUInt32(24), numS = a.ReadUInt32(28);
                    uint offR = a.ReadUInt32(32), sizeR = a.ReadUInt32(36), numR = a.ReadUInt32(40);
                    for (uint i = 0; i < numR && i < 4096; i++)
                    {
                        long r = offR + i * sizeR;
                        if (a.ReadUInt32(r) != 1) continue;                        // temperature
                        var label = Ansi(a, r + 12, 128);
                        if (!Hot(label)) continue;
                        uint si = a.ReadUInt32(r + 4);
                        var sensor = si < numS ? Ansi(a, offS + si * sizeS + 8, 128) : "";
                        if (sensor.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) < 0 && sensor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0 && sensor.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var v = a.ReadDouble(r + 284);
                        if (v > 0 && v < 130) return Math.Round(v, 1);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>GPU-Z shared memory (GPUZShMem): 128 records then 128 sensors {name[256], unit[8], digits, value}.</summary>
        static double? FromGpuZ()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting("GPUZShMem", MemoryMappedFileRights.Read))
                using (var a = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    const long sensors = 12 + 128 * 1024;
                    foreach (var size in new[] { 540, 544 })                      // packed, or 8-byte aligned
                    {
                        int valueOff = size == 540 ? 532 : 536;
                        for (int i = 0; i < 128; i++)
                        {
                            long s = sensors + (long)i * size;
                            var name = Utf16(a, s, 256);
                            if (name.Length == 0) break;
                            if (!Hot(name)) continue;
                            var unit = Utf16(a, s + 512, 8);
                            if (!unit.Contains("C")) continue;
                            var v = a.ReadDouble(s + valueOff);
                            if (v > 0 && v < 130) return Math.Round(v, 1);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static string Ansi(MemoryMappedViewAccessor a, long at, int max)
        {
            var b = new byte[max]; a.ReadArray(at, b, 0, max);
            int n = Array.IndexOf(b, (byte)0); if (n < 0) n = max;
            return Encoding.Default.GetString(b, 0, n);
        }
        static string Utf16(MemoryMappedViewAccessor a, long at, int chars)
        {
            var b = new byte[chars * 2]; a.ReadArray(at, b, 0, b.Length);
            var s = Encoding.Unicode.GetString(b); int n = s.IndexOf('\0');
            return n < 0 ? s : s.Substring(0, n);
        }
    }
}
