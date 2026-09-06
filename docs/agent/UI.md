# UI area

## Scope

Dashboard, settings, replay library, localization, accent themes, hotkeys,
overlay notifications, and recording indicator.

## Structure

- `SettingsWindow`: main dashboard and staged settings editor.
- `ProcessAudioRoutingWindow`: advanced audio assignment.
- `ClipEditorWindow`: preview and trim UI; see `PLAYER.md`.
- `OverlayNotificationWindow`: click-through transient notifications.
- `ReplayStatusIndicatorWindow`: optional capture status surface.
- `DisplayIdentifierWindow`: numbered display overlay.
- `Themes/Theme.xaml` and `ThemeManager`: shared resources and accent selection.
- `Languages/Strings.*.xaml` and `Localization`: dynamic localized strings.

## Invariants

- Settings edits are staged. Done applies; Cancel discards. Closing dirty
  settings shows the save/discard notice and keeps the window open.
- Main replay/recording selector applies immediately and shows only controls
  relevant to the selected mode.
- Every localization key exists in all dictionaries with matching placeholders.
- Public repository UI copy is authored in English, then localized.
- Accent changes use shared dynamic resources, not isolated hard-coded colors.
- About links use the packaged-safe external URI launcher.
- Overlay windows never activate, capture pointer input, or appear in taskbar.

## Validation

```powershell
.\tools\TestReportedUiRuntimeRegressions.ps1
.\tools\TestStartupUiSurfaces.ps1
.\tools\TestLocalizedLayout.ps1
.\tools\TestNextFeatureRegression.ps1
dotnet build .\Captail.sln -c Release --no-restore
```

For visual changes, inspect real windows at supported localized layouts. A
successful build alone is not UI verification.
