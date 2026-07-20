[CmdletBinding()]
param(
    [string]$ObsRoot = "",
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"
$version = "32.1.2"
if (-not $Destination) {
    $Destination = Join-Path $PSScriptRoot "..\runtime\obs"
}

if (-not $ObsRoot) {
    $installed = Join-Path $env:ProgramFiles "obs-studio"
    if (Test-Path (Join-Path $installed "bin\64bit\obs64.exe")) {
        $ObsRoot = $installed
    }
}

if (-not $ObsRoot) {
    $archive = Join-Path $env:TEMP "OBS-Studio-$version-Windows-x64.zip"
    $extract = Join-Path $env:TEMP "Captail-OBS-$version"
    if (-not (Test-Path $archive)) {
        $url = "https://github.com/obsproject/obs-studio/releases/download/$version/OBS-Studio-$version-Windows-x64.zip"
        Write-Host "Downloading OBS Studio $version runtime..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    }
    if (-not (Test-Path $extract)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extract
    }
    $obsExe = Get-ChildItem -LiteralPath $extract -Filter obs64.exe -Recurse |
        Select-Object -First 1
    if (-not $obsExe) {
        throw "obs64.exe not found in OBS archive."
    }
    $ObsRoot = $obsExe.Directory.Parent.Parent.FullName
}

$obsExePath = Join-Path $ObsRoot "bin\64bit\obs64.exe"
if (-not (Test-Path $obsExePath)) {
    throw "Invalid OBS root: $ObsRoot"
}
$actualVersion = (Get-Item $obsExePath).VersionInfo.ProductVersion
if (-not $actualVersion.StartsWith($version, [StringComparison]::Ordinal)) {
    throw "OBS $version required; found $actualVersion."
}

$Destination = [IO.Path]::GetFullPath($Destination)
$binDestination = Join-Path $Destination "bin"
$pluginDestination = Join-Path $Destination "obs-plugins\64bit"
$dataDestination = Join-Path $Destination "data"
New-Item -ItemType Directory -Force -Path $binDestination, $pluginDestination, $dataDestination |
    Out-Null

foreach ($helper in @(
    "obs-amf-test.exe",
    "obs-ffmpeg-mux.exe",
    "obs-nvenc-test.exe",
    "obs-qsv-test.exe"
)) {
    $helperPath = Join-Path $ObsRoot "bin\64bit\$helper"
    if (Test-Path $helperPath) {
        Copy-Item -LiteralPath $helperPath -Destination $binDestination -Force
    }
}
Get-ChildItem -LiteralPath (Join-Path $ObsRoot "bin\64bit") -Filter *.dll |
    Copy-Item -Destination $binDestination -Force

$plugins = @(
    "obs-ffmpeg",
    "obs-nvenc",
    "obs-qsv11",
    "obs-x264",
    "win-capture",
    "win-wasapi"
)
foreach ($plugin in $plugins) {
    Copy-Item -LiteralPath (Join-Path $ObsRoot "obs-plugins\64bit\$plugin.dll") `
        -Destination $pluginDestination -Force
    $pluginData = Join-Path $ObsRoot "data\obs-plugins\$plugin"
    if (Test-Path $pluginData) {
        Copy-Item -LiteralPath $pluginData -Destination (Join-Path $dataDestination "obs-plugins") `
            -Recurse -Force
    }
}
Copy-Item -LiteralPath (Join-Path $ObsRoot "data\libobs") `
    -Destination $dataDestination -Recurse -Force

# Captail ships English and Russian UI only. OBS plugin locale directories
# contain hundreds of tiny files; retaining these two locales keeps extracted
# Portable builds compact without removing capture or encoder functionality.
$localesToKeep = @("en-US.ini", "ru-RU.ini")
Get-ChildItem -LiteralPath $dataDestination -Directory -Filter locale -Recurse |
    ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -File |
            Where-Object { $_.Name -notin $localesToKeep } |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Force
            }
    }

Set-Content -LiteralPath (Join-Path $Destination "VERSION") `
    -Value $version -Encoding UTF8
Write-Host "OBS runtime $version ready: $Destination"
