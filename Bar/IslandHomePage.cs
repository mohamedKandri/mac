using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Island page "Home": three tiles side by side (Music, Focus timer, Weather). Each tile glows from the bottom in
// its own color while something is happening there: the album's color while music plays, purple while the focus
// timer runs, the sky's color for the weather. When the timer ends its tile turns green ("Done") and fades back.
//
// CPU rule (the island is a layered window: every visual change redraws it, ~40 ms in software rendering):
// the glow is a static gradient that changes only when the state changes, with one short fade (300 ms) at that
// moment. Nothing pulses or loops. The timer's seconds tick only while this page shows; the closed pill shows
// whole minutes, so it redraws once a minute.
internal sealed class IslandHomePage : IslandPage
{
    private const double TileWidth = 92, TileHeight = 128, Gap = 10, Corner = 16;
    private static readonly Color Base = Color.FromRgb(0x1C, 0x1C, 0x1F);
    private static readonly Color FocusGlow = Color.FromRgb(0x8E, 0x5B, 0xFF);
    private static readonly Color DoneGlow = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color SunGlow = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color RainGlow = Color.FromRgb(0x2F, 0x7B, 0xFF);
    private static readonly Color GreyGlow = Color.FromRgb(0x8A, 0x93, 0xA6);
    private static readonly Color NoArtGlow = Color.FromRgb(0xFF, 0x6B, 0x8B);   // the island's placeholder pink
    private static readonly SolidColorBrush Green = Frozen.Brush(DoneGlow);
    private static readonly Geometry PlayIcon = Frozen.Geometry("M7,4 L20,12 L7,20 Z");
    private static readonly Geometry PauseIcon = Frozen.Geometry("M6,4 H10 V20 H6 Z M14,4 H18 V20 H14 Z");
    private static readonly Geometry TimerIcon = Frozen.Geometry("M12,8 V12 L14.5,14.5 M5,13 A7,7 0 1 0 19,13 A7,7 0 1 0 5,13 M10,2 H14");
    private static readonly TimeSpan DefaultFocus = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan DoneShownFor = TimeSpan.FromSeconds(8);

    private readonly Grid _root = new() { Width = ContentWidth };
    private readonly IslandWeatherPage _weather;
    private readonly Action _togglePlayPause;

    // One tile = a base border + a glow layer (its Opacity is what fades) + the content.
    private sealed record GlowTile(Border Glow, Border Frame);
    private readonly GlowTile _music, _focus, _sky;

    private readonly Border _musicArt = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _musicTitle, _musicArtist, _musicIdle;
    private readonly Path _musicPlayIcon = new() { Fill = Brushes.White, Data = PlayIcon };
    private readonly FrameworkElement _musicPlaying;
    private string _artKey = "";
    private Color _musicColor = GreyGlow;

    private readonly TextBlock _focusTime, _focusStatus;
    private readonly Ellipse _focusRing = new() { Width = 22, Height = 22, StrokeThickness = 2.5, Stroke = Brushes.White, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90) };
    private readonly DispatcherTimer _focusTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _doneTimer = new();
    private DateTime _focusEndsAt;
    private TimeSpan _focusLength = DefaultFocus;
    private bool _focusRunning, _focusDone, _shown;

    private readonly TextBlock _skyTemp, _skyCity, _skyText;
    private readonly ContentControl _skyIcon = new() { HorizontalAlignment = HorizontalAlignment.Right };

    public IslandHomePage(IslandWeatherPage weather, Action togglePlayPause)
    {
        _weather = weather;
        _togglePlayPause = togglePlayPause;

        // Music: artwork + play/pause on top, title and artist at the bottom ("Nothing playing" when idle).
        _musicTitle = Text("", 13, weight: FontWeights.SemiBold);
        _musicArtist = Text("", 11, Muted);
        _musicIdle = Text("Nothing playing", 11, Muted);
        _musicIdle.TextWrapping = TextWrapping.Wrap;
        var playButton = new Button
        {
            Style = (Style)Application.Current.FindResource("Shell.DockTile"),
            Width = 26, Height = 26,
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Frozen.Brush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Child = new Viewbox { Width = 11, Height = 11, Child = new Canvas { Width = 24, Height = 24, Children = { _musicPlayIcon } } },
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(playButton, "Play or pause");
        playButton.Click += (_, _) => _togglePlayPause();
        _musicPlaying = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { _musicTitle, _musicArtist },
            Visibility = Visibility.Collapsed,
        };
        _musicIdle.VerticalAlignment = VerticalAlignment.Bottom;
        var musicTop = new Grid { VerticalAlignment = VerticalAlignment.Top, Children = { _musicArt, playButton } };
        _musicArt.HorizontalAlignment = HorizontalAlignment.Left;
        _music = MakeTile("Music", new Grid { Children = { musicTop, _musicPlaying, _musicIdle } });

        // Focus: a 25-minute timer. Click = start / stop; the ring empties as time passes.
        _focusTime = Text("25:00", 22, weight: FontWeights.SemiBold);
        _focusStatus = Text("Tap to start", 11, Muted);
        _focusRing.HorizontalAlignment = HorizontalAlignment.Right;
        _focusRing.VerticalAlignment = VerticalAlignment.Top;
        var focusText = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _focusTime, _focusStatus } };
        var timerIcon = Icon(TimerIcon, 20);
        timerIcon.HorizontalAlignment = HorizontalAlignment.Left;
        timerIcon.VerticalAlignment = VerticalAlignment.Top;
        _focus = MakeTile("Focus", new Grid { Children = { timerIcon, _focusRing, focusText } });
        _focus.Frame.Cursor = System.Windows.Input.Cursors.Hand;
        _focus.Frame.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // not the island's own click (that would open the lyrics)
            ToggleFocus();
        };
        _focusTick.Tick += (_, _) => FocusTick();
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer.Stop();
            _focusDone = false;
            ShowFocus();
            RaisePillChanged();
        };

        // Weather: big temperature, icon, city and conditions, from the weather page's forecast.
        _skyTemp = Text("—", 22, weight: FontWeights.SemiBold);
        _skyCity = Text("", 11, weight: FontWeights.Medium);
        _skyText = Text("", 11, Muted);
        _skyIcon.VerticalAlignment = VerticalAlignment.Top;
        var skyText = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _skyTemp, _skyCity, _skyText } };
        _sky = MakeTile("Weather", new Grid { Children = { _skyIcon, skyText } });
        _weather.PillChanged += ShowWeather;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _music.Frame, _focus.Frame, _sky.Frame } };
        _focus.Frame.Margin = new Thickness(Gap, 0, Gap, 0);
        _root.Children.Add(row);
        View = _root;

        ShowFocus();
        ShowWeather();
        SetMedia(MediaSessions.State.Nothing, null);
    }

    public override FrameworkElement View { get; }
    public override double Height => TileHeight + 8;

    public override void Shown()
    {
        _shown = true;
        ShowWeather();
        if (_focusRunning) FocusTick();
        SyncTick();
    }

    public override void Hidden()
    {
        _shown = false;
        SyncTick();
    }

    // Closed pill: the timer while it runs (whole minutes), or "Focus done" in green for a few seconds.
    public override PillInfo? Pill =>
        _focusDone ? new PillInfo(TimerIcon, Green, "Focus done")
        : _focusRunning ? new PillInfo(TimerIcon, Frozen.Brush(FocusGlow), $"{Math.Max(1, (int)Math.Ceiling((_focusEndsAt - DateTime.UtcNow).TotalMinutes))} min left")
        : null;

    // Called by the island whenever the media session changes (track, play state, artwork).
    public void SetMedia(MediaSessions.State m, ImageSource? art)
    {
        bool active = m.Active && !string.IsNullOrEmpty(m.Title);
        _musicPlaying.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _musicIdle.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _musicTitle.Text = m.Title;
        _musicArtist.Text = m.Artist;
        _musicPlayIcon.Data = m.Playing ? PauseIcon : PlayIcon;
        string key = $"{m.App}|{m.Artist}|{m.Title}|{art?.GetHashCode()}";
        if (key != _artKey)
        {
            _artKey = key;
            _musicArt.Background = art is null
                ? new LinearGradientBrush(NoArtGlow, Color.FromRgb(0x7C, 0x6B, 0xFF), 45)
                : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            _musicColor = art is BitmapSource b ? Dominant(b) : NoArtGlow;   // once per track
        }
        SetGlow(_music, active && m.Playing ? _musicColor : (Color?)null);
    }

    // ---------------- Focus timer ----------------

    // For the `--open island-focus` developer switch: starts a 5-second timer (running, then "Done").
    internal void StartFocusForTest()
    {
        _focusLength = TimeSpan.FromSeconds(5);
        ToggleFocus();
    }

    private void ToggleFocus()
    {
        _focusDone = false;
        _doneTimer.Stop();
        _focusRunning = !_focusRunning;
        if (!_focusRunning) _focusLength = DefaultFocus;
        if (_focusRunning) _focusEndsAt = DateTime.UtcNow + _focusLength;
        ShowFocus();
        SyncTick();
        RaisePillChanged();
    }

    // Seconds while the page shows; once a minute (for the pill's "N min left") while it doesn't.
    private void SyncTick()
    {
        _focusTick.Interval = _shown ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        if (_focusRunning && !_focusTick.IsEnabled) _focusTick.Start();
        else if (!_focusRunning) _focusTick.Stop();
        if (_focusRunning && !_shown)
        {
            // Re-arm at the next whole minute left, so the pill changes when the minute does.
            var left = _focusEndsAt - DateTime.UtcNow;
            _focusTick.Stop();
            _focusTick.Interval = TimeSpan.FromMilliseconds(Math.Max(200, left.TotalMilliseconds % 60000 + 20));
            _focusTick.Start();
        }
    }

    private void FocusTick()
    {
        if (!_focusRunning) return;
        if (DateTime.UtcNow >= _focusEndsAt)
        {
            _focusRunning = false;
            _focusDone = true;
            _focusLength = DefaultFocus;
            _focusTick.Stop();
            _doneTimer.Interval = DoneShownFor;
            _doneTimer.Start();
            ShowFocus();
            RaisePillChanged();
            return;
        }
        if (_shown) ShowFocus();
        else
        {
            RaisePillChanged();
            SyncTick();
        }
    }

    private void ShowFocus()
    {
        var left = _focusRunning ? _focusEndsAt - DateTime.UtcNow : _focusLength;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        int seconds = (int)Math.Ceiling(left.TotalSeconds);
        _focusTime.Text = _focusDone ? "Done" : $"{seconds / 60:00}:{seconds % 60:00}";
        _focusStatus.Text = _focusDone ? "Take a break" : _focusRunning ? $"{(int)Math.Ceiling(left.TotalMinutes)} min left" : "Tap to start";
        // Ring: the part still to go, as a dash in stroke-width units (like the notch rings).
        double circumference = Math.PI * (_focusRing.Width - _focusRing.StrokeThickness) / _focusRing.StrokeThickness;
        double fraction = _focusRunning ? left.TotalSeconds / _focusLength.TotalSeconds : _focusDone ? 0 : 1;
        _focusRing.StrokeDashArray = new DoubleCollection { circumference * fraction, 1000 };
        _focusRing.Opacity = _focusRunning ? 1 : 0.35;
        SetGlow(_focus, _focusDone ? DoneGlow : _focusRunning ? FocusGlow : null);
    }

    // ---------------- Weather ----------------

    private void ShowWeather()
    {
        if (_weather.Latest is not WeatherService.Forecast f)
        {
            _skyText.Text = "No forecast yet";
            SetGlow(_sky, null);
            return;
        }
        _skyTemp.Text = $"{f.Temperature}°";
        _skyCity.Text = f.City;
        _skyText.Text = WeatherService.Describe(f.Code).Text;
        _skyIcon.Content = Icon(IslandWeatherPage.IconFor(f.Code), 22, IslandWeatherPage.ColorFor(f.Code));
        SetGlow(_sky, WeatherService.Describe(f.Code).Icon switch
        {
            "sun" or "partly" => SunGlow,
            "rain" or "storm" => RainGlow,
            _ => GreyGlow,
        });
    }

    // ---------------- Tiles and glow ----------------

    private static GlowTile MakeTile(string label, FrameworkElement content)
    {
        var glow = new Border { CornerRadius = new CornerRadius(Corner), Opacity = 0 };
        var body = new DockPanel { Margin = new Thickness(10, 9, 10, 10) };
        var title = Text(label, 11, Muted, FontWeights.Medium);
        title.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(title, Dock.Top);
        body.Children.Add(title);
        body.Children.Add(content);
        var frame = new Border
        {
            Width = TileWidth, Height = TileHeight,
            CornerRadius = new CornerRadius(Corner),
            Background = Frozen.Brush(Base),
            BorderThickness = new Thickness(1),
            BorderBrush = Frozen.Brush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true,
            Child = new Grid { Children = { glow, body } },
        };
        return new GlowTile(glow, frame);
    }

    // Lights a tile with `color` (a gradient rising from the bottom and a tinted rim), or turns it off (null).
    // Fades once over 300 ms; does nothing if the tile already shows that state.
    private static void SetGlow(GlowTile tile, Color? color)
    {
        if (color is Color c)
        {
            if (tile.Glow.Tag is Color current && current == c && tile.Glow.Opacity > 0) return;
            tile.Glow.Tag = c;
            var gradient = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 0.0));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, c.R, c.G, c.B), 0.55));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xD9, c.R, c.G, c.B), 1.0));
            gradient.Freeze();
            tile.Glow.Background = gradient;
            tile.Glow.BorderThickness = new Thickness(1);
            tile.Glow.BorderBrush = Frozen.Brush(Color.FromArgb(0x80, c.R, c.G, c.B));
            Fade(tile.Glow, 1);
        }
        else
        {
            if (tile.Glow.Tag is null) return;
            tile.Glow.Tag = null;
            Fade(tile.Glow, 0);
        }
    }

    private static void Fade(UIElement e, double to) =>
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    // The artwork's main color: the average of its saturated pixels (grey and near-black/white ones skipped),
    // then brightened so it reads as a glow on black. A 16×16 copy is enough, and it runs once per track.
    private static Color Dominant(BitmapSource art)
    {
        try
        {
            var small = new FormatConvertedBitmap(new TransformedBitmap(art, new ScaleTransform(16.0 / art.PixelWidth, 16.0 / art.PixelHeight)), PixelFormats.Bgra32, null, 0);
            var px = new byte[small.PixelWidth * small.PixelHeight * 4];
            small.CopyPixels(px, small.PixelWidth * 4, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                int max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])), min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                double sat = max == 0 ? 0 : (max - min) / (double)max;
                if (max < 40 || sat < 0.2) continue;
                double w = sat * max;
                b += px[i] * w; g += px[i + 1] * w; r += px[i + 2] * w; weight += w;
            }
            if (weight == 0) return GreyGlow;
            r /= weight; g /= weight; b /= weight;
            double scale = 255 / Math.Max(r, Math.Max(g, b));   // full brightness, same hue
            return Color.FromRgb((byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
        }
        catch (Exception)
        {
            return GreyGlow;
        }
    }

    public override void Dispose()
    {
        _focusTick.Stop();
        _doneTimer.Stop();
        _weather.PillChanged -= ShowWeather;
    }
}
