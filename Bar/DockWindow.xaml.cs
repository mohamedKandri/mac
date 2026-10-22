using System.Windows;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The dock at the bottom of the primary monitor: a solid black shape on the bottom edge, styled like the
// right-edge notch. It reserves a strip as tall as the panel (so maximized windows stop above it).
// Magnified tiles draw above the strip, in the DockMagnifier overlay.
//
//   ┌──────────── reserved strip (full width) ────────────┐
//   │            ╭───── panel (black) ─────╮               │
//   │___________╱                           ╲______________│  <- 16 px flares into the screen edge
//   └──────────────────────────────────────────────────────┘
public partial class DockWindow : ShellWindow
{
    // macOS proportions (owner's reference screenshot): padding 6 top / 8 sides / 3 bottom, a 3 px running dot 2 px
    // under the tile, 6 between tiles. Since the black notch style (2026-10-22) it sits on the edge (no gap), with
    // the notch's 16 px flares on both sides and 18 px top corners.
    private const double EdgeGap = 0, PaddingTop = 6, PaddingSide = 8, PaddingBottom = 3, DotRow = 2 + 3;
    private const double Flare = 16, CornerRadius = 18;

    private const double TileGap = 6;

    private readonly Action<string, string> _notify;
    private readonly AppCommands _commands;
    private DockMenu? _menu;
    private WinNumberKeys? _numberKeys;

    // Hover window list (Bar/DockWindowList): opens after resting on a tile, stays while the mouse is on it.
    private static readonly TimeSpan ListOpenDelay = TimeSpan.FromMilliseconds(400), ListCloseDelay = TimeSpan.FromMilliseconds(300);
    private readonly System.Windows.Threading.DispatcherTimer _listOpenTimer = new() { Interval = ListOpenDelay };
    private readonly System.Windows.Threading.DispatcherTimer _listCloseTimer = new() { Interval = ListCloseDelay };
    private DockWindowList? _list;
    private DockAppTile? _listTile, _hoverTile;
    private readonly WindowTracker _windows = new();
    private bool _binFull;
    private List<AppCatalog.App> _catalog = [];
    private int _iconPixels;
    private AppConfig _config;
    private string _tilesKey = "";

    public DockWindow(AppConfig config, ThemeManager theme, Action<string, string> notify, AppCommands commands, string deviceName)
        : base(theme, deviceName)
    {
        InitializeComponent();
        // Always white on black, whatever the Windows theme (like the notch): these override the theme's colors
        // for everything inside the dock (running dots, dividers, Trash tile).
        Resources[ThemeKeys.Foreground] = Frozen(System.Windows.Media.Colors.White);
        Resources[ThemeKeys.Hairline] = Frozen(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        Resources[ThemeKeys.Pressed] = Frozen(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        Root.SizeChanged += (_, _) => UpdateShape();
        _config = config;
        _notify = notify;
        _commands = commands;
        SetUpNumberKeys();
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
        ApplySizes();
        BuildTiles();
        _windows.Changed += UpdateRunning;
        UpdateRunning();
        // Trash state: checked at start, whenever the mouse enters the dock, and after a drop (no live subscription,
        // see Native/RecycleBin).
        UpdateTrash();
        Panel.MouseEnter += (_, _) => UpdateTrash();

        // Auto-hide: show briefly at start (so you know it's there), then slide away until the mouse reaches the edge.
        SetUpAutoHide(_config.Dock.AutoHide);
        MouseEnter += (_, _) => _hideTimer?.Stop();
        MouseLeave += (_, _) => ScheduleHide(HideDelay);
        ContentRendered += (_, _) => ScheduleHide(TimeSpan.FromSeconds(1.5));

        // Magnification follows the mouse anywhere on the panel (MouseMove bubbles up from the tiles).
        Panel.MouseMove += (_, e) =>
        {
            _mouseX = e.GetPosition(Root).X;
            if (!_hovering) BeginHover();
            StartLoop();
        };
        Panel.MouseLeave += (_, _) =>
        {
            _hovering = false;
            StartLoop();   // keep animating until the tiles have shrunk back
        };
    }

    // ---------------- Robustness (dock step 6) ----------------

    // A fullscreen app took over: the magnifier is a separate window, so close it with the dock.
    protected override void OnHiddenForFullscreen(bool hidden)
    {
        if (!hidden) return;
        _hovering = false;
        StopLoop();
        _hideTimer?.Stop();
        _trigger?.Hide();   // a game at the bottom edge shouldn't summon the dock
    }

    // Display scale changed (Settings > Display, or the dock moved to a monitor with another scale): icons were
    // rendered for the old pixel density, so render them again and re-place the panel.
    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _tilesKey = "";
        BuildTiles();
        Dispatcher.InvokeAsync(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------- Trash (dock step 5) ----------------

    private void UpdateTrash()
    {
        _binFull = !RecycleBin.IsEmpty();
        TrashIcon.Data = (System.Windows.Media.Geometry)FindResource(_binFull ? "Icon.TrashFull" : "Icon.Trash");
        TrashIcon.FillData = _binFull ? (System.Windows.Media.Geometry)FindResource("Icon.TrashPaper") : null;
        if (_hovering) LoadMagnifier();   // keep the magnified copy in sync
    }

    private void OnTrashClick(object sender, RoutedEventArgs e) => RecycleBin.Open();

    // Accept only files/folders from Explorer (or any app that drags files); show that the drop will land.
    private void OnTrashDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Move : DragDropEffects.None;
        SetTrashHighlight(files);
        e.Handled = true;
    }

    private void OnTrashDragLeave(object sender, DragEventArgs e) => SetTrashHighlight(false);

    private void OnTrashDrop(object sender, DragEventArgs e)
    {
        SetTrashHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        RecycleBin.Recycle(paths,
            failed: error => Dispatcher.InvokeAsync(() => _notify("Couldn't move to the Recycle Bin", error)),
            done: () => Dispatcher.InvokeAsync(UpdateTrash));
        e.Handled = true;
    }

    private void SetTrashHighlight(bool on)
    {
        TrashTile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, on ? ThemeKeys.Selection : ThemeKeys.Hairline);
        TrashTile.BorderThickness = new Thickness(on ? 2 : 1);
    }

    // ---------------- Magnification + app name (dock step 4) ----------------

    private const double LabelSpace = 34;        // room above a magnified tile for its name
    private const double Smoothing = 0.55;       // per frame: how far tiles move towards their target size (0.3 felt laggy)
    private static readonly TimeSpan BounceTime = TimeSpan.FromMilliseconds(280);

    private DockMagnifier? _magnifier;
    private List<DockMagnifier.Item> _items = [];
    private readonly List<FrameworkElement> _hiddenWhileMagnified = [];
    private double[] _scales = [];
    private bool _hovering, _looping;
    private int _framesShown;
    private double _mouseX;
    private int _bounceIndex = -1;
    private DateTime _bounceStart;

    private double Scale => (Monitors.Find(DeviceName)?.Dpi ?? 96) / 96.0;

    // For the `--open dock-hover` developer switch: magnify as if the mouse sat on tile `index`
    // (it stays until the real mouse enters and leaves the dock).
    public void SimulateHover(int index)
    {
        BeginHover();
        if (_items.Count == 0) return;
        _mouseX = _items[Math.Clamp(index, 0, _items.Count - 1)].CenterX;
        StartLoop();
    }

    private void BeginHover()
    {
        _hovering = true;
        _magnifier ??= new DockMagnifier(_config.Dock.MagnifiedSize - _config.Dock.TileSize + LabelSpace);
        LoadMagnifier();
        _magnifier.PlaceOver(WindowStyles.RectOf(new System.Windows.Interop.WindowInteropHelper(this).Handle), Scale);
        _framesShown = 0;
    }

    // Copies the current tiles into the overlay, with their positions in panel coordinates.
    private void LoadMagnifier()
    {
        if (_magnifier is null) return;
        _items.Clear();
        _hiddenWhileMagnified.Clear();
        foreach (var tile in AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()))
            Add(tile.IconElement, tile.App.Name, (tile.IconElement as System.Windows.Controls.Image)?.Source, isTrash: false);
        Add(TrashTile, _binFull ? "Trash (full)" : "Trash", null, isTrash: true);
        _scales = Enumerable.Repeat(1.0, _items.Count).ToArray();
        _magnifier.Load(_items);
        _magnifier.Render(_scales, -1);

        void Add(FrameworkElement element, string name, System.Windows.Media.ImageSource? icon, bool isTrash)
        {
            Point bottomCenter = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight), Root);
            _items.Add(new DockMagnifier.Item(icon, isTrash, name, bottomCenter.X, bottomCenter.Y, element.ActualWidth, isTrash && _binFull));
            _hiddenWhileMagnified.Add(element);
        }
    }

    // Per-frame animation, only while needed: CompositionTarget.Rendering fires once per screen refresh while
    // subscribed (like requestAnimationFrame), so nothing runs when the mouse is elsewhere.
    private void StartLoop()
    {
        if (_looping || _magnifier is null) return;
        _looping = true;
        System.Windows.Media.CompositionTarget.Rendering += OnFrame;
    }

    // Settled while the mouse rests on a tile: stop drawing frames, keep the overlay. The next MouseMove resumes.
    private void PauseLoop()
    {
        _looping = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
    }

    // Mouse gone and tiles back to normal: show the real icons again and close the overlay. Closing (rather than
    // hiding) frees its ~12 MB of layered-window pixels; re-creating it on the next hover takes a few ms.
    private void StopLoop()
    {
        PauseLoop();
        foreach (var element in _hiddenWhileMagnified) element.Opacity = 1;
        _magnifier?.Close();
        _magnifier = null;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_magnifier is null || _items.Count == 0)
        {
            StopLoop();
            return;
        }

        double maxScale = _config.Dock.MagnifiedSize / _config.Dock.TileSize;
        double reach = _config.Dock.TileSize * 1.8;   // how far the effect spreads: neighbours reach ~60 px at default sizes
        int hovered = -1;
        double nearest = double.MaxValue;
        bool settled = true;
        var shown = new double[_items.Count];

        for (int i = 0; i < _items.Count; i++)
        {
            double distance = Math.Abs(_mouseX - _items[i].CenterX);
            if (_hovering && distance < _items[i].Size / 2 + TileGap / 2 && distance < nearest)
            {
                hovered = i;
                nearest = distance;
            }

            // Cosine falloff: full size under the mouse, fading to normal size at `reach`.
            double target = _hovering && distance < reach ? 1 + (maxScale - 1) * Math.Cos(distance / reach * Math.PI / 2) : 1;
            _scales[i] += (target - _scales[i]) * Smoothing;
            if (Math.Abs(_scales[i] - target) > 0.002 || (!_hovering && Math.Abs(_scales[i] - 1) > 0.002)) settled = false;
            shown[i] = _scales[i];
        }

        // Click feedback on the magnified copy (the real tile's own bounce is hidden underneath).
        var sinceBounce = DateTime.Now - _bounceStart;
        if (_bounceIndex >= 0 && _bounceIndex < shown.Length && sinceBounce < BounceTime)
        {
            shown[_bounceIndex] *= 1 - 0.12 * Math.Sin(Math.PI * sinceBounce / BounceTime);
            settled = false;
        }

        _magnifier.Render(shown, hovered);

        // Hide the real icons only once the overlay has drawn its copies (a couple of frames), so nothing blinks.
        if (++_framesShown == 2)
            foreach (var element in _hiddenWhileMagnified) element.Opacity = 0;

        if (settled && _framesShown >= 2)
        {
            if (_hovering) PauseLoop();
            else StopLoop();
        }
    }

    protected override AppBarEdge Edge => AppBarEdge.Bottom;
    protected override bool RoundedCorners => false;
    protected override bool Glass => false;
    protected override string TintColor => Theme.Palette.Dock;

    private double PanelHeight => PaddingTop + _config.Dock.TileSize + DotRow + PaddingBottom;

    // Only the gap and the panel: magnified tiles (and their name label) may float over the bottom of maximized
    // windows while hovering, like macOS. The overlay is click-through, so nothing below gets blocked.
    protected override double ReservedThickness => EdgeGap + PanelHeight;

    // Panel + flares, centered on the monitor, on the bottom of the reserved strip.
    protected override StripPlacement PlaceInStrip => (reserved, monitor, scale) =>
    {
        // Ask for the natural width from scratch: invalidate first, so a size cached from before a tile was
        // added or removed can't be reused (that left an empty gap after Trash when an app closed).
        foreach (var panel in new UIElement[] { AppTiles, RunningTiles, Panel, Root }) panel.InvalidateMeasure();
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Round(Root.DesiredSize.Width * scale);
        int height = (int)Math.Round(PanelHeight * scale);
        int bottom = reserved.Y + reserved.Height - (int)Math.Round(EdgeGap * scale);
        _target = new Int32Rect(monitor.X + (monitor.Width - width) / 2, bottom - height, width, height);
        if (_config.Dock.AutoHide)
        {
            Dispatcher.InvokeAsync(() => _trigger?.PlaceAtBottom(DeviceName));
            // Slid away: keep it hidden at its new place (SetWindowPos doesn't show a hidden window).
        }
        return _target;
    };

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

    public void Apply(AppConfig config)
    {
        bool sizesChanged = config.Dock.TileSize != _config.Dock.TileSize || config.Dock.MagnifiedSize != _config.Dock.MagnifiedSize;
        bool autoHideChanged = config.Dock.AutoHide != _config.Dock.AutoHide;
        _config = config;
        SetUpNumberKeys();
        if (sizesChanged) ApplySizes();
        bool tilesChanged = BuildTiles();
        if (sizesChanged || autoHideChanged) Redock();   // also takes or gives back the reserved strip
        else if (tilesChanged) Reposition();             // same strip, wider or narrower panel
        if (autoHideChanged)
        {
            SetUpAutoHide(config.Dock.AutoHide);
            ScheduleHide(TimeSpan.FromSeconds(1.5));
        }
    }

    // (Re)creates the app tiles when the pinned list or sizes changed. Returns whether it did.
    private bool BuildTiles()
    {
        string key = string.Join("|", _config.Dock.Pinned) + $"|{_config.Dock.TileSize}|{_config.Dock.MagnifiedSize}";
        if (key == _tilesKey) return false;
        _tilesKey = key;

        // Sizes may have changed, and with them the overlay's headroom: start it over on the next hover.
        StopLoop();
        _hovering = false;

        AppTiles.Children.Clear();
        RunningTiles.Children.Clear();   // rebuilt by UpdateRunning, at the new size
        _catalog = AppCatalog.StartMenuApps();
        // Icons are extracted at the magnified size and screen scale, so they stay sharp when magnified.
        _iconPixels = (int)Math.Ceiling(_config.Dock.MagnifiedSize * (Monitors.Find(DeviceName)?.Dpi ?? 96) / 96.0);
        var missing = new List<string>();

        foreach (string pin in _config.Dock.Pinned)
        {
            if (AppCatalog.Resolve(pin, _catalog, out string? hint) is not AppCatalog.App app)
            {
                missing.Add($"'{pin}': {hint}");
                continue;
            }
            AppTiles.Children.Add(NewTile(app, pin));
        }

        if (missing.Count > 0)
            _notify("Dock: pinned app not found", string.Join("\n", missing));
        UpdateRunning();
        return true;
    }

    private DockAppTile NewTile(AppCatalog.App app, string? pin = null)
    {
        var tile = new DockAppTile(app, _config.Dock.TileSize, _iconPixels) { Margin = new Thickness(0, 0, TileGap, 0), PinEntry = pin };
        tile.Clicked += OnTileClicked;
        tile.RightClicked += OnTileRightClicked;
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
        return tile;
    }

    // ---------------- Window list on hover ----------------

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
        _menu = new DockMenu(Theme, tile, new DockMenu.Actions(() => AppCatalog.Launch(app), pin, unpin));
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

    // Matches the open windows to tiles: pinned tiles get their dots; other apps get tiles of their own after the
    // pinned ones. Existing running tiles keep their place (no reshuffling as windows change z-order).
    private void UpdateRunning()
    {
        var windows = _windows.Windows;
        int tilesBefore = AppTiles.Children.Count + RunningTiles.Children.Count;
        var claimed = new HashSet<IntPtr>();

        foreach (var tile in AppTiles.Children.OfType<DockAppTile>())
        {
            var mine = windows.Where(tile.Owns).ToList();
            tile.SetWindows(mine);
            claimed.UnionWith(mine.Select(w => w.Handle));
        }

        // The rest, grouped per app, in order of the app's front-most window.
        var others = windows.Where(w => !claimed.Contains(w.Handle) && (w.AppId ?? w.ExePath) is not null)
            .GroupBy(w => w.Key)
            .Select(g => (Key: g.Key, Windows: g.ToList()))
            .ToList();

        foreach (var tile in RunningTiles.Children.OfType<DockAppTile>().ToList())
        {
            int index = others.FindIndex(o => o.Key == (string)tile.Tag);
            if (index < 0)
            {
                RunningTiles.Children.Remove(tile);   // that app closed its last window
                continue;
            }
            tile.SetWindows(others[index].Windows);
            others.RemoveAt(index);
        }
        foreach (var (key, list) in others)
        {
            var tile = NewTile(AppFor(list[0]));
            tile.Tag = key;
            tile.SetWindows(list);
            RunningTiles.Children.Add(tile);
        }

        bool anyPinned = AppTiles.Children.Count > 0, anyRunning = RunningTiles.Children.Count > 0;
        RunningDivider.Visibility = anyPinned && anyRunning ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = anyPinned || anyRunning ? Visibility.Visible : Visibility.Collapsed;
        if (AppTiles.Children.Count + RunningTiles.Children.Count != tilesBefore)
        {
            // Resize once WPF has laid out the new set of tiles (Loaded priority runs right after layout).
            Dispatcher.InvokeAsync(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
            if (_hovering && _magnifier is not null)
            {
                // An app opened or closed under the mouse: re-copy the tiles once the new layout is done.
                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var element in _hiddenWhileMagnified) element.Opacity = 1;
                    LoadMagnifier();
                    _magnifier?.PlaceOver(WindowStyles.RectOf(new System.Windows.Interop.WindowInteropHelper(this).Handle), Scale);
                    _framesShown = 0;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    // A Start menu entry for a running window if there is one (better name and icon), else its exe.
    private AppCatalog.App AppFor(WindowTracker.AppWindow window) =>
        _catalog.FirstOrDefault(a => window.AppId is not null && a.ParsingName.Equals(window.AppId, StringComparison.OrdinalIgnoreCase))
        ?? _catalog.FirstOrDefault(a => a.ExePath is not null && a.ExePath.Equals(window.ExePath, StringComparison.OrdinalIgnoreCase))
        ?? (window.ExePath is null
            ? new AppCatalog.App(window.Name, window.AppId!, IsFile: false)
            : new AppCatalog.App(window.Name, window.ExePath, IsFile: true, window.ExePath));

    // Not running: launch. Running: bring its front-most window up (restoring it if minimized). Already in front
    // with several windows: bring up its back-most one, so repeated clicks walk through all of them.
    private void OnTileClicked(DockAppTile tile)
    {
        _listOpenTimer.Stop();
        CloseWindowList();
        _bounceIndex = _items.FindIndex(i => i.Name == tile.App.Name && !i.IsTrash);
        _bounceStart = DateTime.Now;
        StartLoop();

        _windows.Refresh();   // act on the current windows, not the 250 ms old list
        var windows = tile.Windows;
        if (windows.Count == 0)
        {
            AppCatalog.Launch(tile.App);
            return;
        }
        bool inFront = windows.Any(w => w.Handle == WindowTracker.Foreground);
        WindowTracker.Activate(inFront && windows.Count > 1 ? windows[^1].Handle : windows[0].Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopLoop();
        _hideTimer?.Stop();
        _slideTimer?.Stop();
        _trigger?.Close();
        _windows.Dispose();
        _menu?.SafeClose();
        _numberKeys?.Dispose();
        _listOpenTimer.Stop();
        _listCloseTimer.Stop();
        CloseWindowList();
        base.OnClosed(e);
    }

    private void ApplySizes()
    {
        double tile = _config.Dock.TileSize;
        Panel.Height = PanelHeight;
        Panel.Padding = new Thickness(PaddingSide, PaddingTop, PaddingSide, PaddingBottom);
        Panel.Margin = new Thickness(Flare, 0, Flare, 0);   // the flares sit outside the body
        TrashTile.Width = TrashTile.Height = tile;
        TrashTile.CornerRadius = new CornerRadius(tile * 0.23);   // canvas: 12 px corners on a 52 px tile
        TrashIcon.Width = TrashIcon.Height = tile * 24 / 52;      // canvas: 24 px glyph in a 52 px tile
        TrashDot.Margin = new Thickness(0, DotRow - 3, 0, 0);
        Divider.Height = RunningDivider.Height = tile;
        Divider.Margin = RunningDivider.Margin = new Thickness(0, 0, TileGap, 0);
    }

    // The black body with rounded top corners, and concave flares where it meets the screen edge (same curves as
    // the notch, turned on its side). Coordinates in WPF units; y = height is the screen edge.
    private void UpdateShape()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 2 * (Flare + CornerRadius) || h < Flare + CornerRadius) return;
        Shape.Data = System.Windows.Media.Geometry.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "M0,{1} Q{2},{1} {2},{3} L{2},{4} Q{2},0 {5},0 L{6},0 Q{7},0 {7},{4} L{7},{3} Q{7},{1} {0},{1} Z",
            w, h, Flare, h - Flare, CornerRadius, Flare + CornerRadius, w - Flare - CornerRadius, w - Flare));
    }

    private static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
