using System.Windows;
using System.Windows.Controls;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Widgets;

// Settings: "showPercentage" (default true).
public partial class BatteryWidget : UserControl, IWidget
{
    private const double FullWidth = 13;   // level bar width at 100%, in icon grid units
    private const int LowPercent = 20;

    private readonly Battery _battery = new();
    private readonly bool _showPercentage;

    public FrameworkElement View => this;

    public BatteryWidget(WidgetEntry entry)
    {
        InitializeComponent();
        _showPercentage = entry.GetBool("showPercentage", true);
        _battery.Changed += Show;
        Show();
    }

    private void Show()
    {
        var state = Battery.Read();
        Visibility = state.Present ? Visibility.Visible : Visibility.Collapsed;
        if (!state.Present) return;

        Percent.Text = $"{state.Percent}%";
        Percent.Visibility = _showPercentage ? Visibility.Visible : Visibility.Collapsed;

        Level.Width = Math.Max(1.5, FullWidth * state.Percent / 100.0);   // a sliver stays visible at 1%
        Level.Visibility = state.Charging ? Visibility.Collapsed : Visibility.Visible;
        Bolt.Visibility = state.Charging ? Visibility.Visible : Visibility.Collapsed;

        // SetResourceReference = {DynamicResource ...} from code, so it still follows theme switches.
        bool low = state.Percent <= LowPercent && !state.PluggedIn;
        Level.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, low ? ThemeKeys.Warning : ThemeKeys.Foreground);

        Button.ToolTip = state.Charging ? $"{state.Percent}% – charging"
            : state.PluggedIn ? $"{state.Percent}% – plugged in"
            : $"{state.Percent}% – on battery";
    }

    private void OnClick(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:batterysaver");

    public void Dispose() => _battery.Dispose();
}
