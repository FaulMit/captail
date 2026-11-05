[CmdletBinding()]
param(
    [string]$Destination = "",

    [ValidateSet("Shared", "Static")]
    [string]$Flavor = "Shared"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$version = "n9.0.1-84-g946fcce07b"
$upstreamReleaseTag = "autobuild-2026-09-18-13-22"
$releaseTag = "runtime-assets-20260923"
$isStatic = $Flavor -eq "Static"
$archiveName = if ($isStatic) {
    "captail-ffmpeg-9.0.1-static.zip"
}
else {
    "captail-ffmpeg-9.0.1-shared.zip"
}
$expectedArchiveSha256 = if ($isStatic) {
    "caa82e5c5540bed005806a2ab8891e7a24aa2d3ca84f5d34ff18cf010e237536"
}
else {
    "675a7772d369f09928da8b23ef8b975114ba754f09136ccbe63ec1d7150e0cee"
}
# Upstream autobuild retention removed the original archives. The mirror contains
# unchanged validated binaries; retain upstream provenance separately.
$upstreamArchiveSha256 = if ($isStatic) {
    "e17c2a1313bec165f12f5a73d07447b03292bbd7a9153af9de901e981e0109d6"
}
else {
    "a2a50423b631cb51e91c2668c16a4807197c50d1f5fc56dd4516ca188a0d731f"
}
$url =
    "https://github.com/FaulMit/captail/releases/download/" +
    "$releaseTag/$archiveName"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$allowedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "runtime"))

function Get-Sha256Hex([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($stream))).Replace("-", "")
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $algorithm.Dispose()
    }
}

if (-not $Destination) {
    $runtimeName = if ($isStatic) { "ffmpeg-static" } else { "ffmpeg" }
    $Destination = Join-Path $allowedRuntimeRoot $runtimeName
}

$Destination = [IO.Path]::GetFullPath($Destination)
$allowedPrefix = $allowedRuntimeRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $Destination.StartsWith(
        $allowedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "FFmpeg runtime destination must stay under $allowedRuntimeRoot"
}

$archive = Join-Path $env:TEMP $archiveName
$extract = Join-Path $env:TEMP "Captail-FFmpeg-$PID-$([Guid]::NewGuid().ToString('N'))"
try {
    if (Test-Path -LiteralPath $archive) {
        $existingHash = Get-Sha256Hex $archive
        if (-not $existingHash.Equals(
                $expectedArchiveSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $archive -Force
        }
    }
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading FFmpeg $version runtime..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    }
    $actualHash = Get-Sha256Hex $archive
    if (-not $actualHash.Equals(
            $expectedArchiveSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archive -Force
        throw "FFmpeg archive SHA-256 mismatch. Expected $expectedArchiveSha256; found $actualHash."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $ffmpeg = Get-ChildItem -LiteralPath $extract -Filter ffmpeg.exe -Recurse |
        Select-Object -First 1
    if (-not $ffmpeg) {
        throw "ffmpeg.exe not found in FFmpeg archive."
    }
    $binRoot = $ffmpeg.Directory.FullName
    if (-not (Test-Path -LiteralPath (Join-Path $binRoot "ffprobe.exe"))) {
        throw "ffprobe.exe not found in FFmpeg archive."
    }
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $binRoot "ffmpeg.exe") -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $binRoot "ffprobe.exe") -Destination $Destination
    Get-ChildItem -LiteralPath $binRoot -File -Filter *.dll |
        Copy-Item -Destination $Destination

    Set-Content -LiteralPath (Join-Path $Destination "VERSION") `
        -Value $version -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Destination "FLAVOR") `
        -Value $Flavor.ToLowerInvariant() -Encoding ascii
    Set-Content -LiteralPath (Join-Path $Destination "SOURCE_URL") `
        -Value $url -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Destination "SOURCE_SHA256") `
        -Value $expectedArchiveSha256 -Encoding ascii
    Set-Content -LiteralPath (Join-Path $Destination "UPSTREAM_RELEASE") `
        -Value "https://github.com/BtbN/FFmpeg-Builds/releases/tag/$upstreamReleaseTag" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Destination "UPSTREAM_ARCHIVE_SHA256") `
        -Value $upstreamArchiveSha256 -Encoding ascii
}
finally {
    if (Test-Path -LiteralPath $extract) {
        $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedExtract = [IO.Path]::GetFullPath($extract)
        if ($resolvedExtract.StartsWith(
                $tempRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
        }
    }
}

Write-Host "FFmpeg runtime $version ($Flavor) ready: $Destination"
