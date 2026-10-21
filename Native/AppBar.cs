using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacShell.Native;

// Turns a WPF window into an "AppBar" docked at the top of one monitor, the same mechanism the taskbar
// uses: Windows shrinks the monitor's work area, so maximized windows stop below the bar.
//
// The flow (all sizes in physical pixels):
//   ABM_NEW       register the window once
//   ABM_QUERYPOS  "I'd like this rectangle"; the shell adjusts it if another bar (e.g. a top taskbar) is there
//   ABM_SETPOS    claim the adjusted rectangle; the work area shrinks
//   SetWindowPos  actually move the window there
//   ABM_REMOVE    on exit: give the space back
public enum AppBarEdge { Top, Bottom }

// Decides where the window goes inside the reserved strip: (reserved strip, whole monitor, scale) -> window rect,
// all in physical pixels. Null = the window fills the strip (the bar). The dock uses a centered panel.
public delegate Int32Rect StripPlacement(Int32Rect reserved, Int32Rect monitor, double scale);

internal sealed class AppBar : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly int _callbackMessage;
    private readonly int _taskbarCreatedMessage;
    private readonly string _deviceName;
    private readonly AppBarEdge _edge;
    private readonly StripPlacement? _placement;
    private double _thicknessDip;
    private bool _registered;

    // Explorer's ABN_FULLSCREENAPP: true = a fullscreen app opened (on some monitor), false = it closed.
    public event Action<bool>? FullscreenNotified;

    // `deviceName` = the monitor to dock on ("\\.\DISPLAY1", see Native/Monitors).
    public AppBar(Window window, string deviceName, AppBarEdge edge = AppBarEdge.Top, StripPlacement? placement = null)
    {
        _deviceName = deviceName;
        _edge = edge;
        _placement = placement;
        // Needs the native window, which exists from Window.SourceInitialized on.
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        _callbackMessage = Win32.RegisterWindowMessageW("MacShell.AppBarNotify");
        _taskbarCreatedMessage = Win32.RegisterWindowMessageW("TaskbarCreated");

        WindowStyles.MakeToolWindow(_hwnd, noActivate: true);
    }

    // True while Windows has accepted the window as an AppBar (its space is reserved).
    public bool IsRegistered => _registered;

    // Reserves a strip `thicknessDip` (WPF units, 1/96 inch) tall along the edge, or updates its size.
    // If Windows refuses (no Explorer taskbar yet, e.g. very early at sign-in), the window is still placed on top,
    // just without reserved space; TaskbarCreated retries once Explorer is up.
    public void Dock(double thicknessDip)
    {
        _thicknessDip = thicknessDip;
        if (!_registered)
        {
            var data = NewData();
            data.uCallbackMessage = (uint)_callbackMessage;
            _registered = Win32.SHAppBarMessage(Win32.ABM_NEW, ref data) != UIntPtr.Zero;
        }
        Position();
    }

    // Gives the space back. Safe to call twice, and from any thread (it's just a message to Explorer),
    // so the crash handler can use it too.
    public void Remove()
    {
        if (!_registered) return;
        _registered = false;
        var data = NewData();
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref data);
    }

    // Re-places the window, e.g. after its content (and so its placement) changed. Same strip, same reservation.
    public void Reposition() => Position();

    private void Position()
    {
        // Monitor unplugged: do nothing; Bar/BarManager closes this window on the same display change.
        if (Monitors.Find(_deviceName) is not Monitors.Info monitor) return;

        // Config sizes are in WPF units; at 150% scale, 32 units = 48 physical pixels.
        double scale = monitor.Dpi / 96.0;
        int thickness = (int)Math.Round(_thicknessDip * scale);
        var m = monitor.Bounds;
        bool top = _edge == AppBarEdge.Top;

        var data = NewData();
        data.uEdge = top ? Win32.ABE_TOP : Win32.ABE_BOTTOM;
        data.rc = new Win32.RECT
        {
            Left = m.Left,
            Right = m.Right,
            Top = top ? m.Top : m.Bottom - thickness,
            Bottom = top ? m.Top + thickness : m.Bottom,
        };
        if (_registered)
        {
            // QUERYPOS may move the strip inward (e.g. away from a visible taskbar); keep our thickness.
            Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref data);
            if (top) data.rc.Bottom = data.rc.Top + thickness;
            else data.rc.Top = data.rc.Bottom - thickness;
            Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref data);
        }

        var reserved = new Int32Rect(data.rc.Left, data.rc.Top, data.rc.Right - data.rc.Left, thickness);
        var window = _placement?.Invoke(reserved, new Int32Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top), scale) ?? reserved;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, window.X, window.Y, window.Width, window.Height, Win32.SWP_NOACTIVATE);
    }

    private Win32.APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(),
        hWnd = _hwnd,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessage)
        {
            // Another AppBar or the taskbar moved/resized: ask for our spot again.
            if ((int)wParam == Win32.ABN_POSCHANGED)
                Position();
            else if ((int)wParam == Win32.ABN_FULLSCREENAPP)
                FullscreenNotified?.Invoke(lParam != IntPtr.Zero);
            handled = true;
        }
        else if (msg == Win32.WM_POWERBROADCAST && (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC)
        {
            // Back from sleep: displays may have been re-detected meanwhile.
            _source.Dispatcher.InvokeAsync(Position, DispatcherPriority.Background);
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted and forgot every AppBar: register again.
            _registered = false;
            Dock(_thicknessDip);
        }
        else if (msg is Win32.WM_DISPLAYCHANGE or Win32.WM_DPICHANGED)
        {
            // Resolution, monitor layout or scale changed. Let WPF handle the message first (it rescales its
            // content on WM_DPICHANGED and moves the window), then re-dock once things settle.
            _source.Dispatcher.InvokeAsync(Position, DispatcherPriority.Background);
        }
        else if (msg == Win32.WM_ACTIVATE && _registered)
        {
            // Required by the AppBar contract: keeps the shell's z-order of AppBars right.
            var data = NewData();
            Win32.SHAppBarMessage(Win32.ABM_ACTIVATE, ref data);
        }
        else if (msg == Win32.WM_WINDOWPOSCHANGED && _registered)
        {
            var data = NewData();
            Win32.SHAppBarMessage(Win32.ABM_WINDOWPOSCHANGED, ref data);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Remove();
        _source.RemoveHook(WndProc);
    }
}
