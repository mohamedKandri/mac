# Prints the bar window's rectangle and the primary monitor's work area, in real pixels.
# With MacShell running, the work area should start at the bar's bottom edge (e.g. 0,40 at 125% scale).
# Run from a normal terminal: powershell -ExecutionPolicy Bypass -File tools\check-appbar.ps1
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class MacShellCheck {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() { return L + "," + T + " -> " + R + "," + B; } }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint c);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
"@
[MacShellCheck]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null   # see real pixels, like MacShell
$h = [MacShellCheck]::FindWindowW([NullString]::Value, 'MacShell Bar')           # $null would become "" in PowerShell
$bar = New-Object MacShellCheck+RECT; [MacShellCheck]::GetWindowRect($h, [ref]$bar) | Out-Null
$work = New-Object MacShellCheck+RECT; [MacShellCheck]::SystemParametersInfoW(0x30, 0, [ref]$work, 0) | Out-Null   # SPI_GETWORKAREA
"Explorer taskbar found: $([MacShellCheck]::FindWindowW('Shell_TrayWnd', [NullString]::Value) -ne 0)"
"Bar window:  $(if ($h -ne 0) { $bar } else { 'not running' })"
"Work area:   $work"
