using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacShell.Native;

// Frosted glass behind a WPF window, plus Windows 11 corner style. Used by the bar now, popups and the dock later.
//
// Why not the documented DWMWA_SYSTEMBACKDROP_TYPE (Mica / Acrylic)? Those switch to a flat color whenever the
// window isn't the active one, and our bar is never active (WS_EX_NOACTIVATE). The accent-policy blur stays on.
internal static class Backdrop
{
    // Call once from SourceInitialized (the native window must exist).
    public static void Prepare(Window window, bool roundedCorners)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;

        // WPF normally fills its window with an opaque color before drawing. Make that fill transparent...
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;

        // ...and have DWM honor WPF's alpha channel across the whole window.
        var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int corners = roundedCorners ? Win32.DWMWCP_ROUND : Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }

    // Turns the blur on with a tint (the config's background color), or off when Windows'
    // "Transparency effects" setting is off. `dark` picks dark or light system-drawn borders and shadow.
    public static void Apply(Window window, Color tint, bool blur, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int darkMode = dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        var accent = new Win32.ACCENT_POLICY
        {
            AccentState = blur ? Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND : Win32.ACCENT_DISABLED,
            // A fully transparent tint makes the acrylic glitch, so keep at least a little alpha.
            GradientColor = ((uint)Math.Max(tint.A, (byte)1) << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R,
        };

        // The API takes a pointer to the struct, so copy it into unmanaged memory for the call.
        int size = Marshal.SizeOf<Win32.ACCENT_POLICY>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new Win32.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = Win32.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            Win32.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
