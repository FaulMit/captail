[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-Text([string]$relativePath) {
    [IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Require([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

$theme = Read-Text "src\Captail\ThemeManager.cs"
$app = Read-Text "src\Captail\App.xaml.cs"
$shortcuts = Read-Text "src\Captail\ShortcutIconManager.cs"
$project = Read-Text "src\Captail\Captail.csproj"

$icons = @(
    "Captail.ico",
    "CaptailBlue.ico",
    "CaptailViolet.ico",
    "CaptailRose.ico",
    "CaptailAmber.ico"
)

foreach ($icon in $icons) {
    $path = Join-Path $repoRoot "src\Captail\Assets\$icon"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing accent icon: $icon"
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 6 -or
        [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1 -or
        [BitConverter]::ToUInt16($bytes, 4) -lt 5) {
        throw "Invalid multi-resolution ICO: $icon"
    }

    Require $project ([Regex]::Escape("Assets\$icon")) `
        "$icon must be packaged as a WPF resource."
    Require $theme ([Regex]::Escape("`"$icon`"")) `
        "$icon must be selectable by ThemeManager."
}

foreach ($window in @(
    "SettingsWindow.xaml",
    "ProcessAudioRoutingWindow.xaml",
    "ClipEditorWindow.xaml"
)) {
    $xaml = Read-Text "src\Captail\$window"
    Require $xaml 'Icon="\{DynamicResource ApplicationIcon\}"' `
        "$window must use dynamic application icon."
}

Require $theme 'resources\["ApplicationIcon"\]\s*=\s*LoadIcon' `
    "Theme changes must replace the WPF application icon."
Require $app 'ThemeManager\.IconAssetName\(_config\?\.AccentColor\)' `
    "Active tray icon must follow selected accent."
Require $app 'ShortcutIconManager\.Apply\(accentName\)' `
    "Accent application must update installed shortcuts."
Require $shortcuts 'AppDistribution\.IsMicrosoftStore' `
    "MSIX shortcut must be excluded because package logos are immutable."
Require $shortcuts 'TargetsCurrentExecutable\(targetPath\)' `
    "Only shortcuts targeting current Captail executable may change."

Write-Host "ACCENT_ICON_REGRESSION_TEST PASS"
