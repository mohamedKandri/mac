using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Widgets;

// Settings: "showName" (default true).
public partial class WifiWidget : UserControl, IWidget
{
    private readonly Wifi _wifi = new();
    private readonly bool _showName;

    public FrameworkElement View => this;

    public WifiWidget(WidgetEntry entry)
    {
        InitializeComponent();
        _showName = entry.GetBool("showName", true);
        _wifi.Changed += Show;
        Show();
    }

    private void Show()
    {
        var state = Wifi.Read();
        Visibility = state.HasAdapter ? Visibility.Visible : Visibility.Collapsed;
        if (!state.HasAdapter) return;

        Icon.Data = (Geometry)FindResource(state.Connected ? "Icon.Wifi" : "Icon.WifiOff");
        Icon.FillData = state.Connected ? (Geometry)FindResource("Icon.WifiDot") : null;
        // Muted when disconnected; SetResourceReference keeps following theme switches.
        Button.SetResourceReference(ForegroundProperty, state.Connected ? ThemeKeys.Foreground : ThemeKeys.Muted);

        NetworkName.Text = state.Name ?? "";
        NetworkName.Visibility = _showName && state.Connected && state.Name is not null ? Visibility.Visible : Visibility.Collapsed;
        Button.ToolTip = state.Connected ? $"Wi-Fi: {state.Name ?? "connected"}" : "Wi-Fi: not connected";
    }

    private void OnClick(object sender, RoutedEventArgs e) => Shell.Open("ms-availablenetworks:");

    public void Dispose() => _wifi.Dispose();
}
