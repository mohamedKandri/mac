# Read-only health check of the desktop and MacShell.
# Run it yourself from Windows Terminal (NOT from Claude's sandbox or the Claude app's Terminal panel: both have a
# restricted view and report Explorer, the taskbar and MacShell as missing even when they're running):
#   powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Diag {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() { return L + "," + T + " -> " + R + "," + B; } }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint c);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
}
"@
[Diag]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
function Win($class, $title) {
    $h = [Diag]::FindWindowW($class, $title)
    if ($h -eq [IntPtr]::Zero) { return 'not found' }
    $r = New-Object Diag+RECT; [Diag]::GetWindowRect($h, [ref]$r) | Out-Null
    return "$r visible=$([Diag]::IsWindowVisible($h))"
}
$work = New-Object Diag+RECT; [Diag]::SystemParametersInfoW(0x30, 0, [ref]$work, 0) | Out-Null
$abd = New-Object Diag+APPBARDATA; $abd.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Diag+APPBARDATA])
$state = [Diag]::SHAppBarMessage(4, [ref]$abd)   # ABM_GETSTATE: 1 = auto-hide, 2 = always on top

"== Explorer"
Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { "  pid $($_.Id) started $($_.StartTime.ToString('HH:mm:ss')) $($_.Path)" }
"  taskbar (Shell_TrayWnd): $(Win 'Shell_TrayWnd' $null)"
"  desktop (Progman):       $(Win 'Progman' $null)"
"  taskbar auto-hide: $(($state.ToUInt64() -band 1) -ne 0)"
"== MacShell"
Get-Process MacShell -ErrorAction SilentlyContinue | ForEach-Object { "  pid $($_.Id) started $($_.StartTime.ToString('HH:mm:ss')) $([math]::Round($_.PrivateMemorySize64 / 1MB)) MB" }
if (-not (Get-Process MacShell -ErrorAction SilentlyContinue)) { "  not running" }
"  bar:  $(Win $null 'MacShell Bar')"
"  dock: $(Win $null 'MacShell Dock')"
"== Work area (primary): $work"
"== Wallpaper: $((Get-ItemProperty 'HKCU:\Control Panel\Desktop' -ErrorAction SilentlyContinue).WallPaper)"
"== Shell setting (should be explorer.exe):"
"  HKLM: $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).Shell)"
"  HKCU: $((Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).Shell)"
"== App crashes / errors in the last 30 min (Application log):"
try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2; StartTime = (Get-Date).AddMinutes(-30) } -MaxEvents 15 -ErrorAction Stop |
        ForEach-Object { "  $($_.TimeCreated.ToString('HH:mm:ss')) [$($_.ProviderName) $($_.Id)] " + (($_.Message -split "`n" | Select-Object -First 3) -join ' | ').Trim() }
} catch { "  none ($($_.Exception.Message))" }
"== crash.log:"
$log = Join-Path $PSScriptRoot '..\dist\crash.log'
if (Test-Path $log) { Get-Content $log -Tail 15 } else { "  none" }
