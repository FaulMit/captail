[CmdletBinding()]
param(
    [ValidateSet(
        "overview",
        "capture",
        "audio",
        "player",
        "ui",
        "distribution",
        "release")]
    [string]$Area = "overview",

    [ValidateRange(1, 100)]
    [int]$MaxStatusEntries = 20,

    [ValidateRange(20, 500)]
    [int]$MaxDocumentLines = 160,

    [switch]$NoDocumentText
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

$areas = @{
    overview = @{
        Docs = @(
            "docs/agent/INDEX.md",
            "docs/agent/CURRENT.md",
            "docs/agent/ARCHITECTURE.md",
            "docs/agent/KNOWN_ISSUES.md"
        )
        Paths = @(
            "src/Captail/App.xaml.cs",
            "src/Captail/Config.cs",
            "src/Captail/ObsReplayEngine.cs",
            "src/Captail/SettingsWindow.xaml.cs",
            "src/Captail/ClipEditorWindow.xaml.cs"
        )
        Tests = @(
            ".\tools\TestAgentContext.ps1",
            "dotnet build .\Captail.sln -c Release --no-restore"
        )
    }
    capture = @{
        Docs = @(
            "docs/agent/CAPTURE.md",
            "docs/agent/decisions/0001-app-owns-capture-lifecycle.md"
        )
        Paths = @(
            "src/Captail/App.xaml.cs",
            "src/Captail/Config.cs",
            "src/Captail/ObsReplayEngine.cs",
            "src/Captail/ObsNative.cs",
            "src/Captail/Interop/CaptureInterop.cs",
            "src/Captail/ReplayPaths.cs",
            "native/ObsBridge/"
        )
        Tests = @(
            ".\tools\TestCapturePlayerRegression.ps1",
            ".\tools\TestIssue41Fixes.ps1",
            ".\tools\TestFullscreenOverlayHotkeys.ps1",
            "dotnet build .\Captail.sln -c Release --no-restore"
        )
    }
    audio = @{
        Docs = @(
            "docs/agent/AUDIO.md",
            "docs/agent/decisions/0002-advanced-audio-is-optional.md"
        )
        Paths = @(
            "src/Captail/AudioDevices.cs",
            "src/Captail/AudioRoutingCapabilities.cs",
            "src/Captail/ProcessAudioMonitor.cs",
            "src/Captail/ProcessAudioReconciler.cs",
            "src/Captail/ProcessAudioRoutingWindow.xaml",
            "src/Captail/ProcessAudioRoutingWindow.xaml.cs",
            "src/Captail/ProcessAudioSessionMonitor.cs",
            "src/Captail/ProcessIconProvider.cs",
            "src/Captail/Interop/ProcessTopology.cs",
            "native/ProcessAudio/"
        )
        Tests = @(
            ".\tools\TestIssue41Fixes.ps1",
            ".\tools\TestIssue41Followup.ps1",
            "dotnet build .\Captail.sln -c Release --no-restore"
        )
    }
    player = @{
        Docs = @("docs/agent/PLAYER.md")
        Paths = @(
            "src/Captail/ClipEditorWindow.xaml",
            "src/Captail/ClipEditorWindow.xaml.cs",
            "src/Captail/MpvHost.cs",
            "src/Captail/ReplayLibrary.cs",
            "src/Captail/FfmpegAdapter.cs"
        )
        Tests = @(
            ".\tools\TestCapturePlayerRegression.ps1",
            ".\tools\TestNextFeatureRegression.ps1",
            "dotnet build .\Captail.sln -c Release --no-restore"
        )
    }
    ui = @{
        Docs = @("docs/agent/UI.md")
        Paths = @(
            "src/Captail/SettingsWindow.xaml",
            "src/Captail/SettingsWindow.xaml.cs",
            "src/Captail/OverlayNotificationWindow.xaml",
            "src/Captail/OverlayNotificationWindow.xaml.cs",
            "src/Captail/ReplayStatusIndicatorWindow.xaml",
            "src/Captail/ReplayStatusIndicatorWindow.xaml.cs",
            "src/Captail/DisplayIdentifierWindow.xaml",
            "src/Captail/DisplayIdentifierWindow.xaml.cs",
            "src/Captail/Localization.cs",
            "src/Captail/Languages/",
            "src/Captail/Themes/",
            "src/Captail/ThemeManager.cs",
            "src/Captail/HotkeyManager.cs"
        )
        Tests = @(
            ".\tools\TestReportedUiRuntimeRegressions.ps1",
            ".\tools\TestStartupUiSurfaces.ps1",
            ".\tools\TestLocalizedLayout.ps1",
            ".\tools\TestNextFeatureRegression.ps1",
            "dotnet build .\Captail.sln -c Release --no-restore"
        )
    }
    distribution = @{
        Docs = @(
            "docs/agent/DISTRIBUTION.md",
            "docs/agent/decisions/0003-release-channels-are-independent.md"
        )
        Paths = @(
            "src/Captail/AppDataPaths.cs",
            "src/Captail/AppDistribution.cs",
            "src/Captail/Autostart.cs",
            "src/Captail/DiagnosticLogExporter.cs",
            "src/Captail/StorePackageLifecycle.cs",
            "src/Captail/UpdateService.cs",
            "packaging/",
            "store-listing/",
            ".github/workflows/release.yml",
            ".github/workflows/store-release.yml"
        )
        Tests = @(
            ".\tools\TestStoreLifecycleIsolation.ps1",
            ".\tools\TestStoreFfmpegIsolation.ps1",
            ".\tools\TestStoreNativeDependencies.ps1",
            ".\tools\TestStoreIconAssets.ps1",
            ".\tools\TestStoreReleaseWorkflow.ps1"
        )
    }
    release = @{
        Docs = @(
            "docs/agent/WORKFLOWS.md",
            "docs/agent/decisions/0003-release-channels-are-independent.md"
        )
        Paths = @(
            "CHANGELOG.md",
            "README.md",
            "docs/RELEASING.md",
            "docs/RELEASE_NOTES.md",
            "docs/SCREENSHOTS.md",
            "docs/MICROSOFT-STORE-RELEASES.md",
            "packaging/",
            "store-listing/",
            ".github/workflows/release.yml",
            ".github/workflows/store-release.yml",
            "tools/BuildRelease.ps1",
            "tools/BuildStorePackage.ps1",
            "tools/CaptureReadmeScreenshots.ps1",
            "tools/New-StoreListingScreenshots.ps1",
            "tools/SyncStoreListing.ps1"
        )
        Tests = @(
            ".\tools\TestStoreReleaseWorkflow.ps1",
            ".\tools\TestStoreIconAssets.ps1",
            ".\tools\SyncStoreListing.ps1 -Version <version> -ValidateOnly"
        )
    }
}

function Invoke-Git([string[]]$Arguments) {
    $output = @(& git -C $repoRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }
    return $output
}

function Normalize-RepoPath([string]$Path) {
    return $Path.Trim().Trim('"').Replace('\', '/')
}

function Get-StatusPath([string]$Line) {
    if ($Line.Length -le 3) {
        return ""
    }
    $path = $Line.Substring(3).Trim()
    if ($path.Contains(" -> ")) {
        $path = ($path -split " -> ", 2)[1]
    }
    return Normalize-RepoPath $path
}

function Test-RelevantPath([string]$Path, [object[]]$Prefixes) {
    foreach ($prefixValue in $Prefixes) {
        $prefix = Normalize-RepoPath ([string]$prefixValue)
        if ($prefix.EndsWith("/")) {
            if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        elseif ($Path.Equals($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

$selection = $areas[$Area]
foreach ($relativePath in @($selection.Docs) + @($selection.Paths)) {
    $candidate = Join-Path $repoRoot ([string]$relativePath).TrimEnd('/', '\')
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Context route points to a missing path: $relativePath"
    }
}
[xml]$project = Get-Content -LiteralPath (
    Join-Path $repoRoot "src/Captail/Captail.csproj")
$version = [string](
    $project.Project.PropertyGroup.Version | Select-Object -First 1)
$branch = (
    Invoke-Git -Arguments @("branch", "--show-current") |
        Select-Object -First 1)
$head = (
    Invoke-Git -Arguments @("rev-parse", "--short", "HEAD") |
        Select-Object -First 1)
$status = @(
    Invoke-Git -Arguments @("status", "--short", "--untracked-files=all"))
$relevantPrefixes = @($selection.Paths) + @($selection.Docs) + @(
    "AGENTS.md",
    "docs/agent/INDEX.md",
    "tools/GetAgentContext.ps1",
    "tools/TestAgentContext.ps1"
)
$relevantStatus = @(
    $status | Where-Object {
        $Area -eq "overview" -or
        (Test-RelevantPath (Get-StatusPath $_) $relevantPrefixes)
    }
)

Write-Output "# Captail task context: $Area"
Write-Output ""
Write-Output "## Live state"
Write-Output ""
Write-Output "- Repository: ``$repoRoot``"
Write-Output "- Branch: ``$branch``"
Write-Output "- HEAD: ``$head``"
Write-Output "- Project version: ``$version``"
Write-Output "- Working tree entries: $($status.Count) total; $($relevantStatus.Count) relevant"

if ($relevantStatus.Count -gt 0) {
    Write-Output ""
    Write-Output "Relevant status (capped at $MaxStatusEntries):"
    Write-Output ""
    Write-Output '```text'
    $relevantStatus | Select-Object -First $MaxStatusEntries | Write-Output
    if ($relevantStatus.Count -gt $MaxStatusEntries) {
        Write-Output "... $($relevantStatus.Count - $MaxStatusEntries) more relevant entries"
    }
    Write-Output '```'
}

Write-Output ""
Write-Output "## Read first"
Write-Output ""
Write-Output "- ``AGENTS.md``"
foreach ($doc in $selection.Docs) {
    Write-Output "- ``$doc``"
}

if (-not $NoDocumentText) {
    Write-Output ""
    Write-Output "## Focused documentation"
    $remainingLines = $MaxDocumentLines
    foreach ($doc in $selection.Docs) {
        if ($remainingLines -le 0) {
            break
        }
        $fullPath = Join-Path $repoRoot $doc
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Context document is missing: $doc"
        }
        $lines = @(Get-Content -LiteralPath $fullPath)
        $take = [Math]::Min($remainingLines, $lines.Count)
        Write-Output ""
        Write-Output "### $doc"
        Write-Output ""
        $lines | Select-Object -First $take | Write-Output
        if ($take -lt $lines.Count) {
            Write-Output ""
            Write-Output "[truncated: $($lines.Count - $take) lines]"
        }
        $remainingLines -= $take
    }
}

Write-Output ""
Write-Output "## Relevant paths"
Write-Output ""
foreach ($path in $selection.Paths) {
    Write-Output "- ``$path``"
}

Write-Output ""
Write-Output "## Validation"
Write-Output ""
Write-Output '```powershell'
$selection.Tests | Write-Output
Write-Output '```'

Write-Output ""
Write-Output "## New task seed"
Write-Output ""
Write-Output "Work in this repository. Area: $Area. Read AGENTS.md and only the"
Write-Output "documents listed above. Preserve unrelated dirty-worktree changes. Goal: <task>."
Write-Output "Run listed validation and report completed, partial, and unverified results."
