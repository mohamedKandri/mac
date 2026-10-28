using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MacShell.Core;
using MacShell.Themes;

namespace MacShell.Bar;

// Island page: the weather for island.weatherCity (Core/WeatherService, Open-Meteo). Big temperature + icon, the
// city, conditions, today's high/low, and the next five hours. Refreshed every 30 min (and when shown, if older),
// so the closed pill can show "22°  Casablanca" when nothing is playing.
internal sealed class IslandWeatherPage : IslandPage
{
    private static readonly SolidColorBrush Sun = Frozen.Brush(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly SolidColorBrush Drop = Frozen.Brush(Color.FromRgb(0x4D, 0xA3, 0xFF));
    private const string Cloud = "M7,18 H17 A4,4 0 0 0 17,10 A5.5,5.5 0 0 0 6.6,11.6 A3.2,3.2 0 0 0 7,18 Z";
    private static readonly Dictionary<string, Geometry> Icons = new()
    {
        ["sun"] = Frozen.Geometry("M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 M12,2 V4 M12,20 V22 M4.9,4.9 L6.3,6.3 M17.7,17.7 L19.1,19.1 M2,12 H4 M20,12 H22 M4.9,19.1 L6.3,17.7 M17.7,6.3 L19.1,4.9"),
        ["partly"] = Frozen.Geometry("M6,8 A3,3 0 1 0 12,8 A3,3 0 1 0 6,8 M9,2 V3.4 M3.4,8 H2 M4.8,3.8 L5.8,4.8 M13.2,3.8 L12.2,4.8 M8,20 H17 A4,4 0 0 0 17,12 A5.5,5.5 0 0 0 7.2,13.6 A3.2,3.2 0 0 0 8,20 Z"),
        ["cloud"] = Frozen.Geometry(Cloud),
        ["fog"] = Frozen.Geometry("M4,9 H20 M4,13 H20 M6,17 H18"),
        ["rain"] = Frozen.Geometry("M7,15 H17 A4,4 0 0 0 17,7 A5.5,5.5 0 0 0 6.6,8.6 A3.2,3.2 0 0 0 7,15 Z M8,18 L7,21 M12,18 L11,21 M16,18 L15,21"),
        ["snow"] = Frozen.Geometry("M7,15 H17 A4,4 0 0 0 17,7 A5.5,5.5 0 0 0 6.6,8.6 A3.2,3.2 0 0 0 7,15 Z M8,19.5 H8.01 M12,20.5 H12.01 M16,19.5 H16.01"),
        ["storm"] = Frozen.Geometry("M7,15 H17 A4,4 0 0 0 17,7 A5.5,5.5 0 0 0 6.6,8.6 A3.2,3.2 0 0 0 7,15 Z M12.5,15 L10.5,18.5 H13.5 L11.5,22"),
    };

    private readonly WeatherService _weather = new();
    private readonly string _city;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly Grid _root = new() { Width = ContentWidth };
    private readonly TextBlock _temp, _place, _conditions, _highLow, _status;
    private readonly ContentControl _bigIcon = new();
    private readonly UniformGrid _hours = new() { Columns = 5, Margin = new Thickness(0, 14, 0, 0) };
    private readonly FrameworkElement _content;
    private bool _loading;

    public IslandWeatherPage(string city)
    {
        _city = city.Trim();
        _temp = Text("", 44, weight: FontWeights.SemiBold);
        _place = Text("", 14, weight: FontWeights.SemiBold);
        _conditions = Text("", 12, Muted);
        _highLow = Text("", 12, Muted);
        _status = Text("", 13, Muted);
        _status.TextWrapping = TextWrapping.Wrap;

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { _place, _conditions, _highLow } };
        foreach (var t in right.Children.OfType<TextBlock>()) t.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(right, Dock.Right);
        _bigIcon.Margin = new Thickness(12, 0, 0, 0);
        _bigIcon.VerticalAlignment = VerticalAlignment.Center;
        var top = new DockPanel { Children = { right, new StackPanel { Orientation = Orientation.Horizontal, Children = { _temp, _bigIcon } } } };
        _content = new StackPanel { Children = { top, _hours }, Visibility = Visibility.Collapsed };
        _root.Children.Add(_content);
        _root.Children.Add(_status);
        View = _root;

        _status.Text = _city.Length == 0 ? "Set island.weatherCity in config.json to see the weather." : "Loading the weather…";
        _refresh.Tick += (_, _) => Refresh();
        if (_city.Length > 0)
        {
            _refresh.Start();
            // First fetch a little after start-up, so the pill has a temperature without slowing the launch.
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            first.Tick += (_, _) =>
            {
                first.Stop();
                Refresh();
            };
            first.Start();
        }
    }

    public override FrameworkElement View { get; }

    public override void Shown() => Refresh();

    // The last forecast (for the Home page's weather tile), with the icon and color used here.
    public WeatherService.Forecast? Latest => _weather.Latest;
    public static Geometry IconFor(int code) => Icons[WeatherService.Describe(code).Icon];
    public static Brush ColorFor(int code) => IconColor(code);   // the service answers from its cache unless it's 30 min old

    public override PillInfo? Pill => _weather.Latest is WeatherService.Forecast f
        ? new PillInfo(Icons[WeatherService.Describe(f.Code).Icon], IconColor(f.Code), $"{f.Temperature}°  {f.City}")
        : null;

    private async void Refresh()
    {
        if (_city.Length == 0 || _loading) return;
        _loading = true;
        var forecast = await _weather.GetAsync(_city);
        _loading = false;
        if (forecast is null)
        {
            if (_weather.Latest is null) _status.Text = "Couldn't load the weather. It retries in a while.";
            return;
        }
        Show(forecast);
    }

    private void Show(WeatherService.Forecast f)
    {
        var (text, icon) = WeatherService.Describe(f.Code);
        _temp.Text = $"{f.Temperature}°";
        _bigIcon.Content = Icon(Icons[icon], 40, IconColor(f.Code));
        _place.Text = f.City;
        _conditions.Text = text;
        _highLow.Text = $"H {f.High}°   L {f.Low}°";
        _hours.Children.Clear();
        foreach (var h in f.Hours)
        {
            _hours.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    Center(Text(h.Time.ToString("HH", CultureInfo.CurrentCulture) + "h", 11, Muted)),
                    Center(Icon(Icons[WeatherService.Describe(h.Code).Icon], 18, IconColor(h.Code)), new Thickness(0, 4, 0, 4)),
                    Center(Text($"{h.Temperature}°", 13, weight: FontWeights.SemiBold)),
                },
            });
        }
        _status.Visibility = Visibility.Collapsed;
        _content.Visibility = Visibility.Visible;
        RaisePillChanged();
    }

    private static T Center<T>(T e, Thickness? margin = null) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        if (margin is Thickness m) e.Margin = m;
        return e;
    }

    // Sun yellow, rain blue, the rest white.
    private static Brush IconColor(int code) => WeatherService.Describe(code).Icon switch
    {
        "sun" or "partly" => Sun,
        "rain" or "storm" => Drop,
        _ => Brushes.White,
    };

    public override void Dispose() => _refresh.Stop();
}
