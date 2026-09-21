# Builds dist\Chrono-Setup.exe, a per-user installer (no admin rights).
#  1. runs scripts\publish.ps1 to build dist\Chrono (skip with -SkipPublish if it is already built)
#  2. downloads the Inno Setup compiler from NuGet into .tools\ (git-ignored) if it is not there yet
#  3. compiles scripts\chrono.iss
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-SkipPublish]
# Output: dist\Chrono-Setup.exe  (the portable zip from publish.ps1 stays beside it)

param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$innoVersion = '6.7.1'
$tools = Join-Path $root '.tools'
$innoDir = Join-Path $tools "innosetup-$innoVersion"
$iscc = Get-ChildItem -Path $innoDir -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }

if (-not $iscc) {
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $nupkg = Join-Path $tools "innosetup.$innoVersion.zip"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$innoVersion/tools.innosetup.$innoVersion.nupkg" -OutFile $nupkg -UseBasicParsing
    Expand-Archive -Path $nupkg -DestinationPath $innoDir -Force
    Remove-Item $nupkg
    $iscc = Get-ChildItem -Path $innoDir -Filter ISCC.exe -Recurse | Select-Object -First 1
    if (-not $iscc) { throw 'ISCC.exe was not found in the Inno Setup package' }
}

[xml]$csproj = Get-Content (Join-Path $root 'client\ChronoRecorder\ChronoRecorder.csproj')
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '1.0.0' }

& $iscc.FullName "/DAppVersion=$version" "/DSourceDir=$(Join-Path $root 'dist\Chrono')" (Join-Path $PSScriptRoot 'chrono.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $root 'dist\Chrono-Setup.exe'
Write-Host ("Done: {0} ({1} MB)" -f $setup, [math]::Round((Get-Item $setup).Length / 1MB, 1))
