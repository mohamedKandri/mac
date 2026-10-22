using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MacShell.Native;

namespace MacShell.Bar;

public partial class DockAppTile : UserControl
{
    internal AppCatalog.App App { get; }

    // This app's open windows, front to back (set by DockWindow from the WindowTracker).
    internal IReadOnlyList<WindowTracker.AppWindow> Windows { get; private set; } = [];

    // The dock decides what a click means (launch, focus, next window).
    internal event Action<DockAppTile>? Clicked;
    internal event Action<DockAppTile>? RightClicked;

    // The dock.pinned entry this tile came from (null for "running, not pinned" tiles); Unpin removes it.
    internal string? PinEntry { get; init; }

    internal DockAppTile(AppCatalog.App app, double tileSize, int iconPixels)
    {
        InitializeComponent();
        App = app;
        Icon.Width = Icon.Height = tileSize;
        // Rendered at the magnified size (and screen scale), so it stays sharp when it grows in step 4.
        Icon.Source = AppCatalog.Icon(app, iconPixels);
        // No tooltip: the dock shows the app name above the tile while hovering (DockMagnifier).
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            RightClicked?.Invoke(this);
        };
    }

    // The icon element, for the magnifier: its position, and hidden while the magnified copy is shown.
    internal FrameworkElement IconElement => Icon;

    // True when this tile is one of the app's windows: by app ID, by exe, or the pinned exe itself.
    internal bool Owns(WindowTracker.AppWindow window) =>
        (window.AppId is not null && window.AppId.Equals(App.ParsingName, StringComparison.OrdinalIgnoreCase))
        || (window.ExePath is not null && App.ExePath is not null && window.ExePath.Equals(App.ExePath, StringComparison.OrdinalIgnoreCase));

    private bool _front;

    internal void SetWindows(IReadOnlyList<WindowTracker.AppWindow> windows)
    {
        Windows = windows;
        UpdateDot();
    }

    // True while one of this app's windows is the foreground window.
    internal void SetFront(bool front)
    {
        if (front == _front) return;
        _front = front;
        UpdateDot();
    }

    // Not running: no dot. Running: a dimmed 3 px dot. In front: a 14 px bright pill.
    private void UpdateDot()
    {
        bool running = Windows.Count > 0;
        Dot.Width = running && _front ? 14 : 3;
        Dot.Opacity = !running ? 0 : _front ? 1 : 0.55;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        PlayBounce();
        Clicked?.Invoke(this);
    }

    // A quick squash (to 88%) and back, 140 ms each way: the "I heard you" feedback.
    private void PlayBounce()
    {
        var squash = new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Bounce.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, squash);
        Bounce.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, squash);
    }
}
