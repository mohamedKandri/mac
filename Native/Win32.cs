using System.Runtime.InteropServices;

namespace MacShell.Native;

// Raw Windows API declarations. Only code in Native/ may use this class.
// [DllImport] is C#'s FFI: like Kotlin's `external fun` over JNI, or Python's ctypes.
// "W" functions are the Unicode (UTF-16) versions of each API.
internal static class Win32
{
    // ---------------- shell32.dll: notification area (tray) ----------------

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2;   // balloon icon: info or warning

    // Describes one tray icon. Layout must match the C struct exactly, field for field.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;            // size of this struct, so Windows knows which version we speak
        public IntPtr hWnd;            // window that receives the icon's mouse messages
        public uint uID;               // our own id for the icon (one app can own several)
        public uint uFlags;            // which of the fields below are filled in (NIF_*)
        public uint uCallbackMessage;  // message id Windows sends to hWnd on mouse events
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;       // balloon text
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;   // balloon title
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // Adds (NIM_ADD), updates (NIM_MODIFY) or removes (NIM_DELETE) an icon in the tray.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    // ---------------- user32.dll ----------------

    public const int WM_APP = 0x8000;          // first message id free for apps to define their own
    public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    public const int SM_CXSMICON = 49;         // GetSystemMetrics: width of a small icon (tray size) in px

    // Turns a name into a message id shared by every process. Explorer broadcasts "TaskbarCreated"
    // after it (re)starts, and every app must then re-add its tray icon.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessageW(string lpString);

    // Makes a window the active one. Needed so a tray menu closes when you click elsewhere.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // Reads a system size or setting, such as the tray icon size for the current DPI.
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // Builds an icon handle (HICON) from one image of an .ico file. dwVer must be 0x00030000.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer, int cxDesired, int cyDesired, uint flags);

    // Frees an icon handle created above. Windows doesn't garbage-collect these.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------------- Window messages used by the bar ----------------

    public const int WM_ACTIVATE = 0x0006;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_DISPLAYCHANGE = 0x007E;  // resolution changed, monitor plugged in or removed
    public const int WM_DPICHANGED = 0x02E0;     // the window's monitor changed scale (or the window moved to another one)

    // ---------------- Window styles ----------------

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;  // not in Alt+Tab, not in the taskbar
    public const long WS_EX_NOACTIVATE = 0x08000000;  // clicking it doesn't take focus from the app you're using
    public const long WS_EX_TRANSPARENT = 0x00000020; // with a layered window: mouse input passes through to what's below

    // Read / write a window's extended style bits (the 64-bit "Ptr" versions).
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;

    // Moves and resizes a window in physical pixels, and puts it in the topmost layer, without focusing it.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // ---------------- Monitors and DPI ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;  // the whole monitor, in physical pixels on the virtual desktop
        public RECT rcWork;     // the monitor minus taskbar and AppBars
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    // MONITORINFO plus the monitor's device name ("\\.\DISPLAY1"), which stays stable while HMONITOR handles
    // change whenever the display layout changes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // Calls `callback` once per connected monitor.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    // The monitor a window is (mostly) on.
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;

    // Returns the monitor containing a point. (0,0) is always on the primary monitor.
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    public const int MDT_EFFECTIVE_DPI = 0;

    // A monitor's scale as DPI: 96 = 100%, 120 = 125%, 144 = 150%, 192 = 200%.
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------------- shell32.dll: AppBars ----------------

    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_ACTIVATE = 6,
        ABM_WINDOWPOSCHANGED = 9, ABM_SETSTATE = 10;
    public const int ABS_AUTOHIDE = 0x1, ABS_ALWAYSONTOP = 0x2;   // taskbar state bits (GETSTATE / SETSTATE)
    public const int SW_HIDE = 0, SW_SHOWNA = 8;                  // SHOWNA: show without activating
    public const uint ABE_TOP = 1, ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;  // message id the shell sends us with notifications (ABN_*)
        public uint uEdge;             // which screen edge (ABE_*)
        public RECT rc;                // requested / granted rectangle, physical pixels
        public IntPtr lParam;
    }

    // One function for every AppBar operation: register (ABM_NEW), ask for a spot (ABM_QUERYPOS),
    // claim it (ABM_SETPOS), unregister (ABM_REMOVE)...
    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---------------- dwmapi.dll: Desktop Window Manager (the compositor that draws every window) ----------------

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // dark or light system-drawn parts (shadow, border)
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;  // Windows 11 corner rounding
    public const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
    public const int DWMWA_BORDER_COLOR = 34;              // Windows 11 window border color
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);   // ...no border at all

    // Sets one DWM attribute of a window. `value` is an int for every attribute we use.
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    // Treats part of the window (all of it with -1 margins) as "frame", which DWM composites with per-pixel
    // alpha. That's what lets WPF's transparent pixels show the blur behind instead of black.
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    // ---------------- user32.dll: window composition (blur) ----------------

    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_DISABLED = 0, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;  // tint over the blur, as 0xAABBGGRR (note: blue before red)
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    // UNDOCUMENTED, but stable since Windows 10 1803 and used by TranslucentTB and most WPF "acrylic" libraries.
    // With ACCENT_ENABLE_ACRYLICBLURBEHIND, DWM blurs whatever is behind the window and tints it.
    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    // Broadcast to every top-level window when a system setting changes. lParam names the area;
    // "ImmersiveColorSet" = light/dark mode, accent color or transparency effects.
    public const int WM_SETTINGCHANGE = 0x001A;

    public const int WM_TIMECHANGE = 0x001E;           // system clock or time zone changed
    public const int WM_POWERBROADCAST = 0x0218;       // power events; wParam says which:
    public const int PBT_APMPOWERSTATUSCHANGE = 0x000A; //   plugged in / unplugged, battery level step
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;  //   woke up from sleep
    public const int PBT_POWERSETTINGCHANGE = 0x8013;  //   a setting we subscribed to changed (battery %)

    // ---------------- Battery ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = on battery, 1 = plugged in, 255 = unknown
        public byte BatteryFlag;         // bit 8 = charging, 128 = no battery, 255 = unknown
        public byte BatteryLifePercent;  // 0-100, 255 = unknown
        public byte SystemStatusFlag;    // 1 = battery saver on
        public int BatteryLifeTime;      // seconds left, -1 = unknown
        public int BatteryFullLifeTime;
    }

    public const byte BATTERY_FLAG_CHARGING = 8, BATTERY_FLAG_NO_BATTERY = 128, BATTERY_FLAG_UNKNOWN = 255;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    // Asks Windows to send WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE to hWnd whenever that power setting changes.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // ---------------- Foreground window (active app) ----------------

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;     // another window became the active one
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;       // call us on our own thread, no DLL injection
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;     // ignore our own windows (our popups)

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Subscribes to system-wide UI events (an event listener for the whole desktop). The callback runs on the
    // thread that subscribed, through its message loop: for us, the WPF UI thread. No polling.
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // Which process (and thread) owns a window.
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // Calls `callback` for each child window. Used to find the real app inside a UWP app's frame window.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    // ---------------- Top-level windows (dock: running apps) ----------------

    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;   // UWP / virtual-desktop hide/show
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    public const uint GW_OWNER = 4;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const int SW_RESTORE = 9;
    public const int DWMWA_CLOAKED = 14;

    // Calls `callback` for every top-level window, front to back (z-order).
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    // True when minimized.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLengthW(IntPtr hwnd);

    // A window's title (what the taskbar shows for it).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    // Queues a message for a window and returns at once. WM_CLOSE = what the window's X button sends: the app may
    // still ask "save changes?".
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    // Undocumented but long-standing (Alt+Tab uses it): switches to a window even when SetForegroundWindow
    // is refused by Windows' focus-stealing rules.
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    // "Cloaked" = present and "visible" but not drawn: suspended Store apps, windows on other virtual desktops.
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // The app ID of a packaged (Store) app's process, e.g. "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft...".
    // Returns APPMODEL_ERROR_NO_APPLICATION (15703) for classic apps.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder? id);

    // ---------------- kernel32.dll: processes ----------------

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;  // enough to read the exe path, works on most elevated apps too

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    // Full path of a process's .exe.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // With min = max = -1: removes as many pages as possible from the process's working set (see ProcessMemory).
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumSize, IntPtr maximumSize);

    // How many GDI (0) or USER (1) objects a process holds: brushes/bitmaps, windows/menus. For leak checks.
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint kind);

    // ---------------- Keyboard (Win+1…9 for the dock) ----------------

    // A low-level keyboard hook: Windows calls it for every key press, before the app in front gets it.
    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int hookType, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // What the hook receives for each key event.
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    // Bit 0x8000 set = the key is down right now.
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    // Simulates key presses (used to send one harmless key, see WinNumberKeys).
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // INPUT is a union of mouse/keyboard/hardware input; 40 bytes on 64-bit (the mouse variant is the biggest).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // ---------------- System load (notch rings) ----------------

    // Total time the CPUs spent idle / in the kernel (includes idle) / in user mode since boot, in 100 ns ticks.
    // Comparing two samples gives the CPU load in between.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;          // % of physical memory in use
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    // One snapshot of every process (name, CPU times, memory) in a single call, without opening any process.
    // This is what Task Manager uses. Officially "may change", but stable since Windows 2000.
    // .NET's Process.GetProcesses() + TotalProcessorTime opens a handle per process instead (~350 OpenProcess calls).
    // Fills `buffer` with a chain of SYSTEM_PROCESS_INFORMATION records (offsets in SystemStats.ProcessSnapshot).
    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);   // buffer too small, retry bigger

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    // ---------------- Power ----------------

    // Same as Win+L.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    // Sleep (hibernate = false). Returns a 1-byte BOOLEAN, hence I1.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.I1)] bool hibernate,
        [MarshalAs(UnmanagedType.I1)] bool force, [MarshalAs(UnmanagedType.I1)] bool wakeupEventsDisabled);

    public const uint EWX_SHUTDOWN = 0x01, EWX_REBOOT = 0x02, EWX_POWEROFF = 0x08;
    public const uint EWX_HYBRID_SHUTDOWN = 0x00400000;   // "fast startup", what the Start menu's Shut down does
    public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    // Restart or shut down. Apps get the normal chance to save. Needs SeShutdownPrivilege enabled first.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ExitWindowsEx(uint flags, uint reason);

    // ---------------- gdi32.dll: reading a bitmap's pixels ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;      // negative height = rows top to bottom
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObjectW(IntPtr hObject, int size, out BITMAP bitmap);

    // Copies a bitmap's pixels into our buffer in the format we describe (32-bit BGRA, top-down).
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    // Frees a GDI bitmap. GDI handles aren't garbage-collected and Windows caps them per process.
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // ---------------- advapi32.dll: privileges ----------------
    // A privilege is a right the user has but that's switched off in each process until it asks for it.

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    // The C struct holds an array of privileges; we only ever set one, so it's inlined here.
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
