using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacShell.Native;

// One icon in the notification area (tray). Owns a hidden window that receives the icon's mouse messages.
internal sealed class NotifyIcon : IDisposable
{
    private const int CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreatedMessage;
    private readonly IntPtr _hIcon;
    private readonly string _tooltip;

    // Fired on a left or right click on the icon.
    public event Action? Clicked;

    public NotifyIcon(byte[] icoFile, string tooltip)
    {
        _tooltip = tooltip;

        // HwndSource wraps a raw Win32 window so C# code can receive its messages (WndProc below).
        // WindowStyle = 0 means no WS_VISIBLE: it is never shown, so it's not on screen, in the taskbar or in Alt+Tab.
        // It's a normal hidden window, not a "message-only" one, because those miss the TaskbarCreated broadcast.
        _window = new HwndSource(new HwndSourceParameters("MacShellTray") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);

        _taskbarCreatedMessage = Win32.RegisterWindowMessageW("TaskbarCreated");
        _hIcon = CreateIcon(icoFile, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        Add();
    }

    // Brings a WPF popup (like a ContextMenu) to the foreground. Without this, a menu opened from the
    // tray stays open when you click elsewhere, because our app was never the active one.
    public static void Activate(Visual popupContent)
    {
        if (PresentationSource.FromVisual(popupContent) is HwndSource source)
            Win32.SetForegroundWindow(source.Handle);
    }

    private void Add()
    {
        var data = NewData();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = _tooltip;
        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
    }

    // Shows a notification from the tray icon. On Windows 11 this appears as a regular toast
    // (it stays silent while Focus assist / Do not disturb is on).
    public void ShowBalloon(string title, string text, bool warning)
    {
        var data = NewData();
        data.uFlags = Win32.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);   // the struct's fixed buffers hold 64 / 256 chars incl. the terminator
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = warning ? Win32.NIIF_WARNING : Win32.NIIF_INFO;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private Win32.NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    // Like a message handler in a classic Win32 app: Windows calls this for every message sent to our hidden window.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // With the default tray protocol, lParam holds the mouse message that happened on the icon.
            int mouse = (int)((long)lParam & 0xFFFF);
            if (mouse is Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP)
                Clicked?.Invoke();
            handled = true;
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted (or crashed) and forgot every tray icon: add ours again.
            Add();
        }
        return IntPtr.Zero;
    }

    // An .ico file holds several images (16 px, 24 px, 32 px...). Pick the smallest one that is at least
    // `size` px (else the biggest) and turn it into an HICON.
    private static IntPtr CreateIcon(byte[] ico, int size)
    {
        int count = BitConverter.ToUInt16(ico, 4);
        int chosen = -1, chosenWidth = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;                        // 6-byte header, then 16 bytes per image
            int width = ico[entry] == 0 ? 256 : ico[entry]; // 0 means 256
            bool fits = width >= size, chosenFits = chosenWidth >= size;
            if (chosen < 0 || (fits && (!chosenFits || width < chosenWidth)) || (!fits && !chosenFits && width > chosenWidth))
            {
                chosen = entry;
                chosenWidth = width;
            }
        }

        int length = BitConverter.ToInt32(ico, chosen + 8);
        int offset = BitConverter.ToInt32(ico, chosen + 12);
        byte[] image = ico.AsSpan(offset, length).ToArray();

        IntPtr hIcon = Win32.CreateIconFromResourceEx(image, (uint)length, true, 0x00030000, size, size, 0);
        if (hIcon == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray icon");
        return hIcon;
    }

    public void Dispose()
    {
        var data = NewData();
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        Win32.DestroyIcon(_hIcon);
        _window.Dispose();
    }
}
