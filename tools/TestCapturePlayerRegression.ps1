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
$player = Read-Text "src\Captail\MpvHost.cs"
$editor = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$editorXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"
$settings = Read-Text "src\Captail\SettingsWindow.xaml.cs"

$pipelineEquals = [regex]::Match(
    $config,
    'public bool PipelineEquals\(Config other\) =>(?<body>[\s\S]*?)\n\s*public void Normalize\(').Groups['body'].Value
Assert-NotContains $pipelineEquals 'OutputDirectory|OrganizeReplaysByGame' `
    "Changing replay destination must not restart the active OBS pipeline."
Assert-Contains $settings 'dialog\.ShowDialog\(this\)' `
    "Replay folder picker must stay owned by the settings window."

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
    "Player shortcut panel must document volume keys."
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

Get-ChildItem (Join-Path $repoRoot "src\Captail\Languages") `
    -Filter "Strings.*.xaml" |
    ForEach-Object {
        $language = [IO.File]::ReadAllText($_.FullName)
        Assert-Contains $language 'x:Key="L\.Library\.ShortcutVolume"' `
            "$($_.Name) must localize player volume control."
        Assert-Contains $language 'x:Key="L\.Library\.ShortcutVolumeHint"' `
            "$($_.Name) must localize player volume hint."
    }

Write-Host "CAPTURE_PLAYER_REGRESSION_TEST PASS"
