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
            Backdrop.Prepare(this, RoundedCorners);
            ApplyBackdrop();
            _appBar = new AppBar(this, DeviceName, Edge, PlaceInStrip);
            _appBar.FullscreenNotified += opening => FullscreenNotified?.Invoke(opening);
            _appBar.Dock(ReservedThickness);
        };
        Theme.Changed += ApplyBackdrop;
    }

    protected abstract AppBarEdge Edge { get; }

    // Height of the reserved strip, in WPF units.
    protected abstract double ReservedThickness { get; }

    protected abstract bool RoundedCorners { get; }

    // The glass tint (a palette color, e.g. Theme.Palette.Background).
    protected abstract string TintColor { get; }

    // Where the window goes inside the reserved strip (physical pixels). Null = fill the strip.
    protected virtual StripPlacement? PlaceInStrip => null;

    // False if Windows refused to reserve the space (see AppBar.Dock).
    public bool IsDocked => _appBar?.IsRegistered ?? false;

    // Gives the reserved screen space back. Thread-safe; used by the crash handler too.
    public void ReleaseAppBar() => _appBar?.Remove();

    // Call when ReservedThickness changed (config reload).
    protected void Redock() => _appBar?.Dock(ReservedThickness);

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
        else Show();   // ShowActivated=False: reappearing doesn't steal focus
    }

    // For subclasses with extra windows of their own (the dock's magnifier) that must hide too.
    protected virtual void OnHiddenForFullscreen(bool hidden) { }

    private void ApplyBackdrop() =>
        Backdrop.Apply(this, ThemeManager.ParseColor(TintColor), Theme.Blur, Theme.IsDark);

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= ApplyBackdrop;
        _appBar?.Dispose();
        base.OnClosed(e);
    }
}
