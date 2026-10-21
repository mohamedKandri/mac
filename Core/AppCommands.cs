using MacShell.Native;

namespace MacShell.Core;

// MacShell's own actions (what the tray menu offers), handed to the bar so the logo menu can offer them too:
// with the taskbar hidden, the tray icon is out of reach.
public sealed class AppCommands(Action reload, Action quit)
{
    public void Reload() => reload();
    public void Quit() => quit();

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set => StartupRegistration.SetEnabled(value);
    }
}
