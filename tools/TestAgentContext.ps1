[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$contextScript = Join-Path $PSScriptRoot "GetAgentContext.ps1"

$requiredFiles = @(
    "AGENTS.md",
    "docs/agent/INDEX.md",
    "docs/agent/CURRENT.md",
    "docs/agent/ARCHITECTURE.md",
    "docs/agent/WORKFLOWS.md",
    "docs/agent/KNOWN_ISSUES.md",
    "docs/agent/CAPTURE.md",
    "docs/agent/AUDIO.md",
    "docs/agent/PLAYER.md",
    "docs/agent/UI.md",
    "docs/agent/DISTRIBUTION.md",
    "docs/agent/decisions/0001-app-owns-capture-lifecycle.md",
    "docs/agent/decisions/0002-advanced-audio-is-optional.md",
    "docs/agent/decisions/0003-release-channels-are-independent.md",
    "docs/agent/decisions/0004-progressive-agent-context.md",
    "tools/GetAgentContext.ps1"
)

foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath))) {
        throw "Missing agent context file: $relativePath"
    }
}

$agentGuide = @(Get-Content -LiteralPath (Join-Path $repoRoot "AGENTS.md"))
if ($agentGuide.Count -gt 90) {
    throw "AGENTS.md is too large for always-on context: $($agentGuide.Count) lines."
}
if (($agentGuide -join "`n").Length -gt 5000) {
    throw "AGENTS.md is too large for always-on context."
}

$expectations = [ordered]@{
    overview = "docs/agent/ARCHITECTURE.md"
    capture = "src/Captail/ObsReplayEngine.cs"
    audio = "src/Captail/ProcessAudioReconciler.cs"
    player = "src/Captail/MpvHost.cs"
    ui = "src/Captail/SettingsWindow.xaml"
    distribution = "src/Captail/AppDataPaths.cs"
    release = "docs/RELEASING.md"
}

foreach ($entry in $expectations.GetEnumerator()) {
    $output = @(
        & $contextScript `
            -Area $entry.Key `
            -MaxStatusEntries 5 `
            -MaxDocumentLines 140
    )
    $text = $output -join "`n"
    if ($text -notmatch [regex]::Escape("# Captail task context: $($entry.Key)")) {
        throw "Area $($entry.Key) has no context header."
    }
    if ($text -notmatch [regex]::Escape($entry.Value)) {
        throw "Area $($entry.Key) does not route to $($entry.Value)."
    }
    if ($text -notmatch "## Validation" -or $text -notmatch "## New task seed") {
        throw "Area $($entry.Key) omits validation or task seed."
    }
    if ($output.Count -gt 330) {
        throw "Area $($entry.Key) context is unbounded: $($output.Count) lines."
    }
    if ($text -match "diff --git") {
        throw "Area $($entry.Key) leaked a full Git diff."
    }
}

$minimal = @(
    & $contextScript `
        -Area player `
        -MaxStatusEntries 5 `
        -NoDocumentText
)
$minimalText = $minimal -join "`n"
if ($minimal.Count -gt 90 -or $minimalText -match "## Focused documentation") {
    throw "Minimal context mode is not minimal: $($minimal.Count) lines."
}

Write-Host "AGENT_CONTEXT_TEST PASS: 7 bounded routes verified."
