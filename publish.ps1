# Builds the single self-contained MacShell.exe into .\dist
$ErrorActionPreference = 'Stop'
$stage = "$PSScriptRoot\obj\publish-stage"
$exe = "$PSScriptRoot\dist\MacShell.exe"

dotnet publish "$PSScriptRoot\MacShell.csproj" -c Release -o $stage
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# A running MacShell locks its exe, and right after it exits Windows (antivirus scan) can hold it a few seconds more.
# So stop it, then retry the copy for up to 15 s.
$wasRunning = @(Get-Process MacShell -ErrorAction SilentlyContinue).Count -gt 0
# First ask it to quit cleanly (it restores the taskbar and the screen space), then hard-kill only if it doesn't.
try {
    $quit = [Threading.EventWaitHandle]::OpenExisting('Local\MacShell.Quit')
    $quit.Set() | Out-Null; $quit.Dispose()
    for ($i = 0; $i -lt 50 -and (Get-Process MacShell -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
} catch { }   # not running, or an older build without the quit signal
Get-Process MacShell -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
for ($i = 1; ; $i++) {
    try { Copy-Item "$stage\MacShell.exe" $exe -Force; break }
    catch { if ($i -ge 30) { throw }; Start-Sleep -Milliseconds 500 }
}
Write-Host "Done: $exe"
if ($wasRunning) { Write-Host "MacShell was stopped to replace the exe. Start dist\MacShell.exe again from your desktop (not from Claude's sandbox)." }
