[CmdletBinding()]
param(
    [string]$Destination = "",

    [ValidateSet("Shared", "Static")]
    [string]$Flavor = "Shared"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$version = "n8.1-latest"
$isStatic = $Flavor -eq "Static"
$archiveName = if ($isStatic) {
    "ffmpeg-$version-win64-lgpl-8.1.zip"
}
else {
    "ffmpeg-$version-win64-lgpl-shared-8.1.zip"
}
$releaseApiUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest"
$githubHeaders = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "Captail-build"
}
$githubToken = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN")
if (-not [string]::IsNullOrWhiteSpace($githubToken)) {
    $githubHeaders.Authorization = "Bearer $githubToken"
}
$release = Invoke-RestMethod -UseBasicParsing -Uri $releaseApiUrl `
    -Headers $githubHeaders
$asset = $release.assets | Where-Object { $_.name -eq $archiveName } |
    Select-Object -First 1
if (-not $asset) {
    throw "FFmpeg release asset not found: $archiveName"
}
if ($asset.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') {
    throw "FFmpeg release asset has no valid GitHub SHA-256 digest: $archiveName"
}
$expectedArchiveSha256 = $Matches[1]
$url = [string]$asset.browser_download_url
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
