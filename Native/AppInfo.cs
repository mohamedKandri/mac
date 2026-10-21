using System.Diagnostics;
using System.IO;
using System.Text;

namespace MacShell.Native;

// Finds a human app name for a window, like macOS shows "Safari" rather than the window title.
// Source: the exe's "File description" (what Task Manager shows), e.g. msedge.exe -> "Microsoft Edge".
internal static class AppInfo
{
    // Windows that aren't really "apps": while they're in front, keep showing the previous app.
    private static readonly HashSet<string> IgnoredClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"];
    // pickerhost = "File Picker UI Host", the system process behind some Open/Save dialogs.
    private static readonly HashSet<string> IgnoredExes = ["startmenuexperiencehost", "searchhost", "shellexperiencehost", "searchapp", "lockapp", "pickerhost"];

    // Exes whose description isn't what you'd call them.
    private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = "File Explorer",   // description says "Windows Explorer"; also covers the desktop
    };

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    // Returns null when the window should be ignored (ours, taskbar, Start menu...).
    public static string? GetAppName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (IgnoredClasses.Contains(ClassName(hwnd))) return null;

        uint pid = ProcessOf(hwnd);
        if (pid == OwnProcessId) return null;

        string? path = ExePath(pid);
        if (path is null) return null;

        string exe = Path.GetFileNameWithoutExtension(path);
        if (IgnoredExes.Contains(exe.ToLowerInvariant())) return null;

        if (!NameCache.TryGetValue(path, out string? name))
        {
            name = Renames.TryGetValue(exe, out var renamed) ? renamed : Describe(path) ?? Capitalize(exe);
            NameCache[path] = name;
        }
        return name;
    }

    // The process behind a window. UWP / Store apps (Settings, Calculator...) live inside a frame window owned by
    // ApplicationFrameHost.exe; the real app is the process owning one of its child windows.
    public static uint ProcessOf(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (ClassName(hwnd) == "ApplicationFrameWindow")
            pid = FindChildProcess(hwnd, pid) ?? pid;
        return pid;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint? FindChildProcess(IntPtr frame, uint framePid)
    {
        uint? found = null;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == framePid) return true;   // keep looking
            found = childPid;
            return false;                            // stop
        }, IntPtr.Zero);
        return found;
    }

    public static string? ExePath(uint pid)
    {
        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;   // e.g. a protected system process
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Win32.QueryFullProcessImageNameW(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static string? Describe(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
