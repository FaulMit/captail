[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-Text([string]$relativePath) {
    Get-Content -LiteralPath (Join-Path $root $relativePath) -Raw
}

function Require([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

function Reject([string]$text, [string]$pattern, [string]$message) {
    if ($text -match $pattern) {
        throw $message
    }
}

$settings = Read-Text "src\Captail\SettingsWindow.xaml.cs"
$settingsXaml = Read-Text "src\Captail\SettingsWindow.xaml"
$config = Read-Text "src\Captail\Config.cs"
$engine = Read-Text "src\Captail\ObsReplayEngine.cs"
$manifest = Read-Text "packaging\msix\AppxManifest.xml.template"
$player = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$playerXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"
$mpvHost = Read-Text "src\Captail\MpvHost.cs"
$indicator = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml.cs"
$indicatorXaml = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml"

Reject $settings `
    'Window_Deactivated[\s\S]{0,180}AboutPopup\.IsOpen\s*=\s*false' `
    "About popup closes before its external-link buttons can click."
Require $settings 'ExternalLinkLauncher\.OpenAsync' `
    "Footer links must use one testable shell launcher."

Require $engine `
    'RecommendedMonitorCaptureMethod\(Version osVersion\)\s*=>\s*MonitorCaptureMethodAuto' `
    "Desktop capture must prefer DXGI/Auto instead of forcing bordered WGC."
Require $manifest 'xmlns:uap11="http://schemas\.microsoft\.com/appx/manifest/uap/windows10/11"' `
    "Store manifest is missing uap11 namespace."
Require $manifest '<uap11:Capability Name="graphicsCaptureWithoutBorder"\s*/>' `
    "Store package is missing borderless-capture capability."

Require $config 'CaptureMode\s*\{\s*get;\s*set;\s*\}\s*=\s*"replay"' `
    "Replay must remain default capture mode."
Require $engine 'CreateRecordingOutput' `
    "OBS pipeline has no continuous-recording output."
Require $engine 'ffmpeg_muxer' `
    "Continuous recording must use file output, not a replay-buffer workaround."
Require $settingsXaml 'x:Name="DashboardCaptureModeOptions"' `
    "Dashboard is missing Replay/Recording mode selector."
Require $settings 'DashboardCaptureMode_Click' `
    "Dashboard mode selector is not connected to runtime settings apply."
$modeSwitch = [regex]::Match(
    $settings,
    'DashboardCaptureMode_Click(?<body>[\s\S]*?)\n\s*private async void SourceChip_Click').Groups['body'].Value
Require $modeSwitch 'candidate\.ReplayEnabled\s*=\s*false' `
    "Switching Replay/Recording mode must always leave capture disabled."
Require $modeSwitch 'UpdateRuntimeState\(false\)' `
    "Mode switch must immediately render the disabled runtime state."
Reject $settingsXaml 'x:Name="CaptureModeBox"' `
    "Capture mode selector must not remain buried in Settings."
Reject $settings 'CaptureModeBox' `
    "Settings code still depends on the removed capture mode ComboBox."
Require $settingsXaml 'x:Name="ReplayOnlySettings"' `
    "Replay-only buffer controls cannot be hidden in Recording mode."
Require $settingsXaml 'x:Name="ReplayToggleHotkeySettings"' `
    "Recording mode still exposes a duplicate replay-toggle hotkey."

Require $config 'AccentColor\s*\{\s*get;\s*set;\s*\}' `
    "Accent choice is not persisted."
Require $settingsXaml 'x:Name="AccentColorOptions"' `
    "Accent selector is missing."

Require $playerXaml 'ResizeMode="CanResize"' `
    "Replay player is still fixed-size."
Require $playerXaml 'PreviewMouseWheel="PreviewSurface_MouseWheel"' `
    "Mouse wheel volume is missing from video surface."
Require $playerXaml 'x:Name="RecentReplayList"' `
    "Recent replay sidebar is missing."
Require $player 'GetRecentAsync\(\s*_rootDirectory,\s*int\.MaxValue' `
    "Replay player sidebar must load every clip instead of only the newest 12."
Require $playerXaml 'x:Name="DeleteRecentReplayButton"[\s\S]*?RequestDeleteRecentReplay_Click' `
    "Replay sidebar needs a delete action on every clip."
Require $playerXaml 'DeleteRecentReplayButton[\s\S]*?AncestorType=ListBoxItem[\s\S]*?Visibility' `
    "Replay delete action must appear when its clip row is hovered."
Require $player 'ConfirmDeleteRecentReplay_Click' `
    "Replay sidebar delete action needs confirmation handling."
Require $player 'LoadReplayAsync' `
    "Player cannot replace current clip for previous/next navigation."
Require $player 'PreviousReplay_Click' `
    "Previous replay action is missing."
Require $player 'NextReplay_Click' `
    "Next replay action is missing."
Require $playerXaml 'x:Name="NextReplayButton"[\s\S]*?x:Name="PreviousReplayButton"' `
    "Replay player must place Next on the left and Previous on the right."
Require $mpvHost 'NativeMouseLeftButton' `
    "Embedded native video host must forward left-click input."
Require $player 'PreviewPlayer\.NativeMouseLeftButton' `
    "Replay player must toggle playback from native video clicks."
Require $config 'PlayerVolume\s*\{\s*get;\s*set;\s*\}' `
    "Replay player volume must be persisted in Config."
Require $config 'PlayerVolume\s*=\s*source\.PlayerVolume' `
    "Config cloning must preserve replay player volume."
Require $config 'PlayerVolume\s*=\s*Math\.Clamp\(PlayerVolume,\s*0,\s*100\)' `
    "Persisted replay player volume must be normalized."
Require $playerXaml 'x:Name="PreviewVolumeSlider"' `
    "Replay player transport is missing its volume slider."
Require $indicator 'TryFindResource\("AccentBrush"\)' `
    "Recording indicator active state must use selected accent color."
Require $indicatorXaml 'StateRing[\s\S]*?DynamicResource AccentBrush' `
    "Recording indicator XAML must track accent resource changes."

Get-ChildItem (Join-Path $root "src\Captail\Languages") `
    -Filter "Strings.*.xaml" |
    ForEach-Object {
        $language = [IO.File]::ReadAllText($_.FullName)
        Require $language 'x:Key="L\.Library\.AllClips"' `
            "$($_.Name) is missing the all-clips sidebar title."
    }

Write-Host "NEXT_FEATURE_REGRESSION PASS"
