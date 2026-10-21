using Microsoft.Win32;

namespace MacShell.Native;

// "Start with Windows": at sign-in, Windows launches every program listed under
// HKEY_CURRENT_USER\...\CurrentVersion\Run. Per-user, so no admin rights needed.
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacShell";

    // Quoted because the path may contain spaces. Environment.ProcessPath is the real .exe, even for a single-file app.
    private static string Command => $"\"{Environment.ProcessPath}\"";

    // True only if the entry points at THIS exe. If you move the exe, it reads false until you tick it again.
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
