[CmdletBinding()]
param(
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$version = "v0.41.0-1023-g69e63f425"
$releaseTag = "20260903"
$archiveName = "mpv-dev-x86_64-20260903-git-69e63f425a.7z"
$expectedArchiveSha256 =
    "fac135c68a35b7639e39d72c0c365104edbaebdea39a0dfdd8c36e8c8e80faef"
$url =
    "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/" +
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
    $Destination = Join-Path $allowedRuntimeRoot "mpv"
}

$Destination = [IO.Path]::GetFullPath($Destination)
$allowedPrefix = $allowedRuntimeRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $Destination.StartsWith(
        $allowedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "mpv runtime destination must stay under $allowedRuntimeRoot"
}

$archive = Join-Path $env:TEMP $archiveName
$extract = Join-Path $env:TEMP "Captail-mpv-$PID-$([Guid]::NewGuid().ToString('N'))"
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
        Write-Host "Downloading mpv $version runtime..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    }
    $actualHash = Get-Sha256Hex $archive
    if (-not $actualHash.Equals(
            $expectedArchiveSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archive -Force
        throw "mpv archive SHA-256 mismatch. Expected $expectedArchiveSha256; found $actualHash."
    }

    $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source -First 1
    if (-not $sevenZip) {
        $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    }
    if (-not (Test-Path -LiteralPath $sevenZip)) {
        throw '7-Zip is required to extract the mpv runtime archive.'
    }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    & $sevenZip x -y "-o$extract" $archive | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not extract mpv runtime archive (exit code $LASTEXITCODE)."
    }
    $libmpv = Get-ChildItem -LiteralPath $extract -Filter libmpv-2.dll -Recurse |
        Select-Object -First 1
    if (-not $libmpv) {
        throw "libmpv-2.dll not found in mpv archive."
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath $libmpv.FullName -Destination $Destination
    Set-Content -LiteralPath (Join-Path $Destination "VERSION") `
        -Value $version -Encoding UTF8
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

Write-Host "mpv runtime $version ready: $Destination"
