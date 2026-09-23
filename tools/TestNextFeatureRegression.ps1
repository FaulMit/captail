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
$app = Read-Text "src\Captail\App.xaml.cs"
$config = Read-Text "src\Captail\Config.cs"
$engine = Read-Text "src\Captail\ObsReplayEngine.cs"
$obsNative = Read-Text "src\Captail\ObsNative.cs"
$manifest = Read-Text "packaging\msix\AppxManifest.xml.template"
$player = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$playerXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"
$library = Read-Text "src\Captail\ReplayLibrary.cs"
$mpvHost = Read-Text "src\Captail\MpvHost.cs"
$indicator = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml.cs"
$indicatorXaml = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml"

Reject $settings `
    'Window_Deactivated[\s\S]{0,180}AboutPopup\.IsOpen\s*=\s*false' `
    "About popup closes before its external-link buttons can click."
Require $settings 'ExternalLinkLauncher\.OpenAsync' `
    "Footer links must use one testable shell launcher."

Require $engine `
    'WindowsGraphicsCaptureMinimumBuild\s*=\s*18362' `
    "Desktop capture is missing the Windows 10 1903 WGC support boundary."
Require $engine `
    'osVersion\.Build\s*>=\s*WindowsGraphicsCaptureMinimumBuild[\s\S]{0,100}\?\s*MonitorCaptureMethodWgc[\s\S]{0,100}:\s*MonitorCaptureMethodAuto' `
    "Desktop capture must prefer WGC on Windows 10 1903+ and retain Auto fallback for older systems."
Require $engine `
    'windowsGraphicsCaptureReady\s*&&[\s\S]{0,180}MonitorCaptureMethodWgc' `
    "Desktop capture must fall back to Auto when WGC preflight cannot complete."
Require $app `
    'GraphicsCaptureAccess\.RequestAccessAsync\([\s\S]{0,100}GraphicsCaptureAccessKind\.Borderless' `
    "Windows 11 must request borderless WGC access on the UI thread before OBS starts."
Require $engine `
    '_preferredMonitorCaptureMethod\s*==\s*MonitorCaptureMethodWgc[\s\S]{0,100}\?\s*MonitorCaptureMethodAuto' `
    "Desktop capture must start on Auto before switching the live source to WGC."
Require $engine `
    'obs_source_update\(_desktopVideoSource,\s*settings\)' `
    "Desktop capture must switch the existing source to WGC without restarting output."
Require $obsNative 'extern void obs_source_update\(nint source, nint settings\)' `
    "libobs source update interop is missing."
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
Require $engine `
    'ResetObsVideo\(\s*IsGameCapture\s*&&\s*!IsContinuousRecording\s*\?\s*GameCaptureIdleFrameRate\s*:\s*_config\.FrameRate' `
    "Continuous Game Capture recording must use the configured frame rate instead of the idle detector rate."
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
Require $settingsXaml `
    'x:Name="SaveReplayButton"[\s\S]{0,500}<ColumnDefinition Width="\*"/>\s*<ColumnDefinition Width="Auto"/>\s*<ColumnDefinition Width="\*"/>' `
    "Save button content must stay centered when hotkey text width changes."
Require $settingsXaml `
    'Text="\{Binding Title\}"[\s\S]{0,160}Foreground="\{StaticResource TextPrimaryBrush\}"' `
    "Recent replay titles must use readable primary text color."

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
Require $playerXaml `
    'x:Name="PreviousReplayButton"[\s\S]*?Click="Back_Click"[\s\S]*?Click="Forward_Click"[\s\S]*?x:Name="NextReplayButton"' `
    "Replay player must place Previous left of seek controls and Next on the right."
Require $playerXaml `
    'x:Name="EditorTransportControls"[\s\S]{0,400}x:Name="EditorPreviousReplayButton"[\s\S]*?Click="Back_Click"[\s\S]*?Click="Forward_Click"[\s\S]*?x:Name="EditorNextReplayButton"' `
    "Visible editor transport must include Previous and Next clip buttons around seek controls."
Require $playerXaml `
    'x:Name="NormalPlaybackBar"[\s\S]{0,5000}x:Name="EditorVolumeSlider"' `
    "Visible editor playback bar must include a volume slider."
Require $player `
    'SetReplayNavigationButtonsEnabled\(canNavigatePrevious, canNavigateNext\)' `
    "Visible editor navigation buttons must track available previous and next clips."
Require $player `
    'item\.IsActive\s*=\s*string\.Equals\([\s\S]{0,180}_clip\.Path[\s\S]*?RecentReplayList\.UnselectAll\(\)' `
    "Replay list active state must follow the clip loaded in the player."
Require $playerXaml `
    'DataTrigger Binding="\{Binding IsActive\}" Value="True"[\s\S]{0,260}AccentSubtleBrush[\s\S]{0,180}AccentDimBrush' `
    "Replay highlight must bind to active playback state instead of list focus."
Require $player `
    'canNavigatePrevious\s*=\s*_currentReplayIndex\s*>\s*0[\s\S]*?canNavigateNext\s*=\s*_currentReplayIndex\s*>=\s*0\s*&&[\s\S]*?_currentReplayIndex\s*<\s*RecentReplayItems\.Count\s*-\s*1' `
    "Previous must move up and Next must move down the visible replay list."
Require $player `
    'PreviousReplay_Click[\s\S]{0,320}RecentReplayItems\[_currentReplayIndex\s*-\s*1\][\s\S]*?NextReplay_Click[\s\S]{0,320}RecentReplayItems\[_currentReplayIndex\s*\+\s*1\]' `
    "Replay navigation handlers must follow visible list order."
Reject $player `
    'RecentReplay_Click[\s\S]{0,420}TogglePlaybackAsync\(\)' `
    "Clicking the active replay row must not pause playback."
Reject $player `
    'LoadReplayAsync\(ReplayClip clip\)[\s\S]{0,180}!_previewMode' `
    "Replay navigation must work from the visible editor, not only Preview mode."
Require $player `
    'LoadReplayAsync\(ReplayClip clip\)[\s\S]{0,500}SetReplayNavigationButtonsEnabled\(false, false\)[\s\S]*?finally[\s\S]{0,300}UpdateReplayNavigationState\(\)' `
    "Replay navigation must disable buttons while loading and restore them in finally."
Require $player `
    '_\s*=\s*CompleteReplayEnrichmentAsync\(clip, replayLoadVersion\)' `
    "Replay navigation must not wait for timeline enrichment before becoming interactive."
Require $player `
    'CompleteReplayEnrichmentAsync[\s\S]{0,500}Task videoInfoTask\s*=\s*LoadVideoInfoAsync[\s\S]*?_\s*=\s*LoadTimelineThumbnailsAsync[\s\S]*?await videoInfoTask[\s\S]*?InitializeOutputSettings\(\)' `
    "Output settings must initialize after video metadata without waiting for timeline thumbnails."
Require $player `
    'ObservableCollection<BitmapImage\?> _timelineImages[\s\S]*?TimelineFrameCount[\s\S]*?ShowThumbnail[\s\S]*?_timelineImages\[index\]\s*=\s*LoadBitmap' `
    "Timeline thumbnails must appear progressively instead of waiting for the full strip."
Require $player `
    'Interlocked\.Exchange\([\s\S]{0,120}ref _timelineLoadCts[\s\S]{0,120}previousTimelineCts\?\.Cancel\(\)' `
    "Opening another replay must cancel stale timeline generation."
Require $library `
    'SemaphoreSlim _timelineThumbnailGate\s*=\s*new\(3, 3\)[\s\S]*?Task\.WhenAll\(pending\)' `
    "Missing timeline thumbnails must use bounded parallel generation."
Require $playerXaml `
    'IconSkipPrevious[\s\S]{0,220}Fill="\{Binding Foreground[\s\S]*?IconSkipNext[\s\S]{0,220}Fill="\{Binding Foreground' `
    "Replay navigation must use filled previous/next clip icons."
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
