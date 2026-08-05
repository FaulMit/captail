[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$DumpbinPath = ""
)

$ErrorActionPreference = "Stop"
$packageRoot = [IO.Path]::GetFullPath($PackageRoot)
$manifestPath = Join-Path $packageRoot "AppxManifest.xml"
$vclibsName = "Microsoft.VCLibs.140.00.UWPDesktop"
$vclibsPublisher =
    "CN=Microsoft Corporation, O=Microsoft Corporation, " +
    "L=Redmond, S=Washington, C=US"
$minimumVclibsVersion = [Version]"14.0.33728.0"

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Store manifest not found: $manifestPath"
}

if (-not $DumpbinPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} `
        "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $DumpbinPath = & $vswhere `
            -latest `
            -products '*' `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' |
            Select-Object -Last 1
    }
}
if (-not $DumpbinPath -or
    -not (Test-Path -LiteralPath $DumpbinPath -PathType Leaf)) {
    throw "dumpbin.exe not found. Install MSVC x64 build tools or pass -DumpbinPath."
}

$runtimeImports = [Collections.Generic.SortedSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$runtimeUsers = [Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object { $_.Extension -in ".exe", ".dll" } |
    ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($packageRoot, $_.FullName)
        $dependencies = & $DumpbinPath /dependents $_.FullName 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "dumpbin failed for Store binary: $relativePath"
        }

        $fileUsesRuntime = $false
        foreach ($line in $dependencies) {
            if ($line -match `
                '^\s+(?<name>VCRUNTIME140(?:_1)?|MSVCP140)\.dll\s*$') {
                [void]$runtimeImports.Add("$($Matches.name).dll")
                $fileUsesRuntime = $true
            }
        }
        if ($fileUsesRuntime) {
            $runtimeUsers.Add($relativePath)
        }
    }

if ($runtimeImports.Count -eq 0) {
    Write-Host "PASS: Store native binaries do not import dynamic Visual C++ Runtime libraries."
    exit 0
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$dependency = @($manifest.Package.Dependencies.PackageDependency) |
    Where-Object { $_.Name -eq $vclibsName } |
    Select-Object -First 1
if ($null -eq $dependency) {
    throw (
        "Store package imports $($runtimeImports -join ', ') but manifest " +
        "does not declare $vclibsName. Runtime users: " +
        ($runtimeUsers -join ", ")
    )
}
if ($dependency.Publisher -ne $vclibsPublisher) {
    throw "Store VCLibs dependency publisher is invalid: $($dependency.Publisher)"
}

$declaredVersion = [Version]$dependency.MinVersion
if ($declaredVersion -lt $minimumVclibsVersion) {
    throw (
        "Store VCLibs dependency is too old: $declaredVersion. " +
        "Required minimum: $minimumVclibsVersion."
    )
}

Write-Host (
    "PASS: Store manifest declares $vclibsName $declaredVersion for " +
    "$($runtimeUsers.Count) native runtime users."
)
