namespace MacShell.Native;

// System broadcasts as plain C# events, so widgets never see raw window messages.
internal sealed class SystemEvents : IDisposable
{
    private readonly MessageWindow _window;

    public event Action? TimeChanged;   // clock or time zone changed (Settings, NTP sync)
    public event Action? Resumed;       // woke up from sleep or hibernate

    public SystemEvents(string name)
    {
        _window = new MessageWindow(name);
        _window.Message += (msg, wParam, _) =>
        {
            if (msg == Win32.WM_TIMECHANGE)
            {
                TimeZoneInfo.ClearCachedData();   // .NET caches the time zone; drop it so DateTime.Now is right
                TimeChanged?.Invoke();
            }
            else if (msg == Win32.WM_POWERBROADCAST && (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC)
            {
                Resumed?.Invoke();
            }
        };
    }

    public void Dispose() => _window.Dispose();
}
