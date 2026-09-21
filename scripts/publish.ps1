# Builds the Chrono download for friends: one folder (and a zip) that runs on a clean Windows 10/11 PC.
#  - self-contained: carries the .NET runtime, so nothing has to be installed first
#  - carries FFmpeg (scripts\fetch-ffmpeg.ps1 is run if it is missing)
#  - carries Microsoft's WebView2 installer, used on the rare PC that lacks the WebView2 runtime
#  - built as a Windows app, so no console window opens
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
# Output: dist\Chrono\  and  dist\Chrono-win-x64.zip

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'client\ChronoRecorder\ChronoRecorder.csproj'
$out = Join-Path $root 'dist\Chrono'
$zip = Join-Path $root 'dist\Chrono-win-x64.zip'

if (-not (Test-Path (Join-Path $root 'client\ChronoRecorder\ffmpeg\ffmpeg.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1')
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -p:OutputType=WinExe -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The WebView2 runtime ships with Windows 11 and most Windows 10 PCs; this covers the rest.
$redist = Join-Path $out 'redist'
New-Item -ItemType Directory -Path $redist -Force | Out-Null
$bootstrapper = Join-Path $redist 'MicrosoftEdgeWebview2Setup.exe'
Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper -UseBasicParsing

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"
