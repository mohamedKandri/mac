using System.Windows;
using System.Windows.Threading;
using MacShell.Native;

namespace MacShell.Core;

// Keeps MacShell's RAM low over a whole day. The soak test (`--soak`) showed no leak: the managed heap stays at
// ~3 MB and handle counts are flat. What does grow and shrink is WPF's native memory while menus and overlays are
// open (64-80 MB, back down when they close). So, when MacShell has gone quiet, its idle pages are handed back to
// Windows (ProcessMemory.TrimWorkingSet):
//   - 20 s after any of its windows (menu, popup, overlay) closes,
//   - every 10 minutes otherwise,
// but never while one of its menus is open and focused (that would only slow the menu down).
public sealed class MemoryKeeper
{
    private static readonly TimeSpan AfterClose = TimeSpan.FromSeconds(20), Periodic = TimeSpan.FromMinutes(10);
    private readonly DispatcherTimer _afterClose = new() { Interval = AfterClose };
    private readonly DispatcherTimer _periodic = new() { Interval = Periodic };

    public MemoryKeeper()
    {
        _afterClose.Tick += (_, _) =>
        {
            _afterClose.Stop();
            TrimIfQuiet();
        };
        _periodic.Tick += (_, _) => TrimIfQuiet();
        _periodic.Start();

        // A window closing (Unloaded is a routed event, so one class handler sees every window) restarts the timer.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((_, _) =>
        {
            _afterClose.Stop();
            _afterClose.Start();
        }));
    }

    // Right away (used once after startup's cleanup).
    public void TrimNow() => ProcessMemory.TrimWorkingSet();

    private void TrimIfQuiet()
    {
        // MacShell's menus take focus while open; the bar, dock, notch and island never do.
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive))
        {
            _afterClose.Start();   // try again later
            return;
        }
        ProcessMemory.TrimWorkingSet();
    }
}
