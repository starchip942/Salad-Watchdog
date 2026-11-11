using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace GpuWatchdog
{
    /// <summary>
    /// Keeps Salad running in the background without its tray icon: the icon is removed from the taskbar the same way
    /// Windows removes any app's icon (Salad itself keeps running and earning). Turning the setting off asks Salad to put
    /// its icon back (the same signal Windows sends after Explorer restarts). Salad's window can still be opened from the panel.
    /// </summary>
    static class SaladTray
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA { public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip; public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo; public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

        static List<IntPtr> Hosts()
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName("Salad").Select(p => (uint)p.Id));
            var list = new List<IntPtr>();
            if (pids.Count == 0) return list;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid)) { var sb = new System.Text.StringBuilder(64); GetClassName(h, sb, 64); if (sb.ToString() == "Electron_NotifyIconHostWindow") list.Add(h); }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>Removes Salad's tray icon (no-op if it's already gone). Returns true if one was removed.</summary>
        public static bool Hide()
        {
            bool removed = false;
            foreach (var h in Hosts())
                for (uint id = 1; id <= 6; id++)
                {
                    var d = new NOTIFYICONDATA { cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)), hWnd = h, uID = id };
                    if (Shell_NotifyIcon(2 /*NIM_DELETE*/, ref d)) removed = true;
                }
            return removed;
        }

        /// <summary>Asks Salad to add its tray icon again.</summary>
        public static void Show()
        {
            uint created = RegisterWindowMessage("TaskbarCreated");
            foreach (var h in Hosts()) PostMessage(h, created, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Opens Salad's window (what clicking its tray icon does).</summary>
        public static string OpenWindow()
        {
            if (!Salad.AppRunning()) return "Salad isn't running. Press Chop Now to start it.";
            if (SaladPeek.Parked) { SaladPeek.Release(); }
            foreach (var h in Hosts())
                for (int id = 1; id <= 4; id++)
                {
                    PostMessage(h, 0x8001, new IntPtr(id), new IntPtr(0x0201));
                    PostMessage(h, 0x8001, new IntPtr(id), new IntPtr(0x0202));
                }
            return "Opening Salad's window.";
        }
    }
}
