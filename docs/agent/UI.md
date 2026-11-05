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
- `QuickPanelWindow`: interactive edge panel with inline sections and audio meters.
- Instant screenshots have an enable switch in Recording and a global shortcut
  in Hotkeys. Recording and screenshots share the Video monitor selection.
  PNGs use `OutputDirectory/Screenshots`.
- `Themes/Theme.xaml` and `ThemeManager`: shared resources and accent selection.
- `Languages/Strings.*.xaml` and `Localization`: dynamic localized strings.

## Invariants

- Settings edits are staged. Done applies; Cancel discards. Closing dirty
  settings shows the save/discard notice and keeps the window open.
- Recording presets appear first in settings and start empty; no built-in
  profiles or reserved built-in names. Existing user presets are retained.
  Custom presets can be saved,
  renamed/updated, and deleted; these operations follow Done/Cancel. Profiles
  include video, audio, buffer preferences, and advanced disclosure state, but
  exclude capture enable/mode, output folder, hotkeys, and appearance.
- Advanced disclosure uses the last switch in the scrollable settings list and
  only hides controls, preserving their values. New encoder/audio controls and
  presets use the shared question-mark help tooltip with a title and explanation.
  Provider presets follow detected NVENC/AMF/Quick Sync capabilities. CQP disables bitrate;
  replay size limits and NVENC HEVC keep enforced keyframe/B-frame safeguards.
- Advanced replay settings replace the fixed duration buttons with a minutes
  field. Accept decimal comma or dot, round to whole seconds, and validate the
  1-second to 24-hour range before applying settings or saving/updating a preset.
  Duration persists independently of disclosure and storage mode. Basic mode
  retains fixed choices and shows the current custom duration when none matches.
- Main replay/recording selector applies immediately and shows only controls
  relevant to the selected mode.
- Every localization key exists in all dictionaries with matching placeholders.
- Public repository UI copy is authored in English, then localized.
- Accent changes use shared dynamic resources, not isolated hard-coded colors.
- About links use the packaged-safe external URI launcher.
- Status and notification overlays never activate, capture pointer input, or
  appear in taskbar. The quick panel opens without activation; Save Replay must
  not request keyboard focus or minimize fullscreen games. Use WS_EX_NOACTIVATE
  as well as ShowActivated=False. Native mouse activation
  retains clicks without activating the panel. Escape and foreground changes
  dismiss an inactive panel too; editing controls may request keyboard focus.
- Quick panel audio meters read Windows endpoint peaks only while visible and
  expanded. They estimate device signal level after configured volume/gain;
  process audio routing does not expose a system meter.
- Quick panel microphone boost persists immediately after adjustment and updates
  the existing OBS source on the serialized OBS thread without restarting capture.
- The capture-toggle delegate returns the resulting active state. Quick panel
  treats a result matching the requested state, including `false`, as success.
- Quick-panel Recording mode replaces the replay toggle/status row with a full
  accent button, current start/stop label and icon, and configured save hotkey.
  Use actual runtime state and the shared guarded enable/disable delegate.
- All quick-panel bindings use the low-level keyboard hook, including ordinary
  letters and F1-F12. `RegisterHotKey` reserves the combination; `WM_HOTKEY` does
  not dispatch a second toggle. Queue panel actions on the UI dispatcher after
  the hook returns; rebind/disposal cancels queued actions for the old binding.
- Screenshot settings follow the existing staged Done/Cancel behavior and must
  not restart recording. Screenshot hotkeys are unregistered while disabled;
  failed rebinds restore the previous bindings. Notifications exclude themselves
  from screen capture so rapid screenshots do not include earlier save messages.

## Validation

Disk replay is an advanced-only toggle inside the Replay group. Hiding advanced
controls preserves its value; presets include it. Recording mode hides it and
uses the ordinary continuous output. Buffer duration/size help switches between
RAM and disk wording. Changes remain staged until Done; Cancel discards them.

```powershell
.\tools\TestReportedUiRuntimeRegressions.ps1
.\tools\TestStartupUiSurfaces.ps1
.\tools\TestLocalizedLayout.ps1
.\tools\TestNextFeatureRegression.ps1
.\tools\TestInstantScreenshots.ps1
dotnet run --project .\tools\QuickPanelQa -c Release
dotnet run --project .\tools\QuickPanelQa -c Release -- --hotkeys
dotnet run --project .\tools\QuickPanelQa -c Release -- --focus
dotnet run --project .\tools\RecordingPresetsQa -c Release -- --screenshots=.qa\recording-presets
dotnet build .\Captail.sln -c Release --no-restore
```

For visual changes, inspect real windows at supported localized layouts. A
successful build alone is not UI verification.
