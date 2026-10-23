using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MacShell.Bar;
using MacShell.Native;
using MacShell.Widgets;

namespace MacShell;

// Developer switch `MacShell.exe --soak <minutes>`: for an all-day memory check in minutes. Every 1.5 s it opens or
// closes the next UI piece in turn (dock menu, window list, magnifier, notch + popup, island, power menu, calendar)
// and every 30 s appends a line to soak.log next to the exe: private bytes, the managed heap after a full GC, and
// kernel / GDI / USER handle counts. A leak = a number that keeps climbing across lines. Quits when done.
internal sealed class SoakTest
{
    private readonly BarManager _bars;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "soak.log");
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _duration;
    private readonly DispatcherTimer _step = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _sample = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _index, _cycles;

    public SoakTest(BarManager bars, double minutes)
    {
        _bars = bars;
        _duration = TimeSpan.FromMinutes(minutes);
        File.WriteAllText(_log, $"soak {minutes} min, started {DateTime.Now:HH:mm:ss}\nmin  private_MB  heap_after_GC_MB  handles  gdi  user  cycles\n");
        _step.Tick += (_, _) => Step();
        _sample.Tick += (_, _) => Sample();
        Sample();
        _step.Start();
        _sample.Start();
    }

    private void Step()
    {
        var dock = _bars.Dock;
        switch (_index++ % 8)
        {
            case 0: dock?.OpenFirstMenu(); break;
            case 1: ClosePopups(); dock?.OpenFirstWindowList(); break;
            case 2: dock?.ResetForTest(); dock?.SimulateHover(1); break;
            case 3: dock?.ResetForTest(); _bars.Notch?.ExpandForTest(); _bars.Notch?.OpenFirstPopup(); break;
            case 4: _bars.Notch?.ResetForTest(); _bars.Island?.Expand(pin: true); break;
            case 5: _bars.Island?.ResetForTest(); _bars.FindWidget<LogoWidget>()?.OpenMenu(); break;
            case 6: ClosePopups(); _bars.FindWidget<ClockWidget>()?.OpenCalendar(); break;
            default: ClosePopups(); _cycles++; break;
        }
        if (_clock.Elapsed >= _duration)
        {
            _step.Stop();
            _sample.Stop();
            Sample();
            Application.Current.Shutdown();
        }
    }

    private static void ClosePopups()
    {
        foreach (var popup in Application.Current.Windows.OfType<PopupWindow>().ToList()) popup.SafeClose();
    }

    private void Sample()
    {
        using var self = Process.GetCurrentProcess();
        long heap = GC.GetTotalMemory(forceFullCollection: true);
        self.Refresh();
        File.AppendAllText(_log, string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0,4:F1}  {1,10:F1}  {2,16:F1}  {3,7}  {4,3}  {5,4}  {6,6}\n",
            _clock.Elapsed.TotalMinutes, self.PrivateMemorySize64 / 1048576.0, heap / 1048576.0,
            self.HandleCount, Win32.GetGuiResources(self.Handle, 0), Win32.GetGuiResources(self.Handle, 1), _cycles));
    }
}
