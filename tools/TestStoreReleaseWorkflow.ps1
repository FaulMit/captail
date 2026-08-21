$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repoRoot ".github\workflows\store-release.yml"
$workflow = Get-Content -LiteralPath $workflowPath -Raw

if ($workflow -match '(?m)^\s*package_revision:') {
    throw "Store workflow must not expose the reserved MSIX revision field."
}
if ($workflow -match 'STORE_PACKAGE_REVISION|PackageRevision') {
    throw "Store workflow must keep the reserved MSIX revision field at zero."
}
if ($workflow -notmatch "STORE_IDENTITY_VERSION -notmatch '\^\\d\+\\\.\\d\+\\\.\\d\+\\\.0\$'") {
    throw "Store workflow must reject an MSIX identity whose fourth part is not zero."
}
if ($workflow -notmatch [regex]::Escape('MSIX identity version: ``$identityVersion``')) {
    throw "Store workflow must report the validated MSIX identity version."
}

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
