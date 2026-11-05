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
- Create, initialize, and destroy libmpv on the same dedicated MTA worker.
  Native media initialization must not alter WPF's STA COM lifetime; repeated
  multi-track editor sessions previously ended with an `ITfThreadMgr` exception.
- Preview plays every selected audio track simultaneously.
- Track toggles update playback without losing the source file.
- AV1 clips that fall back to software during initial decoding retry hardware
  decoding once after the first valid video output, before playback starts.
- Fullscreen removes the recent-clips sidebar and native resize frame; controls
  hide after pointer inactivity.
- Trim defaults to preserving selected audio streams. Merge is explicit.
- Overwrite uses a temporary replacement and leaves no internal working file.
- Saving a trimmed copy keeps the source open. Overwrite reloads the replaced
  clip, its duration, audio tracks, and full selection range in the same window.
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
`--qa-trim-overwrite` also checks that the modal player stays open after copy
and overwrite, resumes playback, and supports a second save, with and without
audio merging. Use a fixture with two audio tracks for this check.
Run `./tools/TestPlayerSavePersistence.ps1 -ClipPath <multi-track-clip>` to repeat
the full test in fresh processes and check clean process exit as well as the
individual save assertions. The clip must be at least eight seconds long.

Use `tools/PlayerSyncQa` with a real replay and the target libmpv runtime for
native audio/video synchronization checks after forward/backward seeks.
