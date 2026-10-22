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
// Collapsed: a bar-high pill showing the time, or what's playing (Native/MediaSessions). Hover (or click) and it
// grows down into a card: the clock, or the track with previous / play-pause / next. It shrinks back 300 ms after
// the mouse leaves. A click keeps it open until the next click.
//
// One layered window (AllowsTransparency) sized for the OPEN card; only the black shape is drawn, and fully
// transparent pixels let clicks through to the bar below. It is owned by the menu bar window, so it always stays
// above the bar. Black with 10 px concave flares where it meets the top edge, like the notch and the dock.
internal sealed class IslandWindow : OverlayWindow
{
    private const double Flare = 10, CollapsedWidth = 168, OpenWidth = 340, OpenHeight = 128, Radius = 22;
    private static readonly Duration Motion = TimeSpan.FromMilliseconds(220);
    private static readonly SolidColorBrush Muted = Frozen.Brush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly SolidColorBrush Playing = Frozen.Brush(Color.FromRgb(0x34, 0xC7, 0x59));
    // Solid media glyphs on a 24 px grid.
    private static readonly Geometry PlayIcon = Frozen.Geometry("M7,4 L20,12 L7,20 Z");
    private static readonly Geometry PauseIcon = Frozen.Geometry("M6,4 H10 V20 H6 Z M14,4 H18 V20 H14 Z");
    private static readonly Geometry PreviousIcon = Frozen.Geometry("M5,5 H7.5 V19 H5 Z M19,5 L9,12 L19,19 Z");
    private static readonly Geometry NextIcon = Frozen.Geometry("M16.5,5 H19 V19 H16.5 Z M5,5 L15,12 L5,19 Z");

    private readonly double _collapsedHeight;   // = the menu bar height, so the pill sits inside the bar
    private readonly Border _body;
    private readonly FrameworkElement _collapsed, _open;
    private readonly TextBlock _smallTime, _bigTime, _date;
    private readonly MediaSessions _media = new();
    private readonly FrameworkElement _pillNowPlaying, _clockCard, _mediaCard;
    private readonly TextBlock _pillTitle, _title, _artist, _app;
    private readonly Path _playPauseIcon;
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isOpen, _pinned, _hiddenForFullscreen;

    public string DeviceName { get; }

    public IslandWindow(double barHeight, string deviceName, IntPtr owner) : base(clickThrough: false)
    {
        DeviceName = deviceName;
        _collapsedHeight = barHeight;
        Width = OpenWidth + 2 * Flare;
        Height = OpenHeight;

        // Collapsed content: the time, or (while something plays) three green bars + the title.
        _smallTime = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _pillTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium, MaxWidth = 124, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _pillNowPlaying = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children = { Bars(), _pillTitle },
        };
        _collapsed = new Grid { Children = { _smallTime, _pillNowPlaying } };

        // Open content, nothing playing: big time, the date.
        _bigTime = new TextBlock { FontSize = 34, FontWeight = FontWeights.SemiBold };
        _date = new TextBlock { FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
        var nothing = new TextBlock { Text = "Nothing playing", FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 10, 0, 0) };
        _clockCard = new StackPanel { Children = { _bigTime, _date, nothing } };

        // Open content, playing: title, artist, app, then previous / play-pause / next.
        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _app = new TextBlock { FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
        _playPauseIcon = new Path { Fill = Brushes.White, Data = PlayIcon };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                ControlButton(new Path { Fill = Brushes.White, Data = PreviousIcon }, 18, "Previous", () => _media.Previous()),
                ControlButton(_playPauseIcon, 22, "Play or pause", () => _media.TogglePlayPause()),
                ControlButton(new Path { Fill = Brushes.White, Data = NextIcon }, 18, "Next", () => _media.Next()),
            },
        };
        _mediaCard = new StackPanel { Visibility = Visibility.Collapsed, Children = { _title, _artist, _app, controls } };

        _open = new Grid { Margin = new Thickness(22, 14, 22, 12), Opacity = 0, Children = { _clockCard, _mediaCard } };

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
        _media.Changed += UpdateMedia;

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

    // Shows the playing track (or the clock again when nothing is playing).
    private void UpdateMedia()
    {
        var m = _media.Current;
        bool active = m.Active && !string.IsNullOrEmpty(m.Title);
        _smallTime.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _pillNowPlaying.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _clockCard.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _mediaCard.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;
        _pillTitle.Text = m.Title;
        _pillNowPlaying.Opacity = m.Playing ? 1 : 0.6;   // paused: dimmed
        _title.Text = m.Title;
        _artist.Text = m.Artist.Length > 0 ? m.Artist : " ";
        _app.Text = m.App;
        _playPauseIcon.Data = m.Playing ? PauseIcon : PlayIcon;
    }

    // Three short green bars, like a frozen equalizer. Not animated on purpose: an animation would redraw this
    // layered window every frame (tens of ms of CPU each), all the time music plays.
    private static FrameworkElement Bars()
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        foreach (double h in new[] { 6.0, 10, 7 })
            bars.Children.Add(new Border { Width = 2.5, Height = h, CornerRadius = new CornerRadius(1.25), Background = Playing, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
        return bars;
    }

    // A round, borderless media button (44 px target) holding a solid glyph scaled to `size`.
    private static Button ControlButton(Path icon, double size, string name, Action click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("Shell.DockTile"),
            Width = 44, Height = 32,
            Content = new Border
            {
                Background = Brushes.Transparent,   // the whole button area is clickable, not just the glyph
                Child = new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { icon } } },
            },
            ToolTip = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
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
        _media.Dispose();
        base.OnClosed(e);
    }
}
