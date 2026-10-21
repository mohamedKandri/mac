using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MacShell.Native;

// Hides the Windows taskbar while MacShell runs (the dock replaces it), and puts it back.
//
//   Hide:    remember the taskbar's auto-hide setting, switch auto-hide on (so it stops reserving screen space),
//            then hide its window(s) so it never slides up. Explorer sometimes shows it again (Start menu,
//            notifications): a hook on Explorer's "window shown" events hides it right away.
//   Restore: show the window(s) and put the original auto-hide setting back.
//
// The original setting is also written to taskbar-state.txt next to the exe, so a hard kill (no chance to restore)
// is undone the next time MacShell starts or quits.
internal sealed class Taskbar : IDisposable
{
    private static readonly string StateFile = Path.Combine(AppContext.BaseDirectory, "taskbar-state.txt");

    private readonly MessageWindow _window = new("MacShellTaskbar");
    private readonly int _taskbarCreatedMessage = Win32.RegisterWindowMessageW("TaskbarCreated");
    private readonly Win32.WinEventProc _callback;   // kept in a field so the GC can't free it while Windows uses it
    private IntPtr _hook;
    private bool _hidden;

    public Taskbar()
    {
        _callback = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (_hidden && idObject == Win32.OBJID_WINDOW && IsTaskbarWindow(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_HIDE);
        };
        _window.Message += (msg, _, _) =>
        {
            // Explorer restarted: it made a fresh, visible taskbar. Hide that one too.
            if (msg == _taskbarCreatedMessage && _hidden)
            {
                _hidden = false;
                Hide();
            }
        };
    }

    public void Hide()
    {
        if (_hidden) return;
        var windows = TaskbarWindows();
        if (windows.Count == 0) return;   // no Explorer taskbar (yet); TaskbarCreated will call us again

        // Only the first time: after a hard kill the file still holds the setting from before MacShell.
        if (!File.Exists(StateFile))
        {
            try { File.WriteAllText(StateFile, GetState(windows[0]).ToString()); } catch { }
        }

        SetState(windows[0], Win32.ABS_AUTOHIDE);
        foreach (var hwnd in windows) Win32.ShowWindow(hwnd, Win32.SW_HIDE);
        _hidden = true;

        // Watch only Explorer's windows being shown (cheap: one process).
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        Win32.GetWindowThreadProcessId(windows[0], out uint explorerPid);
        _hook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero, _callback,
            explorerPid, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    // Shows the taskbar and restores the setting from before MacShell. Does nothing if MacShell never hid it
    // (no state file). Thread-safe apart from the hook, so the crash handler can call it.
    public void Restore()
    {
        _hidden = false;
        if (_hook != IntPtr.Zero && Environment.CurrentManagedThreadId == _ownerThread)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (!File.Exists(StateFile)) return;

        var windows = TaskbarWindows();
        foreach (var hwnd in windows) Win32.ShowWindow(hwnd, Win32.SW_SHOWNA);
        if (windows.Count > 0 && int.TryParse(File.ReadAllText(StateFile), out int original))
            SetState(windows[0], original);
        try { File.Delete(StateFile); } catch { }
    }

    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    // The main taskbar plus one per extra monitor.
    private static List<IntPtr> TaskbarWindows()
    {
        var list = new List<IntPtr>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsTaskbarWindow(hwnd)) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        // Main taskbar first: its state is the one that counts.
        return list.OrderByDescending(h => ClassName(h) == "Shell_TrayWnd").ToList();
    }

    private static bool IsTaskbarWindow(IntPtr hwnd) => ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        Win32.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // The taskbar's auto-hide / always-on-top bits, through the AppBar API it is itself part of.
    private static int GetState(IntPtr taskbar)
    {
        var data = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = taskbar };
        return (int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref data);
    }

    private static void SetState(IntPtr taskbar, int state)
    {
        var data = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = taskbar, lParam = new IntPtr(state) };
        Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref data);
    }

    public void Dispose()
    {
        Restore();
        _window.Dispose();
    }
}
