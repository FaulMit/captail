[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-RepoText([string]$relativePath) {
    return [IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Require([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

function Reject([string]$text, [string]$pattern, [string]$message) {
    if ($text -match $pattern) {
        throw $message
    }
}

$globalJson = Read-RepoText "global.json"
$project = Read-RepoText "src\Captail\Captail.csproj"
$lockFile = Read-RepoText "src\Captail\packages.lock.json"
$runtimeAcquisition = Read-RepoText "tools\AcquireObsRuntime.ps1"
$sdkAcquisition = Read-RepoText "tools\AcquireObsPluginSdk.ps1"
$mpvAcquisition = Read-RepoText "tools\AcquireMpvRuntime.ps1"
$ffmpegAcquisition = Read-RepoText "tools\AcquireFfmpegRuntime.ps1"
$engine = Read-RepoText "src\Captail\ObsReplayEngine.cs"
$releaseWorkflow = Read-RepoText ".github\workflows\release.yml"
$releaseScript = Read-RepoText "tools\BuildRelease.ps1"

Require $globalJson '"version"\s*:\s*"10\.0\.401"' `
    ".NET SDK must stay on the validated 10.0.401 feature band."
Require $project '<TargetFramework>net10\.0-windows10\.0\.22621\.0</TargetFramework>' `
    "Captail must target .NET 10 WPF."
Require $project 'H\.NotifyIcon\.Wpf" Version="2\.4\.1"' `
    "Captail must use H.NotifyIcon.Wpf 2.4.1."
Reject $project 'PackageReference Include="System\.Drawing\.Common"' `
    "System.Drawing.Common is supplied by .NET 10 WindowsDesktop; an explicit reference causes NU1510."
Reject $project 'Endpne\.LibMPV\.Windows' `
    "Captail must use the pinned mpv runtime instead of the stale NuGet binary."
Require $project '<MpvRuntimeRoot>[\s\S]*?runtime\\mpv</MpvRuntimeRoot>' `
    "Captail must copy libmpv from the pinned runtime directory."
Require $lockFile '"net10\.0-windows10\.0\.22621"' `
    "NuGet lock file must target .NET 10."
Require $lockFile '"H\.NotifyIcon\.Wpf"[\s\S]*?"resolved"\s*:\s*"2\.4\.1"' `
    "NuGet lock file must resolve H.NotifyIcon.Wpf 2.4.1."

foreach ($text in @($runtimeAcquisition, $sdkAcquisition)) {
    Require $text '\$version\s*=\s*"32\.2\.2"' `
        "OBS runtime and SDK acquisition must stay aligned on 32.2.2."
}
Require $runtimeAcquisition `
    '4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1' `
    "OBS 32.2.2 Windows archive hash is not pinned."
Require $engine 'RequiredObsVersion\s*=\s*"32\.2\.2"' `
    "Runtime OBS version guard must require 32.2.2."
Require $mpvAcquisition '\$version\s*=\s*"v0\.41\.0-1023-g69e63f425"' `
    "mpv runtime must stay on the validated v0.41.0-1023 build."
Require $mpvAcquisition `
    'fac135c68a35b7639e39d72c0c365104edbaebdea39a0dfdd8c36e8c8e80faef' `
    "mpv runtime archive hash is not pinned."
Require $ffmpegAcquisition '\$version\s*=\s*"n9\.0\.1-84-g946fcce07b"' `
    "FFmpeg runtime must stay on the validated 9.0.1 build."
Require $ffmpegAcquisition `
    'a2a50423b631cb51e91c2668c16a4807197c50d1f5fc56dd4516ca188a0d731f' `
    "FFmpeg shared runtime archive hash is not pinned."

Require $releaseWorkflow 'is-7_1_0/innosetup-7\.1\.0-x64\.exe' `
    "Release workflow must install Inno Setup 7.1.0."
Require $releaseWorkflow `
    '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f' `
    "Inno Setup 7.1.0 installer hash is not pinned."
Require $releaseScript '\[Version\]"7\.1\.0"' `
    "Local release builds must reject older Inno Setup compilers."

Write-Host "DEPENDENCY_VERSIONS_TEST PASS"
