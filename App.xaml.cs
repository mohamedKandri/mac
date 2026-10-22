using System.IO;
using System.Windows;
using MacShell.Bar;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;
using MacShell.Widgets;

namespace MacShell;

// App entry point. App.xaml declares this class; WPF generates Main() from it.
// OnStartup/OnExit are lifecycle hooks, like onCreate/onDestroy in Android.
public partial class App : Application
{
    // Kept in fields so they live as long as the app (a local variable could be garbage-collected).
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private ConfigService? _config;
    private BarManager? _bars;
    private ThemeManager? _theme;
    private Taskbar? _taskbar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance("MacShell.SingleInstance");
        if (!_instance.IsFirst)
        {
            // Another MacShell is already running: exit silently.
            Shutdown();
            return;
        }

        // Draw on the CPU instead of the GPU (must be set before any window exists). GPU rendering makes the
        // graphics driver allocate ~30 MB of private buffers; a mostly static 32 px bar doesn't need it.
        // Measured: ~80 MB -> ~50 MB private memory, glass and popups unchanged.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // On a crash, give the screen space back and remove the tray icon before the process dies.
        // DispatcherUnhandledException = exceptions on the UI thread; the AppDomain one catches every other thread.
        DispatcherUnhandledException += (_, args) => OnCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => OnCrash(args.ExceptionObject as Exception);

        // Locals (non-null) for use inside the lambdas below; the fields keep them alive.
        var tray = _tray = new TrayIcon();
        tray.QuitRequested += () => Shutdown();

        // config.json lives next to MacShell.exe (AppContext.BaseDirectory is the exe's folder, even for single-file).
        var config = _config = new ConfigService(Path.Combine(AppContext.BaseDirectory, "config.json"), WidgetRegistry.Check);
        config.Failed += error => tray.Notify("config.json has an error", $"{error}\nStill using the last working config.", warning: true);
        config.Changed += ApplyConfig;
        void Reload()
        {
            if (config.Reload(force: true))
                tray.Notify("Config reloaded", Summary(config.Current));
        }
        tray.ReloadRequested += Reload;
        config.Start();

        // Same actions for the logo menu (the tray is hidden along with the taskbar).
        var commands = new AppCommands(Reload, () => Shutdown());

        // Theme resources must exist before any window is built, since XAML looks them up on load.
        _theme = new ThemeManager(config.Current);
        var bars = _bars = new BarManager(config.Current, _theme, (title, text) => tray.Notify(title, text, warning: true), commands);

        // The dock replaces the Windows taskbar (dock.hideTaskbar); also undoes a hide left over by a hard kill.
        _taskbar = new Taskbar();
        ApplyTaskbar(config.Current);

        // A clean way for other programs (publish.ps1) to ask MacShell to quit, so the taskbar and screen space are
        // restored. A named event: a flag the OS shares between processes by name, like the single-instance mutex.
        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MacShell.Quit");
        new Thread(() =>
        {
            quitSignal.WaitOne();
            Dispatcher.InvokeAsync(() => Shutdown());
        }) { IsBackground = true, Name = "QuitSignal" }.Start();

        // At sign-in MacShell can start before Explorer's taskbar exists; registration then succeeds a few
        // seconds later (TaskbarCreated). So only warn if a bar is still undocked after 15 s.
        var dockCheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        dockCheck.Tick += (_, _) =>
        {
            dockCheck.Stop();
            if (!bars.AllDocked)
                tray.Notify("Couldn't reserve screen space", "Windows refused the AppBar registration, so maximized windows may cover the bar. It retries when Explorer restarts.", warning: true);
        };
        dockCheck.Start();

        // Startup leaves a lot of one-off garbage (XAML parsing, config, fonts). Once everything is idle, collect it
        // once and give the memory back to Windows; after that the app barely allocates.
        // Measured: removes the random +13 MB that about one launch in three showed.
        Dispatcher.InvokeAsync(() =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Developer switch: `MacShell.exe --open power-menu` (or `calendar`) opens a popup at startup, so popups
        // can be checked (screenshots, load errors) without clicking. Runs after the bar has laid out.
        int open = Array.IndexOf(e.Args, "--open");
        if (open >= 0 && open + 1 < e.Args.Length)
        {
            Action? show = e.Args[open + 1] switch
            {
                "power-menu" => () => bars.FindWidget<LogoWidget>()?.OpenMenu(),
                "calendar" => () => bars.FindWidget<ClockWidget>()?.OpenCalendar(),
                "dock-hover" => () => bars.Dock?.SimulateHover(1),
                "notch" => () => bars.Notch?.OpenFirstPopup(),
                _ => null,
            };
            if (show is not null)
                Dispatcher.InvokeAsync(show, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    // Called with every new valid config.
    private void ApplyConfig(AppConfig config)
    {
        _theme?.Apply(config);
        _bars?.Apply(config);
        ApplyTaskbar(config);
    }

    private void ApplyTaskbar(AppConfig config)
    {
        if (config.Dock.Enabled && config.Dock.HideTaskbar) _taskbar?.Hide();
        else _taskbar?.Restore();
    }

    private static string Summary(AppConfig c) =>
        $"Theme {c.Theme.ToString().ToLower()}, bar {c.Bar.Height}px, " +
        $"{c.Bar.Left.Count(w => w.Enabled)} widgets left, {c.Bar.Right.Count(w => w.Enabled)} right.";

    private void OnCrash(Exception? ex)
    {
        // Each step on its own: a failure in one must not skip the others.
        try { _bars?.ReleaseAll(); } catch { }
        try { _taskbar?.Restore(); } catch { }   // never leave the user without a taskbar
        try { _tray?.Dispose(); } catch { }
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{ex}\n\n");
        }
        catch { }
        // Not marked as handled: the app still exits, so a bug can't leave it half-broken.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bars?.Dispose();      // full screen area back right away, on every monitor
        _taskbar?.Dispose();   // taskbar visible again, with the user's own auto-hide setting
        _config?.Dispose();
        _theme?.Dispose();
        _tray?.Dispose();      // removes the tray icon right away instead of leaving a ghost
        _instance?.Dispose();
        base.OnExit(e);
    }
}
