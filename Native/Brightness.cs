using System.Management;

namespace MacShell.Native;

// The built-in screen's brightness (laptops), through WMI (root\wmi: WmiMonitorBrightness to read,
// WmiMonitorBrightnessMethods.WmiSetBrightness to set). There's no Win32 API for a laptop panel's brightness, and
// external monitors aren't covered (they'd need DDC/CI). Each call takes a few ms; never throws.
internal static class Brightness
{
    // 0-100, or null when the screen can't be dimmed this way (desktop PC, external monitor only).
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
                using (item) return Convert.ToInt32(item["CurrentBrightness"]);
        }
        catch (Exception) { }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
                using (item) item.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);   // timeout 1 s
        }
        catch (Exception) { }
    }
}
