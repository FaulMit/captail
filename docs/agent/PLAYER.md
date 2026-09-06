# Player and editor area

## Scope

Replay discovery, preview playback, recent-clip navigation, fullscreen UI,
audio-track selection, timeline/waveforms, and stream-copy trim.

## Flow and seams

- `ReplayLibrary` recursively finds supported clips, sorts newest first, and
  validates every file operation against replay root.
- Preview sidebar and main library request all clips.
- Sidebar combines collection/game and recording-mode filters. Mode is inferred
  from Captail's Replay_ and Recording_ filenames; renamed/imported clips are
  shown as unknown. Changing filters clears deletion marks.
- Checkboxes mark clips independently of playback. Batch deletion uses the
  existing confirmation and Recycle Bin path; failures retain remaining clips.
- Clicking the active clip toggles playback; clicking another clip loads it.
- `ClipEditorWindow` owns preview/trim state and user interaction.
- `MpvHost` embeds libmpv, hardware decode, playback state, volume, and selected
  audio mixing.
- `FfmpegAdapter` probes streams, creates thumbnails/waveforms, and trims without
  re-encoding video when stream copy is valid.

## Invariants

- Switching clips drains old player events before loading the next file.
- Preview plays every selected audio track simultaneously.
- Track toggles update playback without losing the source file.
- Fullscreen removes the recent-clips sidebar and native resize frame; controls
  hide after pointer inactivity.
- Trim defaults to preserving selected audio streams. Merge is explicit.
- Overwrite uses a temporary replacement and leaves no internal working file.
- Left transport edge is Next replay; right transport edge is Previous replay.

## Primary files

- `src/Captail/ClipEditorWindow.xaml`
- `src/Captail/ClipEditorWindow.xaml.cs`
- `src/Captail/MpvHost.cs`
- `src/Captail/ReplayLibrary.cs`
- `src/Captail/FfmpegAdapter.cs`

## Validation

```powershell
.\tools\TestCapturePlayerRegression.ps1
.\tools\TestNextFeatureRegression.ps1
dotnet build .\Captail.sln -c Release --no-restore
```

Use `--qa-replay-player`, `--qa-replay-navigation`, `--qa-audio-mix`,
`--qa-preview-geometry`, or `--qa-trim-overwrite` for runtime changes.
