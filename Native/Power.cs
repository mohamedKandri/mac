using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacShell.Native;

// Lock, sleep, restart, shut down. Each throws Win32Exception if Windows refuses.
internal static class Power
{
    public static void Lock()
    {
        if (!Win32.LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Sleep()
    {
        // hibernate: false = sleep. force/wakeupEventsDisabled: false = normal sleep, timers may wake the PC.
        if (!Win32.SetSuspendState(false, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Restart() => Exit(Win32.EWX_REBOOT);

    // Hybrid = "fast startup", same as the Start menu (falls back to a normal shutdown if fast startup is off).
    public static void ShutDown() => Exit(Win32.EWX_SHUTDOWN | Win32.EWX_POWEROFF | Win32.EWX_HYBRID_SHUTDOWN);

    private static void Exit(uint flags)
    {
        EnableShutdownPrivilege();
        if (!Win32.ExitWindowsEx(flags, Win32.SHTDN_REASON_FLAG_PLANNED))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Every user has the right to shut down, but each process must switch it on for itself first.
    private static void EnableShutdownPrivilege()
    {
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_ADJUST_PRIVILEGES | Win32.TOKEN_QUERY, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!Win32.LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var privileges = new Win32.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Win32.SE_PRIVILEGE_ENABLED };
            bool ok = Win32.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            // AdjustTokenPrivileges "succeeds" even when it couldn't grant the privilege; that's reported via the last error.
            if (!ok || error == Win32.ERROR_NOT_ALL_ASSIGNED)
                throw new Win32Exception(error);
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }
}
