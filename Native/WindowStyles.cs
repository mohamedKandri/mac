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
