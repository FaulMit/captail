[CmdletBinding()]
param(
    [string]$ObsRoot = "",
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"
$version = "32.1.2"
$expectedArchiveSha256 = "8d97e4563bd8d22d03e63042aa7dccede1d555c9bd35ce8a9e5019b0d0201bf6"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$allowedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "runtime"))
$temporaryExtract = ""
if (-not $Destination) {
    $Destination = Join-Path $allowedRuntimeRoot "obs"
}

if (-not $ObsRoot) {
    $archive = Join-Path $env:TEMP "OBS-Studio-$version-Windows-x64.zip"
    $extract = Join-Path $env:TEMP `
        "Captail-OBS-$version-$PID-$([Guid]::NewGuid().ToString('N'))"
    $temporaryExtract = [IO.Path]::GetFullPath($extract)
    if (Test-Path -LiteralPath $archive) {
        $existingHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if (-not $existingHash.Equals(
                $expectedArchiveSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $archive -Force
        }
    }
    if (-not (Test-Path -LiteralPath $archive)) {
        $url = "https://github.com/obsproject/obs-studio/releases/download/$version/OBS-Studio-$version-Windows-x64.zip"
        Write-Host "Downloading OBS Studio $version runtime..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (-not $actualHash.Equals(
            $expectedArchiveSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archive -Force
        throw "OBS archive SHA-256 mismatch. Expected $expectedArchiveSha256; found $actualHash."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
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
if ($actualVersion -notmatch "^$([regex]::Escape($version))(?:\.0)?$") {
    throw "OBS $version required; found $actualVersion."
}

$Destination = [IO.Path]::GetFullPath($Destination)
$allowedPrefix = $allowedRuntimeRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $Destination.StartsWith(
        $allowedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OBS runtime destination must stay under $allowedRuntimeRoot"
}
$binDestination = Join-Path $Destination "bin"
$pluginDestination = Join-Path $Destination "obs-plugins\64bit"
$dataDestination = Join-Path $Destination "data"
foreach ($path in @($binDestination, (Join-Path $Destination "obs-plugins"), $dataDestination)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
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
$runtimeLibraries = @(
    "avcodec-61.dll",
    "avdevice-61.dll",
    "avfilter-10.dll",
    "avformat-61.dll",
    "avutil-59.dll",
    "libcurl.dll",
    "libobs-d3d11.dll",
    "libobs-winrt.dll",
    "librist.dll",
    "libx264-164.dll",
    "obs.dll",
    "srt.dll",
    "swresample-5.dll",
    "swscale-8.dll",
    "w32-pthreads.dll",
    "zlib.dll"
)
foreach ($library in $runtimeLibraries) {
    $libraryPath = Join-Path $ObsRoot "bin\64bit\$library"
    if (-not (Test-Path -LiteralPath $libraryPath)) {
        throw "Required OBS runtime library not found: $libraryPath"
    }
    Copy-Item -LiteralPath $libraryPath -Destination $binDestination -Force
}

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
        $pluginDataDestination = Join-Path $dataDestination "obs-plugins\$plugin"
        Copy-Item -LiteralPath $pluginData -Destination $pluginDataDestination `
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
Get-ChildItem -LiteralPath $dataDestination -File -Filter *.pdb -Recurse |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Set-Content -LiteralPath (Join-Path $Destination "VERSION") `
    -Value $version -Encoding UTF8
Set-Content -LiteralPath (Join-Path $Destination "SOURCE_SHA256") `
    -Value $expectedArchiveSha256 -Encoding ascii
if ($temporaryExtract) {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $temporaryExtract.StartsWith(
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean temporary extraction outside $tempRoot"
    }
    Remove-Item -LiteralPath $temporaryExtract -Recurse -Force
}
Write-Host "OBS runtime $version ready: $Destination"
