using System.Windows.Media;
using MacShell.Native;

namespace MacShell.Bar;

// What the popup shows for a ring: a title line, one main bar, and a short list (busiest apps, drives...).
internal sealed record NotchDetails(
    string Title, string Subtitle,
    string MainLabel, double MainPercent, string MainValue,
    string ListTitle, IReadOnlyList<SystemStats.AppLoad> Rows);

// One ring in the notch. Adding a ring = a subclass + one line in Create (and its key in NotchConfig.KnownRings).
internal abstract class NotchRing : IDisposable
{
    public abstract string Label { get; }      // "CPU"
    public abstract string IconKey { get; }    // geometry in Themes/Icons.xaml
    public abstract Color Color { get; }
    public int Percent { get; protected set; }
    public bool Available { get; protected set; } = true;   // false hides the ring (e.g. no battery)

    // Every `notch.refreshSeconds`; `tick` lets slow-changing rings (disk) update less often.
    public abstract void Update(int tick);

    // While its popup is open (refreshed with the rings).
    public abstract NotchDetails Details();

    public virtual void Dispose() { }

    public static NotchRing Create(string key) => key switch
    {
        "cpu" => new CpuRing(),
        "memory" => new MemoryRing(),
        "disk" => new DiskRing(),
        "battery" => new BatteryRing(),
        _ => throw new ArgumentException($"Unknown ring '{key}'"),   // ConfigLoader rejects these first
    };

    protected static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

internal sealed class CpuRing : NotchRing
{
    private readonly SystemStats.CpuMeter _meter = new();
    private readonly SystemStats.AppCpuMeter _apps = new();

    public override string Label => "CPU";
    public override string IconKey => "Icon.Cpu";
    public override Color Color => Hex("#FF6A3D");

    public override void Update(int tick) => Percent = _meter.Sample();

    public override NotchDetails Details() => new(
        "CPU", $"{Environment.ProcessorCount} logical cores",
        "Total load", Percent, $"{Percent}% used",
        "Busiest apps", _apps.Sample(3));   // empty on the first sample, filled 2 s later
}

internal sealed class MemoryRing : NotchRing
{
    private SystemStats.Memory _memory;

    public override string Label => "Memory";
    public override string IconKey => "Icon.Memory";
    public override Color Color => Hex("#34C759");

    public override void Update(int tick)
    {
        _memory = SystemStats.ReadMemory();
        Percent = _memory.Percent;
    }

    public override NotchDetails Details() => new(
        "Memory", $"{_memory.TotalGb:F0} GB installed",
        "In use", Percent, $"{_memory.UsedGb:F1} GB",
        "Busiest apps", SystemStats.TopMemory(_memory.TotalGb, 3));
}

internal sealed class DiskRing : NotchRing
{
    private List<SystemStats.Drive> _drives = [];

    public override string Label => "Disk";
    public override string IconKey => "Icon.Disk";
    public override Color Color => Hex("#4DA3FF");

    // Disk space changes slowly: every 15th refresh is plenty.
    public override void Update(int tick)
    {
        if (tick % 15 != 0 && _drives.Count > 0) return;
        _drives = SystemStats.ReadDrives();
        Percent = _drives.Count > 0 ? _drives[0].Percent : 0;
    }

    public override NotchDetails Details()
    {
        var system = _drives.FirstOrDefault();
        return new(
            "Disk", $"{_drives.Count} drive{(_drives.Count == 1 ? "" : "s")}",
            $"System ({system.Name})", system.Percent, $"{system.TotalGb - system.UsedGb:F0} GB free",
            "Drives", _drives.Select(d => new SystemStats.AppLoad(d.Name, d.Percent, $"{d.UsedGb:F0}/{d.TotalGb:F0} GB")).ToList());
    }
}

internal sealed class BatteryRing : NotchRing
{
    private Battery.State _state;

    public override string Label => "Battery";
    public override string IconKey => "Icon.BatteryRing";
    public override Color Color => Hex("#FFD60A");

    public override void Update(int tick)
    {
        _state = Battery.Read();
        Available = _state.Present;
        Percent = _state.Percent;
    }

    public override NotchDetails Details() => new(
        "Battery", _state.Charging ? "Charging" : _state.PluggedIn ? "Plugged in" : "On battery",
        "Charge", Percent, $"{Percent}%",
        "", []);
}
