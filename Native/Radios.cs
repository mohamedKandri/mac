namespace MacShell.Native;

// Wi-Fi and Bluetooth on/off, the switches of Windows' quick settings (Windows.Devices.Radios, WinRT by hand:
// see WinRt). IIDs and slots from Include\10.0.26100.0\winrt\windows.devices.radios.h.
// RequestAccessAsync is asked once; Windows allows desktop apps without a prompt. Never throws.
internal sealed class Radios : IDisposable
{
    public enum Kind { WiFi = 1, Bluetooth = 3 }

    private const string RadioClass = "Windows.Devices.Radios.Radio";
    private static readonly Guid IID_RadioStatics = new("5fb6a12e-67cb-46ae-aae9-65919f86eff4");
    private const int Statics_GetRadiosAsync = 6, Statics_RequestAccessAsync = 9;
    private const int Radio_SetStateAsync = 6, Radio_State = 9, Radio_Kind = 11;
    private const int StateOn = 1, StateOff = 2;

    private readonly Dictionary<Kind, IntPtr> _radios = [];
    private bool _disposed;

    // Raised (on the UI thread) once the radios are known, and after every switch.
    public event Action? Changed;

    public Radios()
    {
        IntPtr statics = WinRt.GetStatics(RadioClass, IID_RadioStatics);
        if (statics == IntPtr.Zero) return;
        IntPtr access = WinRt.GetPointer(statics, Statics_RequestAccessAsync);
        IntPtr list = WinRt.GetPointer(statics, Statics_GetRadiosAsync);
        WinRt.Release(statics);
        WinRt.WhenDone(access, result => { });   // the answer itself isn't needed: SetState fails if denied
        WinRt.WhenDone(list, vector =>
        {
            foreach (IntPtr radio in WinRt.VectorItems(vector))
            {
                var kind = (Kind)WinRt.GetInt(radio, Radio_Kind, 0);
                if (_disposed || !Enum.IsDefined(kind) || _radios.ContainsKey(kind)) WinRt.Release(radio);
                else _radios[kind] = radio;
            }
            WinRt.Release(vector);
            Changed?.Invoke();
        });
    }

    public bool Has(Kind kind) => _radios.ContainsKey(kind);

    public bool IsOn(Kind kind) => _radios.TryGetValue(kind, out var r) && WinRt.GetInt(r, Radio_State, 0) == StateOn;

    // Turns a radio on or off (like the quick settings tile). Changed fires when Windows has done it.
    public void Set(Kind kind, bool on)
    {
        if (!_radios.TryGetValue(kind, out var radio)) return;
        IntPtr op = WinRt.CallWithInt(radio, Radio_SetStateAsync, on ? StateOn : StateOff);
        WinRt.WhenDone(op, _ => Changed?.Invoke());
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var radio in _radios.Values) WinRt.Release(radio);
        _radios.Clear();
    }
}
