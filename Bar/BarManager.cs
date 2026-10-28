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
    private NotchWindow? _notch;
    private string _notchKey = "";
    private IslandWindow? _island;
    private string _islandKey = "";
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
    internal NotchWindow? Notch => _notch;
    internal IslandWindow? Island => _island;

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
        // Also rebuilt when dock.style changes: blur and corners are fixed when its native window is created.
        if (_dock is not null && (!_config.Dock.Enabled || _dock.DeviceName != primary.DeviceName || _dock.DockStyle != _config.Dock.Style))
        {
            Close(_dock);
            _dock = null;
        }
        if (_dock is null && _config.Dock.Enabled)
        {
            _dock = new DockWindow(_config, _theme, _notify, _commands, primary.DeviceName);
            Open(_dock);
        }

        // The notch, also on the primary monitor; rebuilt when its config changes (cheap: a few rings).
        string notchKey = System.Text.Json.JsonSerializer.Serialize(_config.Notch) + primary.DeviceName;
        if (_notch is not null && notchKey != _notchKey)
        {
            _notch.Close();
            _notch = null;
        }
        _notchKey = notchKey;
        if (_notch is null && _config.Notch.Enabled)
        {
            _notch = new NotchWindow(_config.Notch, primary.DeviceName);
            _notch.Place();
        }
        else _notch?.Place();   // display change: re-center on the edge

        // The island sits in the primary monitor's menu bar and is owned by that bar window (an owned window always
        // stays above its owner, so the bar can never cover it). Rebuilt when its config, the bar height or the
        // bar window itself changes.
        _bars.TryGetValue(primary.DeviceName, out var primaryBar);
        IntPtr owner = primaryBar is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(primaryBar).Handle;
        string islandKey = System.Text.Json.JsonSerializer.Serialize(_config.Island) + primary.DeviceName + _config.Bar.Height + owner;
        if (_island is not null && islandKey != _islandKey)
        {
            _island.Close();
            _island = null;
        }
        _islandKey = islandKey;
        if (_island is null && _config.Island.Enabled && owner != IntPtr.Zero)
        {
            _island = new IslandWindow(_config.Bar.Height, _config.Island, primary.DeviceName, owner);
            _island.Place();
        }
        else _island?.Place();
    }

    private void Open(ShellWindow window)
    {
        window.FullscreenNotified += opening =>
        {
            // "Closed" is trusted as-is (the window may still be shrinking back); "opened" is checked per monitor.
            if (opening) CheckFullscreen();
            else
            {
                foreach (var w in All) w.SetHiddenForFullscreen(false);
                _notch?.SetHiddenForFullscreen(false);
                _island?.SetHiddenForFullscreen(false);
            }
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
        if (_notch is not null)
            _notch.SetHiddenForFullscreen(Monitors.IsFullscreenAppOn(_notch.DeviceName));
        if (_island is not null)
            _island.SetHiddenForFullscreen(Monitors.IsFullscreenAppOn(_island.DeviceName));
    }

    public void Dispose()
    {
        _displayDebounce.Stop();
        _foreground.Dispose();
        _messages.Dispose();
        foreach (var window in All.ToList()) Close(window);
        _notch?.Close();
        _island?.Close();
        _bars.Clear();
        _dock = null;
        _notch = null;
        _island = null;
    }
}
