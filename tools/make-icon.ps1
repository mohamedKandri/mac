# Regenerates Themes\MacShell.ico: the design's logo (rounded square + dot) at every tray size.
# Run with Windows PowerShell 5.1 (it ships System.Drawing): powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes  = 16, 20, 24, 32, 40, 48, 64, 256
$accent = [System.Drawing.ColorTranslator]::FromHtml('#5B9DFF')
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square filled with the accent color
    $inset = [math]::Max(1, $s * 0.06)
    $side = $s - 2 * $inset
    $r = $side * 0.30
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($inset + $side - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($inset + $side - 2 * $r, $inset + $side - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($inset, $inset + $side - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $path)

    # White dot in the middle
    $d = $s * 0.30
    $g.FillEllipse([System.Drawing.Brushes]::White, ($s - $d) / 2, ($s - $d) / 2, $d, $d)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , $ms.ToArray()
    $g.Dispose(); $bmp.Dispose()
}

# .ico layout: 6-byte header, one 16-byte directory entry per image, then the PNG data
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\Themes\MacShell.ico'), $out.ToArray())
Write-Host 'Wrote Themes\MacShell.ico'
