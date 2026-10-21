using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MacShell.Native;

// Keeps the list of app windows (the ones the taskbar would show), front to back, and raises Changed when
// windows open, close, hide or show. Event-driven (SetWinEventHook), then one recount 250 ms after a burst.
//
// Each window gets an identity to match it with a dock app:
//   AppId   = the window's app ID (explicit per-window property, e.g. "MSEdge"), else the Store app's package app ID,
//             else "Microsoft.Windows.Explorer" for File Explorer windows
//   ExePath = the program's .exe
internal sealed class WindowTracker : IDisposable
{
    public sealed record AppWindow(IntPtr Handle, string? ExePath, string? AppId, string Name)
    {
        // Groups windows of one app for "running but not pinned" tiles.
        public string Key => AppId ?? ExePath?.ToLowerInvariant() ?? Handle.ToString();
    }

    private const int GWL_STYLE = -16;
    private const long WS_CHILD = 0x40000000;

    private readonly Win32.WinEventProc _callback;   // kept in a field so the GC can't free it while Windows uses it
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<IntPtr, AppWindow> _identities = [];
    private HashSet<IntPtr> _known = [];

    public IReadOnlyList<AppWindow> Windows { get; private set; } = [];

    public event Action? Changed;

    public WindowTracker()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };

        // Out-of-context hooks call back on this (UI) thread. They fire for every window and control on the desktop,
        // so the callback only does cheap checks before scheduling a recount.
        _callback = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != Win32.OBJID_WINDOW || idChild != Win32.CHILDID_SELF) return;   // a control, caret, cursor...
            if (!_known.Contains(hwnd) && !IsTopLevel(hwnd)) return;
            _debounce.Stop();
            _debounce.Start();
        };
        uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE, IntPtr.Zero, _callback, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _callback, 0, 0, flags));

        Refresh();
    }

    // Recounts now (also used right before acting on a click, so the decision isn't 250 ms stale).
    public void Refresh()
    {
        var windows = new List<AppWindow>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd)) windows.Add(Identify(hwnd));
            return true;
        }, IntPtr.Zero);

        _known = windows.Select(w => w.Handle).ToHashSet();
        foreach (var gone in _identities.Keys.Where(h => !_known.Contains(h)).ToList()) _identities.Remove(gone);
        Windows = windows;
        Changed?.Invoke();
    }

    // Brings a window to the front, restoring it if minimized.
    public static void Activate(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        // Allowed because the click that triggered this was input to our process. If Windows refuses anyway
        // (its focus-stealing rules), fall back to what Alt+Tab uses.
        if (!Win32.SetForegroundWindow(hwnd) || Win32.GetForegroundWindow() != hwnd)
            Win32.SwitchToThisWindow(hwnd, true);
    }

    public static IntPtr Foreground => Win32.GetForegroundWindow();

    // Roughly the taskbar's rules: visible, top-level, not a tool window or owned popup (unless it asks to be
    // shown with WS_EX_APPWINDOW), not cloaked, has a title, and not ours / the shell's.
    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd) || !IsTopLevel(hwnd)) return false;
        long ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (Win32.GetWindowTextLengthW(hwnd) == 0) return false;
        return AppInfo.GetAppName(hwnd) is not null;
    }

    private static bool IsTopLevel(IntPtr hwnd) => ((long)Win32.GetWindowLongPtrW(hwnd, GWL_STYLE) & WS_CHILD) == 0;

    private AppWindow Identify(IntPtr hwnd)
    {
        if (_identities.TryGetValue(hwnd, out var known)) return known;

        uint pid = AppInfo.ProcessOf(hwnd);
        string? exe = AppInfo.ExePath(pid);
        string? appId = WindowAppId(hwnd) ?? PackagedAppId(pid);
        if (appId is null && exe is not null && Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            appId = "Microsoft.Windows.Explorer";   // File Explorer's Start menu ID

        var window = new AppWindow(hwnd, exe, appId, AppInfo.GetAppName(hwnd) ?? "");
        _identities[hwnd] = window;
        return window;
    }

    // ---------------- App IDs ----------------

    // An app can tag its windows with an app ID (System.AppUserModel.ID); browsers and Electron apps often do.
    private static string? WindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore? store) != 0 || store is null) return null;
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // Store apps' processes have a package app ID; classic apps don't (error 15703).
    private static string? PackagedAppId(uint pid)
    {
        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            uint length = 256;
            var id = new StringBuilder((int)length);
            return Win32.GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // A PROPVARIANT (a tagged union). We only read strings: the type tag, then the pointer 8 bytes in.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // The property bag the shell keeps for a window (app ID, relaunch command...).
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore? store);

    // Frees what a PROPVARIANT points to (the string).
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    public void Dispose()
    {
        _debounce.Stop();
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
    }
}
