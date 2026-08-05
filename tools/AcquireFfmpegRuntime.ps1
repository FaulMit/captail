[CmdletBinding()]
param(
    [string]$Destination = "",

    [ValidateSet("Shared", "Static")]
    [string]$Flavor = "Shared"
)

$ErrorActionPreference = "Stop"
$version = "n7.1.5-12-g1fdbca85aa"
$isStatic = $Flavor -eq "Static"
$archiveName = if ($isStatic) {
    "ffmpeg-$version-win64-lgpl-7.1.zip"
}
else {
    "ffmpeg-$version-win64-lgpl-shared-7.1.zip"
}
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-01-13-21/$archiveName"
$expectedArchiveSha256 = if ($isStatic) {
    "bc25a2260a1eaa6fd91d5774a8d8bc425c7d43ae9e8c74297d2d6ebc38e188cb"
}
else {
    "289ec4ca5a832cb1f2486ee35301d345c5dfaa28018f6177cdbbfbc2ad6f2e33"
}
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
