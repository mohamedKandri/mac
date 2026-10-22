using MacShell.Native;

namespace MacShell.Core;

// MacShell's own actions (what the tray menu offers), handed to the bar so the logo menu can offer them too:
// with the taskbar hidden, the tray icon is out of reach. Also the dock's "Pin/Unpin", which edits config.json.
public sealed class AppCommands(Action reload, Action quit, Action<Func<List<string>, List<string>>> editPinned)
{
    public void Reload() => reload();
    public void Quit() => quit();

    // Changes dock.pinned in config.json (the hot reload then rebuilds the dock).
    public void EditPinned(Func<List<string>, List<string>> change) => editPinned(change);

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set => StartupRegistration.SetEnabled(value);
    }
}
