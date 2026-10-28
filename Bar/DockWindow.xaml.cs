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
    private const double PaddingTop = 6, PaddingSide = 8, PaddingBottom = 3, DotRow = 2 + 3;
    private const double Flare = 16, CornerRadius = 18;

    // dock.style (fixed for this window's life: BarManager builds a new dock when it changes, because the blur
    // and the corner style are set when the native window is created).
    public string DockStyle { get; }
    private bool IsGlass => DockStyle == "glass";

    // Glass floats 6 px above the edge (like macOS); the black shape sits on the edge with its flares.
    private double EdgeGap => IsGlass ? 6 : 0;
    private double FlareInset => IsGlass ? 0 : Flare;

    private const double TileGap = 6;

    private readonly Action<string, string> _notify;
    private readonly AppCommands _commands;
    private readonly WindowTracker _windows = new();
    private readonly ForegroundWatcher _foreground = new();   // which app is in front, for the tiles' pill
    private bool _binFull;
    private List<AppCatalog.App> _catalog = [];
    private int _iconPixels;
    private AppConfig _config;
    private string _tilesKey = "";

    public DockWindow(AppConfig config, ThemeManager theme, Action<string, string> notify, AppCommands commands, string deviceName)
        : base(theme, deviceName)
    {
        InitializeComponent();
        DockStyle = config.Dock.Style;
        if (IsGlass)
        {
            // Frosted: the blur shows through a transparent fill; dots, dividers and Trash follow the theme.
            Panel.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, ThemeKeys.DockGlassFill);
            Shape.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Black: always white on black, whatever the Windows theme (like the notch). These override the
            // theme's colors for everything inside the dock (running dots, dividers, Trash tile).
            Resources[ThemeKeys.Foreground] = Frozen.Brush(System.Windows.Media.Colors.White);
            Resources[ThemeKeys.Hairline] = Frozen.Brush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            Resources[ThemeKeys.Pressed] = Frozen.Brush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Root.SizeChanged += (_, _) => UpdateShape();
        }
        _config = config;
        _notify = notify;
        _commands = commands;
        SetUpNumberKeys();
        SetUpWindowList();
        ApplySizes();
        BuildTiles();
        _windows.Changed += UpdateRunning;
        _foreground.Changed += _ => UpdateFront();
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

    protected override AppBarEdge Edge => AppBarEdge.Bottom;
    protected override bool RoundedCorners => IsGlass;
    protected override bool Glass => IsGlass;
    // A thin light rim around the glass, like macOS's dock (Windows' default border is a dark gray).
    protected override System.Windows.Media.Color? BorderColor => IsGlass ? System.Windows.Media.Color.FromRgb(0xC9, 0xCE, 0xD8) : null;
    protected override string TintColor => IsGlass ? Theme.Palette.DockGlass : Theme.Palette.Dock;

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
        WatchHover(tile);
        return tile;
    }

    // Marks the tile whose app owns the foreground window (its pill), and only that one.
    private void UpdateFront()
    {
        IntPtr front = ForegroundWatcher.Current;
        foreach (var tile in AppTiles.Children.OfType<DockAppTile>().Concat(RunningTiles.Children.OfType<DockAppTile>()))
            tile.SetFront(tile.Windows.Any(w => w.Handle == front));
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
        UpdateFront();   // new tiles start without a pill
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
        _foreground.Dispose();
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
        Panel.Margin = new Thickness(FlareInset, 0, FlareInset, 0);   // black style: the flares sit outside the body
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
}
