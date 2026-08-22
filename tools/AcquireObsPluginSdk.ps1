[CmdletBinding()]
param(
    [string]$Destination = ""
)

function Get-Sha256Hash {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $stream = [IO.File]::OpenRead($LiteralPath)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString(
                $algorithm.ComputeHash($stream)).Replace("-", "")
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$ErrorActionPreference = "Stop"
$version = "32.1.2"
$expectedArchiveSha256 = "21cba22292985cf0da967d5c618999b40eaa32b73d2ab8b06154b5ea1b3d3798"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "runtime"))
if (-not $Destination) {
    $Destination = Join-Path $runtimeRoot "obs-sdk"
}
$Destination = [IO.Path]::GetFullPath($Destination)
$allowedPrefix = $runtimeRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $Destination.StartsWith(
        $allowedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OBS SDK destination must stay under $runtimeRoot"
}

$marker = Join-Path $Destination "VERSION"
$header = Join-Path $Destination "include\obs-module.h"
$generatedConfig = Join-Path $Destination "include\obsconfig.h"
$sourceHashMarker = Join-Path $Destination "SOURCE_SHA256"
if ((Test-Path -LiteralPath $header) -and
    (Test-Path -LiteralPath $generatedConfig) -and
    (Test-Path -LiteralPath $marker) -and
    (Test-Path -LiteralPath $sourceHashMarker) -and
    ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $version) -and
    ((Get-Content -LiteralPath $sourceHashMarker -Raw).Trim() -eq
        $expectedArchiveSha256)) {
    Write-Host "OBS plugin headers $version ready: $Destination"
    return
}

$archive = Join-Path $env:TEMP "obs-studio-$version-source.zip"
if (Test-Path -LiteralPath $archive) {
    $existingHash = Get-Sha256Hash -LiteralPath $archive
    if (-not $existingHash.Equals(
            $expectedArchiveSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archive -Force
    }
}
if (-not (Test-Path -LiteralPath $archive)) {
    $url = "https://github.com/obsproject/obs-studio/archive/refs/tags/$version.zip"
    Write-Host "Downloading OBS Studio $version public headers..."
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
}
$actualHash = Get-Sha256Hash -LiteralPath $archive
if (-not $actualHash.Equals(
        $expectedArchiveSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $archive -Force
    throw "OBS source archive SHA-256 mismatch. Expected $expectedArchiveSha256; found $actualHash."
}

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
$includeRoot = Join-Path $Destination "include"
New-Item -ItemType Directory -Force -Path $includeRoot | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $prefix = "obs-studio-$version/libobs/"
    foreach ($entry in $zip.Entries) {
        if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or
            -not $entry.Name) {
            continue
        }
        $relative = $entry.FullName.Substring($prefix.Length)
        if ([IO.Path]::GetExtension($relative) -notin ".h", ".hpp") {
            continue
        }
        $target = [IO.Path]::GetFullPath((Join-Path $includeRoot $relative))
        $includePrefix = $includeRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $target.StartsWith(
                $includePrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe OBS header archive entry: $($entry.FullName)"
        }
        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally {
    $zip.Dispose()
}

if (-not (Test-Path -LiteralPath $header)) {
    throw "obs-module.h was not extracted from the OBS source archive."
}
Set-Content -LiteralPath $generatedConfig -Encoding ascii -Value @(
    "#pragma once",
    "#define OBS_RELEASE_CANDIDATE 0",
    "#define OBS_BETA 0"
)
Set-Content -LiteralPath $marker -Value $version -Encoding ascii
Set-Content -LiteralPath $sourceHashMarker `
    -Value $expectedArchiveSha256 -Encoding ascii
Write-Host "OBS plugin headers $version ready: $Destination"
