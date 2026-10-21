using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacShell.Native;

// Windows' Settings > Personalization > Colors: "Choose your mode" (light/dark for apps) and "Transparency effects".
// Raises Changed when either is switched, so "theme": "auto" follows Windows live.
internal sealed class SystemTheme : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly MessageWindow _window = new("MacShellThemeWatcher");

    public event Action? Changed;

    public static bool AppsUseLightTheme => ReadFlag("AppsUseLightTheme");
    public static bool TransparencyEnabled => ReadFlag("EnableTransparency");

    public SystemTheme()
    {
        _window.Message += (msg, _, lParam) =>
        {
            // lParam points to a string naming what changed.
            if (msg == Win32.WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                Changed?.Invoke();
            }
        };
    }

    // Both values are DWORDs: 1 = on/light, 0 = off/dark. Missing means Windows' default (on/light).
    private static bool ReadFlag(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is not int value || value != 0;
    }

    public void Dispose() => _window.Dispose();
}
