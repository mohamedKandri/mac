using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacShell.Native;

// Win+1 … Win+9 for the dock, like the taskbar's shortcuts (Shift+Win+N = new window).
//
// Windows oddity: Explorer keeps handling Win+number even while its taskbar is hidden, and RegisterHotKey can't
// take a shortcut another program already holds. So this uses a low-level keyboard hook (WH_KEYBOARD_LL): Windows
// shows us every key press before anyone else, and returning 1 swallows it, so Explorer never sees Win+1.
// Second oddity: once the "1" is swallowed, Windows only sees "Win down, Win up" and opens the Start menu. Sending
// one harmless unassigned key (0xE8) while Win is held makes it a "Win + something" combination again, so the Start
// menu stays shut (the same trick PowerToys uses).
// The hook runs on the UI thread's message loop and must return within ~1 s or Windows drops it, so it only checks
// a few numbers and posts the real work to the dispatcher.
internal sealed class WinNumberKeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_DUMMY = 0xE8;
    private const uint LLKHF_INJECTED = 0x10;

    private readonly Win32.LowLevelKeyboardProc _proc;   // kept in a field so the GC can't free it while Windows uses it
    private readonly IntPtr _hook;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // (0-based dock position, Shift held). Raised on the UI thread.
    public event Action<int, bool>? Pressed;

    public WinNumberKeys()
    {
        _proc = HookProc;
        _hook = Win32.SetWindowsHookExW(WH_KEYBOARD_LL, _proc, Win32.GetModuleHandleW(null), 0);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    private IntPtr HookProc(int code, IntPtr message, IntPtr data)
    {
        if (code == HC_ACTION)
        {
            var key = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(data);
            int msg = (int)message;
            bool digit = key.vkCode is >= '1' and <= '9';
            if (digit && (key.flags & LLKHF_INJECTED) == 0 && Down(VK_LWIN) | Down(VK_RWIN) && !Down(VK_CONTROL) && !Down(VK_MENU))
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    int index = (int)key.vkCode - '1';
                    bool shift = Down(VK_SHIFT);
                    SendDummyKey();
                    _dispatcher.BeginInvoke(() => Pressed?.Invoke(index, shift));
                    return 1;   // swallowed: Explorer and the app in front never see it
                }
                if (msg is WM_KEYUP or WM_SYSKEYUP) return 1;   // its key-up too, so no app gets a stray "1" release
            }
        }
        return Win32.CallNextHookEx(_hook, code, message, data);
    }

    // The keyboard state as of this key event (the hook runs before it is applied, so the digit itself isn't in it).
    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendDummyKey()
    {
        var inputs = new[]
        {
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, ki = new Win32.KEYBDINPUT { wVk = VK_DUMMY } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, ki = new Win32.KEYBDINPUT { wVk = VK_DUMMY, dwFlags = Win32.KEYEVENTF_KEYUP } },
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
    }
}
