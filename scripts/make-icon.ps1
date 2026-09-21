# Builds Chrono's icon files from one square PNG logo (transparent background, ideally 1024 px or bigger).
#
#   client\ChronoRecorder\Assets\chrono.ico     the exe / window / taskbar icon (16, 24, 32, 48, 64, 128, 256 px)
#   client\ChronoRecorder\Assets\logo-256.png   the master the tray icon is drawn from (its centre dot is recoloured
#                                               for "waiting" and "paused")
#   client\ChronoRecorder\UI\img\logo.png       the logo in the window's sidebar
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1 -Source "C:\path\to\logo.png"

param([Parameter(Mandatory = $true)][string]$Source)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'client\ChronoRecorder\Assets'
$img = Join-Path $root 'client\ChronoRecorder\UI\img'
New-Item -ItemType Directory -Force -Path $assets, $img | Out-Null

$logo = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
if ($logo.Width -ne $logo.Height) { throw "The logo must be square, but it is $($logo.Width)x$($logo.Height)." }

# Trim the transparent margin around the artwork (keeping 1.5%), so the icon fills its square at small sizes.
$minX = $logo.Width; $maxX = 0
for ($x = 0; $x -lt $logo.Width; $x += 2) {
    if ($logo.GetPixel($x, [int]($logo.Height / 2)).A -gt 40) { if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x } }
}
$span = $maxX - $minX
$pad = [int]($span * 0.015)
$side = [Math]::Min($logo.Width, $span + 2 * $pad)
$cx = ($minX + $maxX) / 2
$left = [Math]::Max(0, [Math]::Min($logo.Width - $side, [int]($cx - $side / 2)))
$crop = New-Object System.Drawing.Rectangle $left, $left, $side, $side

function Resize([System.Drawing.Bitmap]$from, [System.Drawing.Rectangle]$region, [int]$size) {
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($from, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $region, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $out
}

function PngBytes([System.Drawing.Bitmap]$bitmap) {
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# ---- the .ico: PNG-compressed entries at every size Windows asks for
$sizes = 16, 24, 32, 48, 64, 128, 256
$entries = @()
foreach ($size in $sizes) {
    $bmp = Resize $logo $crop $size
    $entries += , @{ Size = $size; Data = (PngBytes $bmp) }
    $bmp.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$e.Data.Length); $w.Write([uint32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write($e.Data) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'chrono.ico'), $ico.ToArray())

# ---- the tray master and the sidebar logo
$master = Resize $logo $crop 256
$master.Save((Join-Path $assets 'logo-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

$side96 = Resize $logo $crop 96
$side96.Save((Join-Path $img 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$side96.Dispose()
$logo.Dispose()

Write-Host "Icon files written:"
Get-ChildItem $assets, $img | Where-Object { $_.Name -match 'chrono.ico|logo' } | ForEach-Object { "  {0}  ({1} KB)" -f $_.FullName.Replace($root + '\', ''), [math]::Round($_.Length / 1KB, 1) }
