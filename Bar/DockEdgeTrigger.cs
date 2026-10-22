using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MacShell.Native;

namespace MacShell.Bar;

// A 2 px strip along the bottom edge of the monitor that reports when the mouse reaches it, so the auto-hiding
// dock can slide up. Event-driven (no polling of the mouse position).
//
// It's a layered window with a 1/255-alpha fill: fully transparent pixels would let the mouse pass through and we'd
// never hear about it; 1/255 is invisible but still "there". Never focused, not in Alt+Tab.
internal sealed class DockEdgeTrigger : OverlayWindow
{
    public event Action? Touched;

    public DockEdgeTrigger() : base(clickThrough: false)
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        MouseEnter += (_, _) => Touched?.Invoke();
    }

    // Full width of the monitor, the bottom 2 physical pixels.
    public void PlaceAtBottom(string deviceName)
    {
        if (Monitors.Find(deviceName) is not Monitors.Info monitor) return;
        var m = monitor.Bounds;
        if (!IsVisible) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, new Int32Rect(m.Left, m.Bottom - 2, m.Right - m.Left, 2));
    }
}
