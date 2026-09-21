# Downloads the FFmpeg build that Chrono ships with into client/ChronoRecorder/ffmpeg/ (git-ignored).
# The build is gyan.dev's official "release essentials": it has everything Chrono uses (ddagrab, NVENC,
# h264_cuvid, amix normalize) and is smaller than the "full" build. The download is checked against the
# SHA-256 that gyan.dev publishes next to it. Run once; the project copies the folder next to the app.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1 [-Force]

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'client\ChronoRecorder\ffmpeg'
$exe = Join-Path $target 'ffmpeg.exe'

if ((Test-Path $exe) -and -not $Force) {
    Write-Host "FFmpeg is already at $exe (use -Force to download again)."
    & $exe -version | Select-Object -First 1
    return
}

$base = 'https://www.gyan.dev/ffmpeg/builds'
$zipUrl = "$base/ffmpeg-release-essentials.zip"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("chrono-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $zip = Join-Path $temp 'ffmpeg.zip'
    Write-Host "Downloading $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing

    $expected = (Invoke-WebRequest -Uri "$zipUrl.sha256" -UseBasicParsing).Content.Trim().Split()[0].ToLower()
    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
    if ($expected -ne $actual) { throw "Checksum mismatch: expected $expected but got $actual. Not using this download." }
    Write-Host "Checksum OK ($actual)"

    Expand-Archive -Path $zip -DestinationPath $temp -Force
    $build = Get-ChildItem -Path $temp -Directory | Where-Object { $_.Name -like 'ffmpeg-*' } | Select-Object -First 1

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $build.FullName 'bin\ffmpeg.exe') $exe -Force
    # FFmpeg is GPL software; ship its licence with it.
    Copy-Item (Join-Path $build.FullName 'LICENSE') (Join-Path $target 'LICENSE.txt') -Force -ErrorAction SilentlyContinue
    Set-Content -Path (Join-Path $target 'SOURCE.txt') -Value @(
        "This is FFmpeg $($build.Name), built by gyan.dev (https://www.gyan.dev/ffmpeg/builds/).",
        "FFmpeg is licensed under the GPL; its source code is available from https://ffmpeg.org/download.html",
        "and https://github.com/GyanD/codexffmpeg."
    )

    Write-Host "Installed FFmpeg to $target"
    & $exe -version | Select-Object -First 1
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
