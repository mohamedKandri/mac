using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Widgets;

// Settings: "showPercentage" (default false).
public partial class VolumeWidget : UserControl, IWidget
{
    private const float Step = 0.02f;   // per scroll notch, same as Windows

    private readonly Audio _audio;
    private readonly bool _showPercentage;

    public FrameworkElement View => this;

    public VolumeWidget(WidgetEntry entry)
    {
        InitializeComponent();
        _showPercentage = entry.GetBool("showPercentage", false);
        _audio = new Audio(Dispatcher);
        _audio.Changed += Show;
        Show();
    }

    private void Show()
    {
        var state = _audio.Read();
        int percent = (int)Math.Round(state.Level * 100);

        string icon = !state.HasDevice || state.Muted ? "Icon.VolumeMute"
            : percent == 0 ? "Icon.VolumeOff"
            : percent < 50 ? "Icon.VolumeLow"
            : "Icon.VolumeHigh";
        Icon.Data = (Geometry)FindResource(icon);
        Button.SetResourceReference(ForegroundProperty, state.HasDevice ? ThemeKeys.Foreground : ThemeKeys.Muted);

        Percent.Text = $"{percent}%";
        Percent.Visibility = _showPercentage && state.HasDevice ? Visibility.Visible : Visibility.Collapsed;
        Button.ToolTip = !state.HasDevice ? "No audio output" : state.Muted ? $"Muted ({percent}%)" : $"Volume {percent}%";
    }

    // One notch = Delta 120 (precision touchpads send smaller deltas, which this handles proportionally).
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var state = _audio.Read();
        if (!state.HasDevice) return;
        _audio.SetLevel(state.Level + Step * e.Delta / 120f);
        e.Handled = true;
    }

    // Windows 11 Quick Settings (has the volume slider and output device picker).
    private void OnClick(object sender, RoutedEventArgs e) => Shell.Open("ms-actioncenter:controlcenter/&showFooter=true");

    public void Dispose() => _audio.Dispose();
}
