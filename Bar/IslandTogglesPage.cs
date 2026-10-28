using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Island page: quick toggles (like the Mac's Control Center). Wi-Fi and Bluetooth switch for real (Native/Radios);
// Do not disturb and Night light have no public switch in Windows, so their tiles open the matching Settings page
// (marked with a small arrow). Volume (Native/Audio) and brightness (Native/Brightness, built-in screen) sliders.
internal sealed class IslandTogglesPage : IslandPage
{
    private static readonly SolidColorBrush On = Frozen.Brush(Color.FromRgb(0x2F, 0x6F, 0xD6));
    private static readonly SolidColorBrush Label = Frozen.Brush(Color.FromRgb(0xC8, 0xC8, 0xCE));
    private static readonly Geometry WifiIcon = Frozen.Geometry("M2,9 A15,15 0 0 1 22,9 M5.5,12.5 A10,10 0 0 1 18.5,12.5 M9,16 A5,5 0 0 1 15,16 M12,19.5 H12.01");
    private static readonly Geometry BluetoothIcon = Frozen.Geometry("M7,7 L17,17 L12,21 V3 L17,7 L7,17");
    private static readonly Geometry MoonIcon = Frozen.Geometry("M20,14.5 A8,8 0 1 1 9.5,4 A6.5,6.5 0 0 0 20,14.5 Z");
    private static readonly Geometry NightIcon = Frozen.Geometry("M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 M12,3 V5 M12,19 V21 M5,12 H3 M21,12 H19");
    private static readonly Geometry VolumeIcon = Frozen.Geometry("M11,5 L6,9 H3 V15 H6 L11,19 Z M15.5,9 A4,4 0 0 1 15.5,15");
    private static readonly Geometry SunIcon = Frozen.Geometry("M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22");
    private static readonly Geometry ArrowIcon = Frozen.Geometry("M8,16 L16,8 M10,8 H16 V14");

    private readonly Radios _radios = new();
    private readonly Audio _audio;
    private readonly Border _wifi, _bluetooth;
    private readonly Slider _volume, _brightness;
    private readonly DispatcherTimer _brightnessDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _pendingBrightness = -1;

    public IslandTogglesPage()
    {
        _audio = new Audio(Dispatcher.CurrentDispatcher);
        _wifi = Round(WifiIcon);
        _bluetooth = Round(BluetoothIcon);
        var row = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 4,
            Children =
            {
                Toggle(_wifi, "Wi-Fi", () => _radios.Set(Radios.Kind.WiFi, !_radios.IsOn(Radios.Kind.WiFi))),
                Toggle(_bluetooth, "Bluetooth", () => _radios.Set(Radios.Kind.Bluetooth, !_radios.IsOn(Radios.Kind.Bluetooth))),
                Toggle(Round(MoonIcon, settings: true), "Do not disturb", () => Shell.Open("ms-settings:notifications")),
                Toggle(Round(NightIcon, settings: true), "Night light", () => Shell.Open("ms-settings:nightlight")),
            },
        };

        _volume = new Slider(VolumeIcon, level => _audio.SetLevel(level / 100f));
        _brightness = new Slider(SunIcon, level =>
        {
            _pendingBrightness = level;           // WMI takes a few ms: apply at most every 120 ms while dragging
            if (!_brightnessDelay.IsEnabled) _brightnessDelay.Start();
        });
        _brightnessDelay.Tick += (_, _) =>
        {
            _brightnessDelay.Stop();
            if (_pendingBrightness >= 0) Native.Brightness.Set(_pendingBrightness);
            _pendingBrightness = -1;
        };

        View = new StackPanel
        {
            Width = ContentWidth,
            Children = { row, Spaced(_volume.View, 14), Spaced(_brightness.View, 10) },
        };
        _radios.Changed += UpdateRadios;
        _audio.Changed += () => _volume.Show((int)Math.Round(_audio.Read().Level * 100));
    }

    public override FrameworkElement View { get; }

    // Fresh values each time the page shows (the radios and brightness can change from Windows' own panels).
    public override void Shown()
    {
        UpdateRadios();
        var audio = _audio.Read();
        _volume.Show(audio.HasDevice ? (int)Math.Round(audio.Level * 100) : 0);
        int? brightness = Native.Brightness.Read();
        _brightness.View.Visibility = brightness is null ? Visibility.Collapsed : Visibility.Visible;   // desktop PC: no slider
        if (brightness is int b) _brightness.Show(b);
    }

    private void UpdateRadios()
    {
        _wifi.Background = _radios.IsOn(Radios.Kind.WiFi) ? On : Track;
        _bluetooth.Background = _radios.IsOn(Radios.Kind.Bluetooth) ? On : Track;
        _wifi.Opacity = _radios.Has(Radios.Kind.WiFi) ? 1 : 0.4;
        _bluetooth.Opacity = _radios.Has(Radios.Kind.Bluetooth) ? 1 : 0.4;
    }

    // A 48 px round tile; Settings shortcuts get a small arrow in the corner.
    private static Border Round(Geometry icon, bool settings = false)
    {
        var grid = new Grid { Children = { Icon(icon, 20, Brushes.White, 2) } };
        if (settings)
            grid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -10, -10), Child = Icon(ArrowIcon, 10, Muted, 2.5),
            });
        return new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = Track, Child = grid, Cursor = Cursors.Hand };
    }

    private static FrameworkElement Toggle(Border tile, string name, Action click)
    {
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // not the island's own click (pin / lyrics)
            click();
        };
        tile.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var label = Text(name, 10, Label);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 5, 0, 0);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Children = { tile, label } };
    }

    private static FrameworkElement Spaced(FrameworkElement e, double top)
    {
        e.Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    public override void Dispose()
    {
        _brightnessDelay.Stop();
        _radios.Dispose();
        _audio.Dispose();
    }

    // A slim slider: icon, track with a white fill, percentage. Click or drag along the track to set it.
    private sealed class Slider
    {
        private const double TrackWidth = 220;
        private readonly Border _fill = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _value = Text("", 11, Muted);
        private readonly Action<int> _changed;

        public FrameworkElement View { get; }

        public Slider(Geometry icon, Action<int> changed)
        {
            _changed = changed;
            var track = new Grid
            {
                Width = TrackWidth, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Children = { new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Track }, _fill },
            };
            track.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                track.CaptureMouse();
                SetFrom(e.GetPosition(track).X);
            };
            track.MouseMove += (_, e) =>
            {
                if (track.IsMouseCaptured) SetFrom(e.GetPosition(track).X);
            };
            track.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                track.ReleaseMouseCapture();
            };
            _value.Width = 34;
            _value.TextAlignment = TextAlignment.Right;
            _value.VerticalAlignment = VerticalAlignment.Center;
            View = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icon(icon, 16, Brushes.White, 2), track, _value } };
        }

        public void Show(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _fill.Width = TrackWidth * percent / 100;
            _value.Text = $"{percent}%";
        }

        private void SetFrom(double x)
        {
            int percent = (int)Math.Round(Math.Clamp(x / TrackWidth, 0, 1) * 100);
            Show(percent);
            _changed(percent);
        }
    }
}
