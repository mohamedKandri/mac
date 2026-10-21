using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MacShell.Native;

namespace MacShell.Core;

// The tray icon and its menu: Reload config, Start with Windows, Quit.
// The menu is a normal WPF ContextMenu, so later it can be styled like the rest of the bar.
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _startWithWindows;

    // C# events are like Kotlin callbacks/listeners: App subscribes with `+=`.
    public event Action? ReloadRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var reload = new MenuItem { Header = "Reload config" };
        reload.Click += (_, _) => ReloadRequested?.Invoke();

        _startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _startWithWindows.Click += (_, _) => StartupRegistration.SetEnabled(_startWithWindows.IsChecked);

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        _menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Items = { reload, _startWithWindows, new Separator(), quit },
        };
        _menu.Opened += (_, _) => NotifyIcon.Activate(_menu);

        _icon = new NotifyIcon(LoadIco(), "MacShell");
        _icon.Clicked += ShowMenu;
    }

    public void Notify(string title, string text, bool warning = false) => _icon.ShowBalloon(title, text, warning);

    private void ShowMenu()
    {
        // Re-read the registry every time, in case the entry was changed outside MacShell.
        _startWithWindows.IsChecked = StartupRegistration.IsEnabled;
        _menu.IsOpen = true;
    }

    // Themes/MacShell.ico is compiled into the exe (see EmbeddedResource in MacShell.csproj).
    private static byte[] LoadIco()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("MacShell.ico")
            ?? throw new InvalidOperationException("MacShell.ico is missing from the build");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        // Icon first: in the crash handler this may run off the UI thread, where touching the menu throws.
        _icon.Dispose();
        _menu.IsOpen = false;
    }
}
