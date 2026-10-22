using System.Windows;

namespace MacShell.Native;

internal static class WindowStyles
{
    // Hides a window from Alt+Tab and the taskbar (WS_EX_TOOLWINDOW). With `noActivate`, clicking it also never
    // takes focus from the app you're using (WS_EX_NOACTIVATE): right for the bar, wrong for a menu you type into.
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate)
    {
        long ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    // A pure overlay: never focused, not in Alt+Tab, and every click goes to the window underneath.
    // (Click-through needs a layered window, i.e. WPF's AllowsTransparency=True.)
    public static void MakeClickThroughOverlay(IntPtr hwnd)
    {
        long ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    // Like MakeClickThroughOverlay, for a window drawn every frame (the dock magnifier), and much cheaper.
    // WPF's own transparent windows (AllowsTransparency=True) are pushed with UpdateLayeredWindow: in software
    // rendering the WHOLE window is redrawn and copied on every change (measured ~40 ms of CPU each time).
    // Instead, use a normal WPF window (AllowsTransparency=False, so WPF only redraws what changed) and get the
    // transparency from DWM, like the bar's glass: transparent WPF fill + DwmExtendFrameIntoClientArea(-1) makes
    // DWM use the pixels' alpha. Click-through still needs WS_EX_LAYERED + WS_EX_TRANSPARENT; with
    // SetLayeredWindowAttributes(alpha 255) the window becomes layered without changing how it's drawn.
    // Call from SourceInitialized (before the window is first shown: a layered window without attributes is invisible).
    public static void MakeFastClickThroughOverlay(System.Windows.Window window)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        // No Windows 11 rounding or 1 px border: the overlay must be invisible except for what it draws.
        int corners = Win32.DWMWCP_DONOTROUND, border = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        long ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED;
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Win32.SetLayeredWindowAttributes(hwnd, 0, 255, Win32.LWA_ALPHA);
    }

    // Moves a window's top-left corner to (x, y) in physical pixels, keeping its size, z-order and focus.
    public static void MoveTo(IntPtr hwnd, int x, int y) =>
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

    // Moves and sizes a window (physical pixels) and puts it on top of the topmost layer, without focusing it.
    public static void PlaceOnTop(IntPtr hwnd, Int32Rect rect) =>
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, Win32.SWP_NOACTIVATE);

    // A window's rectangle on screen, physical pixels.
    public static Int32Rect RectOf(IntPtr hwnd) =>
        Win32.GetWindowRect(hwnd, out var r) ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Int32Rect.Empty;
}
