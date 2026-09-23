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

function Assert-NotContains(
    [string]$text,
    [string]$pattern,
    [string]$message
) {
    if ($text -match $pattern) {
        throw $message
    }
}

$config = Read-Text "src\Captail\Config.cs"
$engine = Read-Text "src\Captail\ObsReplayEngine.cs"
$native = Read-Text "src\Captail\ObsNative.cs"
$gpuPreference = Read-Text "src\Captail\GpuPreference.cs"
$obsBridge = Read-Text "native\ObsBridge\CaptailObsBridge.cpp"
$player = Read-Text "src\Captail\MpvHost.cs"
$editor = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$editorXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"
$ffmpeg = Read-Text "src\Captail\FfmpegAdapter.cs"
$library = Read-Text "src\Captail\ReplayLibrary.cs"
$settings = Read-Text "src\Captail\SettingsWindow.xaml.cs"
$settingsXaml = Read-Text "src\Captail\SettingsWindow.xaml"

$pipelineEquals = [regex]::Match(
    $config,
    'public bool PipelineEquals\(Config other\) =>(?<body>[\s\S]*?)\n\s*public void Normalize\(').Groups['body'].Value
Assert-NotContains $pipelineEquals 'OutputDirectory|OrganizeReplaysByGame' `
    "Changing replay destination must not restart the active OBS pipeline."
Assert-Contains $settings 'dialog\.ShowDialog\(this\)' `
    "Replay folder picker must stay owned by the settings window."
Assert-NotContains $settings `
    'player\.ShowDialog\(\);\s*_ = RefreshReplayLibraryAsync\(\);' `
    "Closing the replay player must not refresh an unchanged dashboard library."
Assert-Contains $settings `
    'player\.ShowDialog\(\);\s*if \(player\.HasDeletedReplays\)\s*_ = RefreshReplayLibraryAsync\(\);' `
    "Closing the replay player must refresh only after player-side deletion."
Assert-Contains $settingsXaml `
    '<ListBox x:Name="RecentReplaysList"[\s\S]*?VirtualizingPanel\.IsVirtualizing="True"[\s\S]*?VirtualizingPanel\.VirtualizationMode="Recycling"' `
    "Dashboard replay list must virtualize clip cards through its own ScrollViewer."
Assert-Contains $settings `
    'Lazy<BitmapImage\?>[\s\S]*?public BitmapImage\? Thumbnail => _thumbnail\.Value;' `
    "Dashboard replay thumbnails must decode only when a virtualized row becomes visible."
Assert-Contains $settings `
    'GetRecentAsync\(\s*_outputDirectory,\s*DashboardInitialReplayCount[\s\S]*?SetReplayLibraryState\(empty: clips\.Count == 0\)[\s\S]*?GetRecentAsync\(\s*_outputDirectory,\s*int\.MaxValue' `
    "Dashboard must show newest clips before enriching the complete replay library."
Assert-Contains $library `
    'ConcurrentDictionary<ReplayCacheKey, ReplayClip>[\s\S]*?_clipCache\.TryGetValue\(cacheKey, out ReplayClip\? cached\)' `
    "Unchanged clips must reuse probed metadata across dashboard and player loads."

Assert-Contains $engine '_gameVideoOutputSource' `
    "Game Capture must use a canvas-sized output source."
Assert-Contains $engine 'obs_sceneitem_set_bounds_type\([\s\S]*?BoundsType\.Stretch' `
    "Game Capture must stretch non-native aspect ratios to the OBS canvas."
Assert-Contains $native 'obs_sceneitem_set_bounds\(' `
    "OBS interop must expose scene-item bounds for stretched Game Capture."
Assert-Contains $engine `
    'obs_data_set_bool\(settings,\s*"force_sdr",\s*true\)' `
    "SDR recording must force HDR display capture through SDR conversion."
Assert-Contains $engine `
    'obs_set_video_levels\(\s*ObsSdrWhiteLevel,\s*ObsHdrNominalPeakLevel\)' `
    "HDR Game Capture must initialize OBS tone-mapping levels."
Assert-Contains $engine 'ObsSdrWhiteLevel\s*=\s*300\.0f' `
    "OBS SDR white level must use the standard 300-nit reference."
Assert-Contains $engine 'ObsHdrNominalPeakLevel\s*=\s*1000\.0f' `
    "OBS HDR nominal peak level must use the standard 1000-nit reference."
Assert-Contains $obsBridge 'EnumAdapterByGpuPreference\([\s\S]*?DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE' `
    "Captail must ask DXGI for the high-performance GPU on hybrid systems."
Assert-Contains $gpuPreference 'captail_get_high_performance_adapter_index' `
    "Managed capture startup must consume the native high-performance GPU selection."
Assert-Contains $engine 'Adapter\s*=\s*_adapterIndex' `
    "OBS video must use the selected high-performance adapter instead of adapter zero."
Assert-Contains $engine 'id\s*==\s*_adapterIndex' `
    "Encoder capability detection must describe the same adapter used by OBS video."

Assert-Contains $player 'public void SetVolumePercent\(' `
    "Embedded player needs runtime volume control."
Assert-Contains $player 'SetOption\("hwdec", "d3d11va,auto-safe"\)' `
    "Windows player must prefer direct D3D11 hardware decoding before safe fallback."
Assert-Contains $player 'SetOption\("framedrop", "decoder"\)' `
    "High-frame-rate replay preview must drop late decoded frames under GPU pressure."
Assert-NotContains $player 'Preview one selected track' `
    "Preview must not silently discard selected audio tracks."
Assert-Contains $player 'amix=inputs=\{ids\.Length\}' `
    "Player must mix every selected audio track during preview."
$loadBody = [regex]::Match(
    $player,
    'public async Task LoadAsync\((?<body>[\s\S]*?)\n\s*public void Play\(').Groups['body'].Value
$clearGraphIndex = $loadBody.IndexOf('SetProperty("lavfi-complex", "")')
$loadFileIndex = $loadBody.IndexOf('Command("loadfile"')
if ($clearGraphIndex -lt 0 -or $loadFileIndex -lt 0 -or
    $clearGraphIndex -gt $loadFileIndex) {
    throw "Player must clear stale audio graph before loading another replay."
}
Assert-Contains $editor 'GetPreviewProxyAsync' `
    "Replay preview must have a decoder fallback for unsupported AV1 captures."
Assert-Contains $editor 'Key\.Add|Key\.OemPlus' `
    "Player must handle plus keys for volume up."
Assert-Contains $editor 'Key\.Subtract|Key\.OemMinus' `
    "Player must handle minus keys for volume down."
Assert-Contains $editorXaml 'L\.Library\.ShortcutVolume' `
    "Player help popup must document volume keys."
Assert-Contains $editorXaml `
    'x:Name="PlayerHelpButton"[\s\S]*?x:Name="PlayerHelpPopup"[\s\S]*?L\.Library\.ShortcutPlayback[\s\S]*?L\.Library\.ShortcutCloseHint' `
    "Player shortcuts must live behind the dedicated Help button."
Assert-Contains $editorXaml `
    'x:Name="PlayerHelpButton"[\s\S]*?Height="30"[\s\S]*?Foreground="\{DynamicResource AccentBrush\}"[\s\S]*?Background="\{DynamicResource AccentSubtleBrush\}"' `
    "Player Help action must remain compact and accent-colored."
Assert-Contains $editor `
    'TimelineEditorPanel\.Visibility = Visibility\.Visible;[\s\S]*?EditorActionsPanel\.Visibility = Visibility\.Visible;[\s\S]*?PreviewModePanel\.Visibility = Visibility\.Collapsed;' `
    "Player must always show the full trim timeline, waveform tracks, and actions."
Assert-NotContains $settingsXaml 'Click="TrimReplay_Click"' `
    "Dashboard clip cards must not expose a separate trim action."
Assert-NotContains $settings 'private void TrimReplay_Click' `
    "Dashboard must open trimming through the unified player only."
Assert-NotContains $editorXaml 'Click="EnterTrimMode_Click"' `
    "Unified player must not expose a separate trim-mode transition."
Assert-Contains $editor `
    '_ = LoadTimelineThumbnailsAsync\(\);[\s\S]*?LoadAudioTracksAsync\(loadWaveforms: true\)[\s\S]*?InitializeOutputSettings\(\);' `
    "Player must load the same trim timeline, waveforms, and output settings immediately."
Assert-Contains $editorXaml 'shell:WindowChrome\.WindowChrome' `
    "Resizable borderless player must own its non-client frame."
Assert-Contains $editorXaml 'ResizeBorderThickness="6"' `
    "Player must retain an invisible resize hit target."
Assert-Contains $editorXaml 'GlassFrameThickness="0"' `
    "Player must suppress the native light frame."
Assert-NotContains $editor 'DwmwaBorderColor' `
    "Player must not depend on unsupported DWM border-color attributes."
Assert-Contains $editor 'WmEnterSizeMove' `
    "Player must suppress the native mpv child during interactive resize."
Assert-Contains $editor 'BeginInteractiveResize' `
    "Player must enter a flicker-free interactive-resize state."
Assert-Contains $editor 'EndInteractiveResize' `
    "Player must restore the native mpv child after interactive resize."
Assert-Contains $player 'WmEraseBackground' `
    "Native mpv host must suppress background erasing during resize."
Assert-Contains $player 'WsClipChildren' `
    "Native mpv host must clip its render child during resize."
Assert-Contains $player 'WsClipSiblings' `
    "Native mpv host must not repaint over sibling WPF content."
Assert-Contains $player 'WmLButtonUp\s*=\s*0x0202' `
    "Native mpv host must recognize direct clicks on the preview window."
Assert-Contains $player 'message\s*==\s*WmLButtonUp' `
    "Direct preview-window clicks must toggle playback."
Assert-Contains $player `
    'RaiseNativeMouseLeftButtonForQa\(\)[\s\S]*?WmLButtonUp' `
    "Replay navigation QA must exercise a direct preview-window click."
Assert-Contains $editorXaml `
    'x:Name="OutputSettingsButton"[\s\S]*?IconGear[\s\S]*?L\.Library\.Cancel' `
    "Editor settings gear must remain immediately left of Cancel."
Assert-Contains $editorXaml `
    'x:Name="OutputSettingsPopup"[\s\S]*?VideoCodecComboBox[\s\S]*?AudioCodecComboBox[\s\S]*?ResolutionComboBox[\s\S]*?BitRateComboBox[\s\S]*?MergeAudioCheckBox' `
    "Editor settings popup must own codec, resolution, bitrate, and audio merge controls."
Assert-Contains $editor 'ExportTranscodedAsync\(' `
    "Editor export action must use the transcoding pipeline."
Assert-Contains $editor 'CurrentOutputSettings\(\)' `
    "Trim and overwrite must consume current output settings."
Assert-Contains $editor `
    'OutputSettingsPopup\.Child\?\.IsMouseOver\s*==\s*true' `
    "Clicks inside output settings must not close the popup."
Assert-Contains $editor `
    'IsDescendantOrSelf\(source,\s*OutputSettingsPopup\.Child\)' `
    "Popup descendants must be excluded from outside-click closing."
Assert-Contains $library 'public async Task<string> ExportTranscodedAsync\(' `
    "Replay library must validate transcoded exports."
Assert-Contains $ffmpeg 'public async Task TranscodeAsync\(' `
    "FFmpeg adapter must expose transcoded export."
Assert-Contains $ffmpeg '"-c:v", "libopenh264"' `
    "MP4, MKV, and MOV exports must encode compatible H.264 video."
Assert-Contains $ffmpeg '"-c:v", "libvpx-vp9"' `
    "WebM exports must encode VP9 video."
Assert-Contains $ffmpeg 'File\.Move\(temporaryPath, destinationPath, overwrite: true\)' `
    "Confirmed exports must replace existing destinations only after FFmpeg succeeds."

Get-ChildItem (Join-Path $repoRoot "src\Captail\Languages") `
    -Filter "Strings.*.xaml" |
    ForEach-Object {
        $language = [IO.File]::ReadAllText($_.FullName)
        Assert-Contains $language 'x:Key="L\.Library\.ShortcutVolume"' `
            "$($_.Name) must localize player volume control."
        Assert-Contains $language 'x:Key="L\.Library\.ShortcutVolumeHint"' `
            "$($_.Name) must localize player volume hint."
        Assert-Contains $language 'x:Key="L\.Library\.Help"' `
            "$($_.Name) must localize the player Help action."
        Assert-Contains $language 'x:Key="L\.Library\.ExportVideo"' `
            "$($_.Name) must localize video export."
        Assert-Contains $language 'x:Key="L\.Library\.OutputSettings"' `
            "$($_.Name) must localize editor output settings."
        Assert-Contains $language 'x:Key="L\.Library\.CurrentValue"' `
            "$($_.Name) must localize source-value defaults."
    }

Write-Host "CAPTURE_PLAYER_REGRESSION_TEST PASS"
