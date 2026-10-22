using System.Windows;
using MacShell.Native;

namespace MacShell.Bar;

// Taskbar-style features (milestone 4): the right-click menu, the window list on hover and Win+1…9.
public partial class DockWindow
{
    private DockMenu? _menu;
    private WinNumberKeys? _numberKeys;

    // Hover window list (Bar/DockWindowList): opens after resting on a tile, stays while the mouse is on it.
    private static readonly TimeSpan ListOpenDelay = TimeSpan.FromMilliseconds(400), ListCloseDelay = TimeSpan.FromMilliseconds(300);
    private readonly System.Windows.Threading.DispatcherTimer _listOpenTimer = new() { Interval = ListOpenDelay };
    private readonly System.Windows.Threading.DispatcherTimer _listCloseTimer = new() { Interval = ListCloseDelay };
    private DockWindowList? _list;
    private DockAppTile? _listTile, _hoverTile;

    // ---------------- Window list on hover ----------------

    private void SetUpWindowList()
    {
        _listOpenTimer.Tick += (_, _) =>
        {
            _listOpenTimer.Stop();
            if (_hoverTile is not null) ShowWindowList(_hoverTile);
        };
        _listCloseTimer.Tick += (_, _) =>
        {
            _listCloseTimer.Stop();
            if (_hoverTile is null && _list?.IsMouseOver != true) CloseWindowList();
        };
    }

    // Resting on a tile opens its window list after a moment; leaving it starts the close timer.
    private void WatchHover(DockAppTile tile)
    {
        tile.MouseEnter += (_, _) =>
        {
            _hoverTile = tile;
            _listCloseTimer.Stop();
            if (_list is not null) ShowWindowList(tile);   // already showing a list: follow the mouse at once
            else
            {
                _listOpenTimer.Stop();
                _listOpenTimer.Start();
            }
        };
        tile.MouseLeave += (_, _) =>
        {
            if (_hoverTile == tile) _hoverTile = null;
            _listOpenTimer.Stop();
            _listCloseTimer.Stop();
            _listCloseTimer.Start();
        };
    }

    // Shows `tile`'s windows above it when it has two or more (one window: the click already goes straight there).
    private void ShowWindowList(DockAppTile tile)
    {
        if (_list is not null && _listTile == tile) return;
        CloseWindowList();
        _windows.Refresh();   // fresh windows and titles
        if (tile.Windows.Count < 2 || _menu is not null) return;

        _listTile = tile;
        _list = new DockWindowList(tile.App.Name, tile.Windows,
            activate: hwnd =>
            {
                CloseWindowList();
                WindowTracker.Activate(hwnd);
            },
            close: hwnd =>
            {
                CloseWindowList();
                WindowTracker.Close(hwnd);
            });
        _list.MouseEnter += (_, _) => _listCloseTimer.Stop();
        _list.MouseLeave += (_, _) =>
        {
            _listCloseTimer.Stop();
            _listCloseTimer.Start();
        };

        // Above the magnified tile and its name label (the magnifier's headroom), centered on the tile.
        Point center = tile.PointToScreen(new Point(tile.ActualWidth / 2, 0));
        Point dockTop = PointToScreen(new Point(0, 0));
        double headroom = _config.Dock.MagnifiedSize - _config.Dock.TileSize + LabelSpace + 4;
        _list.PlaceAbove((int)center.X, (int)Math.Round(dockTop.Y - headroom * Scale), Scale);
    }

    private void CloseWindowList()
    {
        _list?.Close();   // closed, not hidden: a layered window keeps its pixels while hidden
        _list = null;
        _listTile = null;
    }

    // ---------------- Win+1 … Win+9 (dock.numberShortcuts) ----------------

    private void SetUpNumberKeys()
    {
        if (_config.Dock.NumberShortcuts == (_numberKeys is not null)) return;
        _numberKeys?.Dispose();
        _numberKeys = null;
        if (!_config.Dock.NumberShortcuts) return;
        _numberKeys = new WinNumberKeys();
        _numberKeys.Pressed += OpenByNumber;
    }

    // Win+N = clicking the Nth app (pinned, then running); Shift+Win+N = a new window of it.
    private void OpenByNumber(int index, bool newWindow)
    {
        var tiles = AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()).ToList();
        if (index >= tiles.Count) return;
        if (newWindow) AppCatalog.Launch(tiles[index].App);
        else OnTileClicked(tiles[index]);
    }

    // For the `--open dock-windows` developer switch: the window list of the first app with 2+ windows.
    public void OpenFirstWindowList()
    {
        _windows.Refresh();
        if (AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()).FirstOrDefault(t => t.Windows.Count >= 2) is DockAppTile tile)
            ShowWindowList(tile);
    }

    // For the `--open dock-menu` developer switch: the first app's right-click menu.
    public void OpenFirstMenu()
    {
        if (AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()).FirstOrDefault() is DockAppTile tile)
            OnTileRightClicked(tile);
    }

    // Right-click: the taskbar-style menu (Bar/DockMenu), above the tile.
    private void OnTileRightClicked(DockAppTile tile)
    {
        _listOpenTimer.Stop();
        CloseWindowList();
        _menu?.SafeClose();
        _windows.Refresh();   // current windows and titles
        var app = tile.App;
        Action? pin = null, unpin = null;
        if (tile.PinEntry is string entry)
            unpin = () => _commands.EditPinned(list =>
            {
                list.Remove(entry);
                return list;
            });
        else
            pin = () => _commands.EditPinned(list =>
            {
                list.Add(PinName(app));
                return list;
            });
        // The jump list is filed under the app's AppUserModelID: the AppsFolder parsing name, or (for a tile made
        // from an exe path) the ID its windows carry.
        string? appId = app.IsFile ? tile.Windows.FirstOrDefault(w => w.AppId is not null)?.AppId : app.ParsingName;
        var recent = appId is null ? [] : JumpLists.Recent(appId, 8);
        _menu = new DockMenu(Theme, tile, recent, new DockMenu.Actions(() => AppCatalog.Launch(app), pin, unpin));
        _menu.Closed += (_, _) => _menu = null;
        _menu.ShowAbove(tile, this);
    }

    // What to write in dock.pinned for an app: its Start menu name when that's unambiguous (readable in the
    // file), else its app ID, else its exe path.
    private string PinName(AppCatalog.App app)
    {
        if (app.IsFile) return app.ParsingName;
        bool unique = _catalog.Count(a => a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)) == 1;
        return unique ? app.Name : app.ParsingName;
    }
}
