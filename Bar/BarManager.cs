using System.Windows.Threading;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;
using MacShell.Widgets;

namespace MacShell.Bar;

// Owns every edge-docked window:
//  - one BarWindow per monitor ("monitors": "primary" -> just the primary one, "all" -> every monitor)
//  - the DockWindow, on the primary monitor ("dock.enabled")
// keeps that set in sync when monitors are plugged in or removed, and hides them under fullscreen apps.
public sealed class BarManager : IDisposable
{
    private readonly ThemeManager _theme;
    private readonly Action<string, string> _notify;
    private readonly Dictionary<string, BarWindow> _bars = [];   // by monitor device name, primary first
    private readonly MessageWindow _messages = new("MacShellDisplayWatcher");
    private readonly DispatcherTimer _displayDebounce;
    private readonly ForegroundWatcher _foreground = new();
    private readonly AppCommands _commands;
    private DockWindow? _dock;
    private AppConfig _config;

    public BarManager(AppConfig config, ThemeManager theme, Action<string, string> notify, AppCommands commands)
    {
        _config = config;
        _theme = theme;
        _notify = notify;
        _commands = commands;

        // Plugging a monitor in sends several WM_DISPLAYCHANGE in a row: wait until they stop.
        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _displayDebounce.Tick += (_, _) =>
        {
            _displayDebounce.Stop();
            Sync();
        };
        _messages.Message += (msg, wParam, _) =>
        {
            bool resumed = msg == Win32.WM_POWERBROADCAST && (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC;
            if (msg == Win32.WM_DISPLAYCHANGE || resumed)
            {
                _displayDebounce.Stop();
                _displayDebounce.Start();
            }
        };

        // Switching apps can bring a fullscreen app to the front (or leave one) without Explorer telling us.
        _foreground.Changed += _ => CheckFullscreen();

        Sync();
    }

    // The bars, then the dock: everything that reserves space or hides for fullscreen.
    private IEnumerable<ShellWindow> All => _dock is null ? _bars.Values : _bars.Values.Append<ShellWindow>(_dock);

    public void Apply(AppConfig config)
    {
        _config = config;
        Sync();
        foreach (var bar in _bars.Values) bar.Apply(config);
        _dock?.Apply(config);
    }

    // False if Windows refused to reserve space for any bar or the dock.
    public bool AllDocked => All.All(w => w.IsDocked);

    // For developer switches.
    public DockWindow? Dock => _dock;

    // For developer switches: a widget on the primary bar.
    public T? FindWidget<T>() where T : class, IWidget => _bars.Values.FirstOrDefault()?.FindWidget<T>();

    // Crash handler: give every window's space back. Never throws.
    public void ReleaseAll()
    {
        foreach (var window in All.ToList())
        {
            try { window.ReleaseAppBar(); } catch { }
        }
    }

    private void Sync()
    {
        var primary = Monitors.Primary();
        var wanted = _config.Bar.Monitors == MonitorMode.All ? Monitors.All() : [primary];
        var wantedNames = wanted.Select(m => m.DeviceName).ToHashSet();

        // Monitor gone (or no longer wanted): give its space back and close its bar.
        foreach (var (device, bar) in _bars.ToList())
        {
            if (wantedNames.Contains(device)) continue;
            Close(bar);
            _bars.Remove(device);
        }

        // New monitor: new bar.
        foreach (var monitor in wanted)
        {
            if (_bars.ContainsKey(monitor.DeviceName)) continue;
            var bar = new BarWindow(_config, _theme, _notify, _commands, monitor.DeviceName);
            _bars[monitor.DeviceName] = bar;
            Open(bar);
        }

        // The dock follows the primary monitor; close it if disabled or the primary changed.
        if (_dock is not null && (!_config.Dock.Enabled || _dock.DeviceName != primary.DeviceName))
        {
            Close(_dock);
            _dock = null;
        }
        if (_dock is null && _config.Dock.Enabled)
        {
            _dock = new DockWindow(_config, _theme, _notify, primary.DeviceName);
            Open(_dock);
        }
    }

    private void Open(ShellWindow window)
    {
        window.FullscreenNotified += opening =>
        {
            // "Closed" is trusted as-is (the window may still be shrinking back); "opened" is checked per monitor.
            if (opening) CheckFullscreen();
            else foreach (var w in All) w.SetHiddenForFullscreen(false);
        };
        window.Show();
    }

    private static void Close(ShellWindow window)
    {
        window.ReleaseAppBar();
        window.Close();
    }

    private void CheckFullscreen()
    {
        foreach (var window in All)
            window.SetHiddenForFullscreen(Monitors.IsFullscreenAppOn(window.DeviceName));
    }

    public void Dispose()
    {
        _displayDebounce.Stop();
        _foreground.Dispose();
        _messages.Dispose();
        foreach (var window in All.ToList()) Close(window);
        _bars.Clear();
        _dock = null;
    }
}
