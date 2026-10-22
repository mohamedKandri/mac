using System.Runtime.InteropServices;
using System.Text;

namespace MacShell.Native;

// Connected monitors, their DPI, and "is a fullscreen app in front on this monitor?".
// Monitors are identified by device name ("\\.\DISPLAY1"): HMONITOR handles change on every layout change.
internal static class Monitors
{
    // WorkArea = Bounds minus the space reserved by the menu bar, the dock and any other AppBar (physical pixels).
    public readonly record struct Info(string DeviceName, bool IsPrimary, Win32.RECT Bounds, uint Dpi, Win32.RECT WorkArea);

    // Primary first, then the others in Windows' order.
    public static List<Info> All()
    {
        var list = new List<Info>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            if (Describe(monitor) is Info info) list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ToList();
    }

    public static Info Primary() =>
        Describe(Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY))
        ?? new Info("", true, new Win32.RECT { Right = 1920, Bottom = 1080 }, 96, new Win32.RECT { Right = 1920, Bottom = 1080 });

    public static Info? Find(string deviceName)
    {
        foreach (var monitor in All())
            if (monitor.DeviceName == deviceName) return monitor;
        return null;   // unplugged
    }

    // DPI of the monitor under a physical-pixel point (96 = 100%).
    public static uint DpiAt(int x, int y)
    {
        IntPtr monitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        return Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi : 96;
    }

    // Shell surfaces that cover a whole monitor without being "a fullscreen app": desktop, Task View, Alt+Tab...
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "ForegroundStaging"];

    // True when the window in front covers the entire monitor (games, F11 browsers, video players).
    // Maximized windows never qualify: our AppBar keeps them below the bar.
    public static bool IsFullscreenAppOn(string deviceName)
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        var className = new StringBuilder(256);
        Win32.GetClassNameW(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return false;
        if (AppInfo.GetAppName(hwnd) is null) return false;   // ours, taskbar, Start menu...

        if (Describe(Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST)) is not Info monitor) return false;
        if (monitor.DeviceName != deviceName) return false;

        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        var m = monitor.Bounds;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static Info? Describe(IntPtr monitor)
    {
        var info = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = "" };
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfoEx(monitor, ref info)) return null;
        uint dpi = Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out uint x, out _) == 0 ? x : 96;
        return new Info(info.szDevice, (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0, info.rcMonitor, dpi, info.rcWork);
    }
}
