# Architecture map

Captail is a focused Windows replay/recording application. WPF owns UI and
orchestration; libobs owns capture/encoding; libmpv owns preview playback;
FFmpeg tools own media inspection, waveforms, thumbnails, and stream-copy trim.

## Runtime flow

```text
SettingsWindow / hotkeys / tray
             |
             v
          App.xaml.cs
             |
             v
 SingleThreadTaskScheduler ---- ProcessAudioMonitor
             |
             v
       ObsReplayEngine
             |
       libobs + native bridge
             |
             v
       replay/recording file
             |
             +--> ReplayLibrary --> ClipEditorWindow
                                      |-- MpvHost
                                      `-- FfmpegAdapter
```

## Ownership

- `App.xaml.cs`: process lifetime, single instance, tray, global actions,
  pipeline serialization, recovery, notifications, and debug QA entry points.
- `Config.cs`: persisted user contract, normalization, comparison, and backup.
- `ObsReplayEngine.cs`: one OBS graph, capture source selection, encoding,
  replay/recording output, track layout, and capture health.
- `ObsNative.cs` plus `native/`: native interop and Captail-specific OBS pieces.
- `SettingsWindow.*`: dashboard, settings editing, replay library, and user
  interaction. It should not directly own OBS lifetime.
- `ProcessAudio*`: process discovery, selection, monitoring, reconciliation,
  icons, and optional OBS process-loopback sources.
- `ClipEditorWindow.*`, `MpvHost.cs`, `ReplayLibrary.cs`, `FfmpegAdapter.cs`:
  preview and non-destructive media workflows.
- `AppDataPaths.cs`, `AppDistribution.cs`, `StorePackageLifecycle.cs`:
  package-channel differences and local state.

## Cross-cutting invariants

- OBS mutations are serialized; avoid concurrent graph operations.
- Settings are normalized before use and backed up before replacement.
- Replay and continuous recording share capture configuration but have distinct
  output lifecycles.
- UI strings are dynamic resources present in every language dictionary.
- Portable/Setup and Store are separate distribution channels.
- Public screenshots show real release-candidate behavior, never mockups.

Use the task router in `INDEX.md` before reading implementation files.
