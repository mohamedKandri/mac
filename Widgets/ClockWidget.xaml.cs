using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MacShell.Core;
using MacShell.Native;

namespace MacShell.Widgets;

// Settings: "format" = a .NET date format, e.g. "ddd d MMM  HH:mm" -> "Wed 21 Oct  10:01".
// Cheat sheet: ddd = Wed, dddd = Wednesday, d = 21, MMM = Oct, HH:mm = 24 h, h:mm tt = 12 h, ss = seconds.
public partial class ClockWidget : UserControl, IWidget
{
    private const string DefaultFormat = "ddd d MMM  HH:mm";

    private readonly string _format;
    private readonly bool _showsSeconds;
    private readonly DispatcherTimer _timer = new();
    private readonly SystemEvents _system = new("MacShellClock");
    private readonly PopupToggle _calendar;

    public FrameworkElement View => this;

    public ClockWidget(WidgetContext context, WidgetEntry entry)
    {
        InitializeComponent();
        _calendar = new PopupToggle(Button, context, () => new CalendarPopup(context), alignRight: true);
        _format = entry.GetString("format", DefaultFormat);
        _showsSeconds = _format.Contains('s');

        _timer.Tick += (_, _) => Update();
        _system.TimeChanged += Update;
        _system.Resumed += Update;   // after sleep the timer may have fired late or not at all
        Update();
    }

    // Shows the time, then schedules the next update exactly at the next minute (or second),
    // so the bar wakes up once a minute instead of polling.
    private void Update()
    {
        var now = DateTime.Now;
        Time.Text = now.ToString(_format, CultureInfo.CurrentCulture);

        var next = _showsSeconds
            ? now.AddTicks(TimeSpan.TicksPerSecond - now.Ticks % TimeSpan.TicksPerSecond)
            : now.AddTicks(TimeSpan.TicksPerMinute - now.Ticks % TimeSpan.TicksPerMinute);
        _timer.Stop();
        _timer.Interval = next - now + TimeSpan.FromMilliseconds(20);   // land just after the boundary
        _timer.Start();
    }

    private void OnClick(object sender, RoutedEventArgs e) => _calendar.Toggle();

    // Also used by the `--open calendar` developer switch (see App.xaml.cs).
    public void OpenCalendar() => _calendar.Open();

    // Called by WidgetRegistry at config load: reject formats .NET can't use.
    public static string? Validate(WidgetEntry entry)
    {
        string format = entry.GetString("format", DefaultFormat);
        if (string.IsNullOrWhiteSpace(format)) return "'format' can't be empty.";
        try
        {
            DateTime.Now.ToString(format, CultureInfo.CurrentCulture);
            return null;
        }
        catch (FormatException)
        {
            return $"'{format}' is not a valid date format.";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _system.Dispose();
        _calendar.Close();
    }
}
