using System.Diagnostics;

namespace MacShell.Native;

internal static class Shell
{
    // Opens a URI the way Win+R would: "ms-settings:..." pages, Windows flyouts ("ms-availablenetworks:"), web links.
    public static void Open(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // Unknown URI on this Windows build: nothing useful to do from a bar click.
        }
    }
}
