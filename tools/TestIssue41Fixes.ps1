param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -match $Pattern) {
        throw $Message
    }
}

$root = [System.IO.Path]::GetFullPath($ProjectRoot)
$settingsXaml = Get-Content -Raw (Join-Path $root 'src\Captail\SettingsWindow.xaml')
$settingsCode = Get-Content -Raw (Join-Path $root 'src\Captail\SettingsWindow.xaml.cs')
$theme = Get-Content -Raw (Join-Path $root 'src\Captail\Themes\Theme.xaml')
$notificationXaml = Get-Content -Raw (Join-Path $root 'src\Captail\OverlayNotificationWindow.xaml')
$editorXaml = Get-Content -Raw (Join-Path $root 'src\Captail\ClipEditorWindow.xaml')
$editorCode = Get-Content -Raw (Join-Path $root 'src\Captail\ClipEditorWindow.xaml.cs')
$engineCode = Get-Content -Raw (Join-Path $root 'src\Captail\ObsReplayEngine.cs')
$appCode = Get-Content -Raw (Join-Path $root 'src\Captail\App.xaml.cs')
$project = Get-Content -Raw (Join-Path $root 'src\Captail\Captail.csproj')

Assert-Contains $theme '<Style x:Key="FooterAboutButton"[\s\S]*?<Setter Property="Margin" Value="0,3"/>' `
    'Issue #41.1: About button needs vertical footer inset.'
Assert-Contains $settingsXaml '<Popup x:Name="AboutPopup"[\s\S]*?StaysOpen="True"' `
    'Issue #41.1: About popup must not auto-close before its toggle click is processed.'
Assert-NotContains $settingsXaml 'Deactivated="Window_Deactivated"' `
    'Issue #41.1: window deactivation closes the popup before link clicks are delivered.'
Assert-NotContains $settingsCode 'Window_Deactivated[\s\S]*?AboutPopup\.IsOpen = false' `
    'Issue #41.1: About popup must not close from the window-deactivation event.'

Assert-Contains $notificationXaml '<Border x:Name="Card"[\s\S]*?Margin="12"' `
    'Issue #41.2: notification shadow needs transparent inset around the rounded card.'
Assert-Contains $notificationXaml 'x:Name="NotificationIcon"[\s\S]*?Margin="0,-2,0,0"' `
    'Issue #41.2: notification icon needs optical vertical centering.'

Assert-Contains $editorCode 'BufferingIndicatorDelay' `
    'Issue #41.3: delayed buffering indicator is missing.'
Assert-Contains $editorCode '_bufferingSinceUtc' `
    'Issue #41.3: sustained buffering state is not tracked.'
Assert-Contains $editorXaml '<Border x:Name="WindowChrome"[\s\S]*?CornerRadius="0"' `
    'Issue #41.3/4: editor must use one native outer-corner layer.'
Assert-Contains $editorXaml '<Style x:Key="PlayerShortcutCell"[\s\S]*?<Setter Property="Padding" Value="12,7"/>' `
    'Issue #41.3: preview hotkey rows still overflow the fixed window height.'

Assert-Contains $engineCode 'BuildAudioTrackName' `
    'Issue #41.4: recorded audio streams need stable meaningful names.'
Assert-Contains $editorCode 'IsGenericAudioTitle' `
    'Issue #41.4: editor needs replay-metadata-aware track labels.'
Assert-Contains $editorCode 'SelectedAudioStreamIndices\(\)' `
    'Issue #41.4: selected editor tracks are not passed into trim output.'

Assert-Contains $editorXaml 'x:Name="EditorTransportControls"' `
    'Issue #41.5: editor transport controls were not moved below the preview.'
Assert-NotContains $editorXaml 'x:Name="ClipInfoText"[\s\S]{0,300}MaxWidth="340"' `
    'Issue #41.5: clip metadata still has the old truncating width cap.'

Assert-Contains $project '<Resource Include="Assets\\CaptailInactive.ico"' `
    'Issue #41.6: inactive tray icon is not packaged.'
Assert-Contains $appCode '_tray\.Icon = CreateIcon' `
    'Issue #41.6: tray icon does not follow replay state.'

Write-Output 'ISSUE41_TEST PASS: all six regression contracts are present.'
