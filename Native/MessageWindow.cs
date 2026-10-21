using System.Windows.Interop;

namespace MacShell.Native;

// A hidden window whose only job is to receive system broadcasts (setting, time and power changes)
// and messages Windows sends to a window handle (power notifications). Never shown: not on screen,
// in the taskbar or in Alt+Tab. A normal hidden window, not "message-only", because those miss broadcasts.
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    // (message, wParam, lParam). Runs on the UI thread.
    public event Action<int, IntPtr, IntPtr>? Message;

    public IntPtr Handle => _source.Handle;

    public MessageWindow(string name)
    {
        _source = new HwndSource(new HwndSourceParameters(name) { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            Message?.Invoke(msg, wParam, lParam);
            return IntPtr.Zero;
        });
    }

    public void Dispose() => _source.Dispose();
}
