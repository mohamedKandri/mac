using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The black "island" centered in the menu bar, like the iPhone's Dynamic Island / a MacBook notch.
// Collapsed: a bar-high pill showing the time. Hover (or click) and it grows down into a card; it shrinks back
// 300 ms after the mouse leaves. A click keeps it open until the next click.
//
// One layered window (AllowsTransparency) sized for the OPEN card; only the black shape is drawn, and fully
// transparent pixels let clicks through to the bar below. It is owned by the menu bar window, so it always stays
// above the bar. Black with 10 px concave flares where it meets the top edge, like the notch and the dock.
internal sealed class IslandWindow : Window
{
    private const double Flare = 10, CollapsedWidth = 168, OpenWidth = 340, OpenHeight = 128, Radius = 22;
    private static readonly Duration Motion = TimeSpan.FromMilliseconds(220);
    private static readonly SolidColorBrush Muted = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA2));

    private readonly double _collapsedHeight;   // = the menu bar height, so the pill sits inside the bar
    private readonly IntPtr _owner;
    private readonly Border _body;
    private readonly FrameworkElement _collapsed, _open;
    private readonly TextBlock _smallTime, _bigTime, _date;
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isOpen, _pinned, _hiddenForFullscreen;

    public string DeviceName { get; }

    public IslandWindow(double barHeight, string deviceName, IntPtr owner)
    {
        DeviceName = deviceName;
        _collapsedHeight = barHeight;
        _owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);
        Width = OpenWidth + 2 * Flare;
        Height = OpenHeight;

        // Collapsed content: just the time, centered in the pill.
        _smallTime = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _collapsed = _smallTime;

        // Open content: big time, the date, and what's playing (filled in by the media step).
        _bigTime = new TextBlock { FontSize = 34, FontWeight = FontWeights.SemiBold };
        _date = new TextBlock { FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
        var nothing = new TextBlock { Text = "Nothing playing", FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 10, 0, 0) };
        _open = new StackPanel { Margin = new Thickness(22, 14, 22, 14), Opacity = 0, Children = { _bigTime, _date, nothing } };

        _body = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Width = CollapsedWidth,
            Height = _collapsedHeight,
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new Grid { Children = { _collapsed, _open } },
        };
        // Concave flares joining the body to the top edge (the notch's curve, turned sideways and shrunk).
        var leftFlare = new Path { Data = Geometry.Parse($"M0,0 L{Flare},0 L{Flare},{Flare} Q{Flare},0 0,0 Z"), Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Top };
        var rightFlare = new Path { Data = Geometry.Parse($"M{Flare},0 L0,0 L0,{Flare} Q0,0 {Flare},0 Z"), Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Top };
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { leftFlare, _body, rightFlare },
        };

        _body.MouseEnter += (_, _) =>
        {
            _closeTimer.Stop();
            Expand(pin: false);
        };
        _body.MouseLeave += (_, _) =>
        {
            if (!_pinned) _closeTimer.Start();
        };
        _body.MouseLeftButtonUp += (_, _) =>
        {
            _pinned = !_pinned;
            if (_pinned) Expand(pin: true);
            else if (!_body.IsMouseOver) Collapse();
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Collapse();
        };

        // The clock ticks once a minute, right after the minute changes (no polling in between).
        _clock.Tick += (_, _) => UpdateTime();
        UpdateTime();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowStyles.MakeToolWindow(hwnd, noActivate: true);
        };
        new WindowInteropHelper(this).Owner = owner;   // stays above the menu bar
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    // Top center of the monitor, over the menu bar (physical pixels).
    public void Place()
    {
        if (Monitors.Find(DeviceName) is not Monitors.Info monitor) return;
        double scale = monitor.Dpi / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        var m = monitor.Bounds;
        if (!IsVisible && !_hiddenForFullscreen) Show();
        WindowStyles.PlaceOnTop(new WindowInteropHelper(this).Handle, new Int32Rect(m.Left + (m.Right - m.Left - w) / 2, m.Top, w, h));
    }

    // Opens the card. `pin` keeps it open until the next click (also used by `--open island`).
    public void Expand(bool pin)
    {
        if (pin) _pinned = true;
        if (_isOpen) return;
        _isOpen = true;
        Animate(OpenWidth, OpenHeight, Radius, openOpacity: 1);
    }

    private void Collapse()
    {
        if (!_isOpen || _pinned) return;
        _isOpen = false;
        Animate(CollapsedWidth, _collapsedHeight, 10, openOpacity: 0);
    }

    // Grows or shrinks the pill with an ease-out, cross-fading the small time and the card.
    private void Animate(double width, double height, double radius, double openOpacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _body.BeginAnimation(WidthProperty, new DoubleAnimation(width, Motion) { EasingFunction = ease });
        _body.BeginAnimation(HeightProperty, new DoubleAnimation(height, Motion) { EasingFunction = ease });
        _body.CornerRadius = new CornerRadius(0, 0, radius, radius);
        _open.BeginAnimation(OpacityProperty, new DoubleAnimation(openOpacity, Motion));
        _collapsed.BeginAnimation(OpacityProperty, new DoubleAnimation(1 - openOpacity, Motion));
    }

    private void UpdateTime()
    {
        var now = DateTime.Now;
        _smallTime.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        _bigTime.Text = _smallTime.Text;
        string date = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        _date.Text = date.Length > 0 ? char.ToUpper(date[0], CultureInfo.CurrentCulture) + date[1..] : date;
        _clock.Stop();
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(20);
        _clock.Start();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hidden;
        if (hidden)
        {
            _pinned = false;
            _isOpen = true;   // force the collapse below to run
            Collapse();
            Hide();
        }
        else Place();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        _closeTimer.Stop();
        base.OnClosed(e);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
