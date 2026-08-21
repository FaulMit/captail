$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repoRoot ".github\workflows\store-release.yml"
$workflow = Get-Content -LiteralPath $workflowPath -Raw

function Get-RequiredIndex {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    $index = $workflow.IndexOf($Text, [StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "Store release workflow is missing required text: $Text"
    }

    return $index
}

$uploadIndex = Get-RequiredIndex "- name: Upload package as Partner Center draft"
$submitIndex = Get-RequiredIndex "- name: Submit draft for certification"
$handoffIndex = Get-RequiredIndex "- name: Verify certification handoff"
$pollIndex = Get-RequiredIndex "msstore submission poll"
$summaryIndex = Get-RequiredIndex "- name: Report submission status"

if ($uploadIndex -ge $submitIndex) {
    throw "Store package upload must happen before certification submission."
}
if ($submitIndex -ge $handoffIndex -or $handoffIndex -ge $summaryIndex) {
    throw "Certification handoff must be verified after submission and before success is reported."
}
if ($pollIndex -lt $handoffIndex -or $pollIndex -ge $summaryIndex) {
    throw "The Partner Center poll must run inside the certification handoff gate."
}

Write-Host "Store release workflow waits for Partner Center certification handoff before reporting success."
