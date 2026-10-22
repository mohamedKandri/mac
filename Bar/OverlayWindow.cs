using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Base for MacShell's floating see-through windows: the notch and its popup, the island, the dock's magnifier,
// window list and edge trigger. A layered window (AllowsTransparency): only what it draws is visible, and fully
// transparent pixels let clicks through to whatever is below. Never in the taskbar or Alt+Tab, never takes focus,
// always on top, white Inter text.
// `clickThrough` = the whole window ignores the mouse (the magnifier, the notch popup), not just its empty pixels.
internal abstract class OverlayWindow : Window
{
    protected OverlayWindow(bool clickThrough)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (clickThrough) WindowStyles.MakeClickThroughOverlay(hwnd);
            else WindowStyles.MakeToolWindow(hwnd, noActivate: true);
        };
    }
}
