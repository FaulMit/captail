[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-Text([string]$relativePath) {
    return [IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Assert-Contains(
    [string]$text,
    [string]$pattern,
    [string]$message
) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

$app = Read-Text "src\Captail\App.xaml.cs"
$config = Read-Text "src\Captail\Config.cs"
$indicator = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml.cs"
$settings = Read-Text "src\Captail\SettingsWindow.xaml"

Assert-Contains $config 'ReplayEnabled\s*\{\s*get;\s*set;\s*\}\s*=\s*false' `
    "Fresh installations must start with replay disabled."
Assert-Contains $app `
    'await PrepareCapturePermissionsAtStartupAsync\(backgroundLaunch\);\s*if\s*\(_config\.ReplayEnabled' `
    "Startup must prepare permissions before checking whether replay is enabled."

Assert-Contains $app `
    '_tray\.ForceCreate\(enablesEfficiencyMode:\s*false\)' `
    "Background recording must not enable H.NotifyIcon Efficiency Mode."
Assert-Contains $app `
    'SetCurrentProcessExplicitAppUserModelID' `
    "Portable builds need a stable shell identity after sign-in."
Assert-Contains $indicator `
    'ContentRendered\s*\+=' `
    "Recording indicator capture protection must wait for its first frame."
Assert-Contains $indicator `
    'if\s*\(!_firstFrameRendered\)\s*return;' `
    "Recording indicator must not apply capture affinity before rendering."
Assert-Contains $settings `
    'Icon="\{DynamicResource ApplicationIcon\}"' `
    "Main window must use the accent-aware application icon resource."

Write-Host "STARTUP_UI_SURFACES_TEST PASS"
