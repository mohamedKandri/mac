using System.Windows;
using MacShell.Native;

namespace MacShell.Bar;

// dock.autoHide: slide away when not in use, back when the mouse touches the bottom edge.
public partial class DockWindow
{
    // ---------------- Auto-hide (dock.autoHide) ----------------

    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(400);

    private Int32Rect _target;                 // where the dock sits when shown (physical pixels)
    private DockEdgeTrigger? _trigger;
    private System.Windows.Threading.DispatcherTimer? _hideTimer;
    private System.Windows.Threading.DispatcherTimer? _slideTimer;
    private bool _shown = true;

    protected override bool ReservesSpace => !_config.Dock.AutoHide;

    private void SetUpAutoHide(bool on)
    {
        if (!on)
        {
            _hideTimer?.Stop();
            _trigger?.Close();
            _trigger = null;
            if (!_shown) SlideIn();
            return;
        }
        if (_trigger is null)
        {
            _trigger = new DockEdgeTrigger();
            _trigger.Touched += SlideIn;
            _trigger.PlaceAtBottom(DeviceName);
        }
        _hideTimer ??= new System.Windows.Threading.DispatcherTimer();
        _hideTimer.Tick -= OnHideTimer;
        _hideTimer.Tick += OnHideTimer;
    }

    private void OnHideTimer(object? sender, EventArgs e)
    {
        _hideTimer!.Stop();
        if (!IsMouseOver) SlideOut();
    }

    private void ScheduleHide(TimeSpan delay)
    {
        if (!_config.Dock.AutoHide || _hideTimer is null) return;
        _hideTimer.Stop();
        _hideTimer.Interval = delay;
        _hideTimer.Start();
    }

    private void SlideIn()
    {
        _hideTimer?.Stop();
        if (_shown) return;
        _shown = true;
        // Move below the screen first, then show: otherwise it flashes at its final spot for a frame.
        WindowStyles.MoveTo(new System.Windows.Interop.WindowInteropHelper(this).Handle, _target.X, ScreenBottom);
        if (!IsVisible) Show();
        Slide(fromY: ScreenBottom, toY: _target.Y, then: null);
    }

    private void SlideOut()
    {
        if (!_shown || !_config.Dock.AutoHide) return;
        _shown = false;
        _hovering = false;
        StopLoop();
        Slide(fromY: _target.Y, toY: ScreenBottom, then: Hide);
    }

    private int ScreenBottom => Monitors.Find(DeviceName)?.Bounds.Bottom ?? _target.Y + _target.Height;

    // Moves the window vertically over 150 ms with an ease-out curve (fast start, gentle stop).
    private void Slide(int fromY, int toY, Action? then)
    {
        _slideTimer?.Stop();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var start = DateTime.Now;
        WindowStyles.MoveTo(hwnd, _target.X, fromY);
        _slideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _slideTimer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - start) / SlideTime);
            double eased = 1 - Math.Pow(1 - t, 3);
            WindowStyles.MoveTo(hwnd, _target.X, (int)Math.Round(fromY + (toY - fromY) * eased));
            if (t < 1) return;
            _slideTimer!.Stop();
            then?.Invoke();
        };
        _slideTimer.Start();
    }

    protected override void ShowAfterFullscreen()
    {
        if (!_config.Dock.AutoHide)
        {
            Show();
            return;
        }
        _shown = false;                         // stays hidden until the mouse reaches the edge again
        _trigger?.PlaceAtBottom(DeviceName);
    }
}
