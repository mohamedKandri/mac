using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// Island page: the network right now. Connection name (Native/Wifi) and ping (1.1.1.1, every 2 s; some networks
// block it, then "—"), download / upload speed (byte counters of the active adapters, every second) and a chart of
// the last minute. Everything runs ONLY while this page is open: closed, it costs nothing.
internal sealed class IslandNetworkPage : IslandPage
{
    private const int Samples = 60;
    private const double ChartHeight = 52;
    private static readonly SolidColorBrush Down = Frozen.Brush(Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly SolidColorBrush Up = Frozen.Brush(Color.FromRgb(0x34, 0xC7, 0x59));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _name, _ping, _down, _up;
    private readonly Polyline _downLine = new() { Stroke = Down, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polyline _upLine = new() { Stroke = Up, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Queue<(double Down, double Up)> _history = new();
    private readonly Ping _pinger = new();
    private (long Received, long Sent, DateTime At)? _last;
    private int _ticks;
    private bool _pinging;

    public IslandNetworkPage()
    {
        _name = Text("", 13, weight: FontWeights.SemiBold);
        _ping = Text("Ping —", 12, Muted);
        DockPanel.SetDock(_ping, Dock.Right);
        _down = Text("—", 22, weight: FontWeights.SemiBold);
        _up = Text("—", 22, weight: FontWeights.SemiBold);
        var stats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { Stat("Download", Down, _down), Stat("Upload", Up, _up) },
        };
        var chart = new Grid
        {
            Width = ContentWidth, Height = ChartHeight, Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Track },
                _downLine, _upLine,
            },
        };
        View = new StackPanel { Width = ContentWidth, Children = { new DockPanel { Children = { _ping, _name } }, stats, chart } };
        _tick.Tick += (_, _) => Sample();
    }

    public override FrameworkElement View { get; }

    public override void Shown()
    {
        var wifi = Wifi.Read();
        _name.Text = wifi.Connected && !string.IsNullOrEmpty(wifi.Name) ? $"{wifi.Name} · Wi-Fi" : "Network";
        _last = null;
        _history.Clear();
        _downLine.Points.Clear();
        _upLine.Points.Clear();
        Sample();
        _tick.Start();
    }

    public override void Hidden() => _tick.Stop();

    private void Sample()
    {
        var (received, sent) = Totals();
        var now = DateTime.UtcNow;
        if (_last is var (r0, s0, t0))
        {
            double seconds = Math.Max(0.2, (now - t0).TotalSeconds);
            double down = Math.Max(0, received - r0) * 8 / seconds, up = Math.Max(0, sent - s0) * 8 / seconds;   // bits/s
            _down.Text = Format(down);
            _up.Text = Format(up);
            _history.Enqueue((down, up));
            while (_history.Count > Samples) _history.Dequeue();
            Draw();
        }
        _last = (received, sent, now);
        if (_ticks++ % 2 == 0) Ping();
    }

    // Bytes in/out of the adapters that are up (no loopback or tunnels' doubles).
    private static (long Received, long Sent) Totals()
    {
        long received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var stats = nic.GetIPStatistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }
        return (received, sent);
    }

    private async void Ping()
    {
        if (_pinging) return;
        _pinging = true;
        try
        {
            var reply = await _pinger.SendPingAsync("1.1.1.1", 1000);
            _ping.Text = reply.Status == IPStatus.Success ? $"Ping {reply.RoundtripTime} ms" : "Ping —";
        }
        catch (Exception) { _ping.Text = "Ping —"; }   // ICMP blocked, no network
        finally { _pinging = false; }
    }

    // Both lines scaled to the busiest second of the last minute.
    private void Draw()
    {
        double max = Math.Max(1, _history.Max(h => Math.Max(h.Down, h.Up)));
        var down = new PointCollection();
        var up = new PointCollection();
        int i = 0, n = Math.Max(2, Samples);
        foreach (var (d, u) in _history)
        {
            double x = ContentWidth * i / (n - 1);
            down.Add(new Point(x, ChartHeight - 1 - (ChartHeight - 4) * d / max));
            up.Add(new Point(x, ChartHeight - 1 - (ChartHeight - 4) * u / max));
            i++;
        }
        _downLine.Points = down;
        _upLine.Points = up;
    }

    // 12.4 Mb/s, 850 kb/s.
    private static string Format(double bits) => bits >= 1_000_000
        ? (bits / 1_000_000).ToString(bits >= 100_000_000 ? "0" : "0.0", CultureInfo.CurrentCulture) + " Mb/s"
        : (bits / 1000).ToString("0", CultureInfo.CurrentCulture) + " kb/s";

    private static FrameworkElement Stat(string label, Brush color, TextBlock value) =>
        new StackPanel { Margin = new Thickness(0, 0, 28, 0), Children = { Text(label, 11, color), value } };

    public override void Dispose()
    {
        _tick.Stop();
        _pinger.Dispose();
    }
}
