[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$ChangelogPath = "",

    [string]$Repository = "FaulMit/captail",

    [string]$PreviousTag = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $ChangelogPath) {
    $ChangelogPath = Join-Path $repoRoot "CHANGELOG.md"
}

if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Changelog not found: $ChangelogPath"
}

$changelog = Get-Content -LiteralPath $ChangelogPath -Raw
$escapedVersion = [Regex]::Escape($Version)
$pattern = "(?ms)^## \[$escapedVersion\] - (?<date>\d{4}-\d{2}-\d{2})\r?\n(?<body>.*?)(?=^## \[|\z)"
$match = [Regex]::Match($changelog, $pattern)
if (-not $match.Success) {
    throw "CHANGELOG.md has no release section for [$Version]."
}

$changeBody = $match.Groups["body"].Value.Trim()
if (-not $changeBody) {
    throw "CHANGELOG.md release section [$Version] is empty."
}

# Changelog entries are nested below version headings. Promote their headings
# one level when the entry becomes a standalone GitHub Release description.
$changeBody = [Regex]::Replace($changeBody, '(?m)^### ', '## ')

$lines = [Collections.Generic.List[string]]::new()
$lines.Add("> [!WARNING]")
$lines.Add("> Captail $Version is an early public preview. Bugs and hardware-specific issues are expected.")
$lines.Add("")
$lines.Add($changeBody)
$lines.Add("")
$lines.Add("## Downloads")
$lines.Add("")
$lines.Add("- **Installer (recommended):** ``Captail-$Version-Setup-win-x64.exe`` includes Captail, .NET, libobs, FFmpeg, and an uninstaller.")
$lines.Add("- **Portable:** extract the entire ``Captail-$Version-Portable-win-x64.zip`` before launching ``Captail.exe``.")
$lines.Add("")
$lines.Add("## Compatibility and feedback")
$lines.Add("")
$lines.Add("Captail is tested on NVIDIA GeForce RTX 40 and RTX 50 series. Older NVIDIA, AMD, and Intel hardware needs broader public testing. Report problems through [GitHub Issues](https://github.com/$Repository/issues).")
$lines.Add("")
$lines.Add("Release binaries are not Authenticode-signed yet. Windows may show an unknown-publisher or SmartScreen warning. Use ``SHA256SUMS.txt`` and GitHub build provenance to verify the download.")

if ($PreviousTag) {
    $lines.Add("")
    $lines.Add("**Full changelog:** https://github.com/$Repository/compare/$PreviousTag...v$Version")
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if ($outputDirectory) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$notes = ($lines -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine
[IO.File]::WriteAllText(
    $outputFullPath,
    $notes,
    [Text.UTF8Encoding]::new($false))

Write-Host "Release notes written to $outputFullPath"
