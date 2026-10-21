namespace MacShell.Native;

// Tells us when the active (foreground) window changes, anywhere on the desktop. Event-driven, no polling.
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly Win32.WinEventProc _callback;   // kept in a field: if the GC collected it, Windows would call freed memory
    private readonly IntPtr _hook;

    public event Action<IntPtr>? Changed;

    public static IntPtr Current => Win32.GetForegroundWindow();

    public ForegroundWatcher()
    {
        _callback = (_, _, hwnd, _, _, _, _) => Changed?.Invoke(hwnd);
        // SKIPOWNPROCESS: opening our own popup (power menu) doesn't count as an app switch.
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
    }
}
