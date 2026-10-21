namespace MacShell.Native;

// Battery level and charging state, with a Changed event. No polling: Windows notifies us when the
// percentage changes or the charger is plugged in/out.
internal sealed class Battery : IDisposable
{
    public readonly record struct State(bool Present, int Percent, bool Charging, bool PluggedIn);

    private readonly MessageWindow _window = new("MacShellBattery");
    private readonly List<IntPtr> _registrations = [];

    public event Action? Changed;

    public Battery()
    {
        foreach (var setting in new[] { Win32.GUID_BATTERY_PERCENTAGE_REMAINING, Win32.GUID_ACDC_POWER_SOURCE })
        {
            var guid = setting;
            IntPtr handle = Win32.RegisterPowerSettingNotification(_window.Handle, ref guid, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (handle != IntPtr.Zero) _registrations.Add(handle);
        }

        _window.Message += (msg, wParam, _) =>
        {
            if (msg != Win32.WM_POWERBROADCAST) return;
            int kind = (int)wParam;
            // PBT_APMPOWERSTATUSCHANGE also covers "charging started/stopped" while staying plugged in.
            if (kind is Win32.PBT_POWERSETTINGCHANGE or Win32.PBT_APMPOWERSTATUSCHANGE or Win32.PBT_APMRESUMEAUTOMATIC)
                Changed?.Invoke();
        };
    }

    public static State Read()
    {
        if (!Win32.GetSystemPowerStatus(out var s) || s.BatteryFlag == Win32.BATTERY_FLAG_UNKNOWN)
            return new State(false, 0, false, false);

        bool present = (s.BatteryFlag & Win32.BATTERY_FLAG_NO_BATTERY) == 0;
        int percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 0;
        bool charging = (s.BatteryFlag & Win32.BATTERY_FLAG_CHARGING) != 0;
        return new State(present, percent, charging, s.ACLineStatus == 1);
    }

    public void Dispose()
    {
        foreach (var handle in _registrations) Win32.UnregisterPowerSettingNotification(handle);
        _window.Dispose();
    }
}
