using System.Windows;
using System.Windows.Media;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Base for the windows that dock to a screen edge: the menu bar and the dock.
// Gives them: frosted glass that follows the theme, an AppBar that reserves their screen space,
// hiding under fullscreen apps, and releasing the space on exit or crash.
// A subclass says which edge, how much space, its tint and (optionally) where the window sits in the strip.
public abstract class ShellWindow : Window
{
    private bool _hiddenForFullscreen;
    private AppBar? _appBar;

    protected ThemeManager Theme { get; }

    // The monitor this window belongs to ("\\.\DISPLAY1").
    public string DeviceName { get; }

    // Explorer said a fullscreen app opened (true) or closed (false); BarManager decides which windows hide.
    public event Action<bool>? FullscreenNotified;

    protected ShellWindow(ThemeManager theme, string deviceName)
    {
        Theme = theme;
        DeviceName = deviceName;

        // No title bar or border; never in the taskbar; appearing doesn't steal focus.
        // UseLayoutRounding snaps edges to whole pixels, so 1 px hairlines stay crisp at 125% / 150%.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, ThemeKeys.Foreground);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);
        SetResourceReference(FontSizeProperty, ThemeKeys.FontSize);

        // SourceInitialized = the native window exists but isn't visible yet: set up glass and dock before it
        // appears, so it never flashes in the wrong place or as a black box.
        SourceInitialized += (_, _) =>
        {
            Backdrop.Prepare(this, RoundedCorners, systemBorder: Glass);
            ApplyBackdrop();
            _appBar = new AppBar(this, DeviceName, Edge, PlaceInStrip);
            _appBar.FullscreenNotified += opening => FullscreenNotified?.Invoke(opening);
            _appBar.Dock(ReservedThickness, ReservesSpace);
        };
        Theme.Changed += ApplyBackdrop;
    }

    protected abstract AppBarEdge Edge { get; }

    // Height of the reserved strip, in WPF units.
    protected abstract double ReservedThickness { get; }

    protected abstract bool RoundedCorners { get; }

    // False = no blur and no system border: the window stays see-through (DWM honors WPF's alpha) and draws its
    // own opaque shape, like the black dock with its edge flares.
    protected virtual bool Glass => true;

    // The glass tint (a palette color, e.g. Theme.Palette.Background).
    protected abstract string TintColor { get; }

    // Where the window goes inside the reserved strip (physical pixels). Null = fill the strip.
    protected virtual StripPlacement? PlaceInStrip => null;

    // False = sit on the edge without taking the space (the auto-hiding dock): maximized windows use the full height.
    protected virtual bool ReservesSpace => true;

    // False if Windows refused to reserve the space (see AppBar.Dock). Windows that don't reserve count as docked.
    public bool IsDocked => !ReservesSpace || (_appBar?.IsRegistered ?? false);

    // Gives the reserved screen space back. Thread-safe; used by the crash handler too.
    public void ReleaseAppBar() => _appBar?.Remove();

    // Call when ReservedThickness or ReservesSpace changed (config reload).
    protected void Redock() => _appBar?.Dock(ReservedThickness, ReservesSpace);

    // Call when the window's own size changed but the strip didn't (e.g. the dock got more tiles).
    protected void Reposition() => _appBar?.Reposition();

    // Hides the window while a fullscreen app (game, F11 browser, video) is in front on its monitor.
    // The space stays reserved; fullscreen apps ignore the work area anyway.
    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hidden;
        OnHiddenForFullscreen(hidden);
        if (hidden) Hide();
        else ShowAfterFullscreen();
    }

    // For subclasses with extra windows of their own (the dock's magnifier) that must hide too.
    protected virtual void OnHiddenForFullscreen(bool hidden) { }

    // The fullscreen app is gone. ShowActivated=False: reappearing doesn't steal focus. The auto-hiding dock
    // overrides this to stay hidden until the mouse reaches the edge.
    protected virtual void ShowAfterFullscreen() => Show();

    private void ApplyBackdrop() =>
        Backdrop.Apply(this, ThemeManager.ParseColor(TintColor), Glass && Theme.Blur, Theme.IsDark);

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= ApplyBackdrop;
        _appBar?.Dispose();
        base.OnClosed(e);
    }
}
