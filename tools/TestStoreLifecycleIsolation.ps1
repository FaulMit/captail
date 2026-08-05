[CmdletBinding()]
param(
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"
if (-not $ProjectRoot) {
    $ProjectRoot = Join-Path $PSScriptRoot "..\src\Captail"
}
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)

$pathChecks = [ordered]@{
    "Config.cs" = "AppDataPaths.ConfigFile"
    "Log.cs" = "AppDataPaths.LogFile"
    "ReplayLibrary.cs" = "AppDataPaths.ThumbnailDirectory"
    "ObsPluginDataCache.cs" = "AppDataPaths.ObsPluginCacheDirectory"
    "ObsReplayEngine.cs" = "AppDataPaths.ObsConfigDirectory"
}

foreach ($entry in $pathChecks.GetEnumerator()) {
    $path = Join-Path $ProjectRoot $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Store lifecycle source file not found: $path"
    }
    $source = Get-Content -LiteralPath $path -Raw
    if (-not $source.Contains($entry.Value, [StringComparison]::Ordinal)) {
        throw "$($entry.Key) does not use managed Store data path $($entry.Value)."
    }
}

$pathsSource = Join-Path $ProjectRoot "AppDataPaths.cs"
if (-not (Test-Path -LiteralPath $pathsSource -PathType Leaf)) {
    throw "AppDataPaths.cs is missing."
}
$pathsText = Get-Content -LiteralPath $pathsSource -Raw
foreach ($required in @(
    "ApplicationData.Current.LocalFolder.Path",
    "ApplicationData.Current.LocalCacheFolder.Path")) {
    if (-not $pathsText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Store data path resolver is missing: $required"
    }
}

$lifecycleSource = Join-Path $ProjectRoot "StorePackageLifecycle.cs"
if (-not (Test-Path -LiteralPath $lifecycleSource -PathType Leaf)) {
    throw "StorePackageLifecycle.cs is missing."
}
$lifecycleText = Get-Content -LiteralPath $lifecycleSource -Raw
foreach ($required in @(
    "PackageCatalog.OpenForCurrentPackage()",
    "PackageUninstalling",
    "PackageUpdating")) {
    if (-not $lifecycleText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Store package lifecycle listener is missing: $required"
    }
}

Write-Host "PASS: Store state is package-managed and update/uninstall events are handled."
