# Capture area

## Scope

Replay buffer, continuous recording, desktop/game source selection, encoder
capabilities, output lifecycle, recovery, and capture notifications.

## Flow and seams

- `Config.CaptureMode` selects `replay` or `recording`.
- `App.xaml.cs` owns start/stop/restart policy and serializes pipeline work.
- Fresh configurations default `ReplayEnabled` to false; saved preferences remain
  unchanged. Foreground startup prepares Borderless permission even while replay
  is off, without creating an OBS graph. The permission request shares the pipeline
  gate with manual starts. Disabled background launches do not show consent UI.
- `SingleThreadTaskScheduler` isolates OBS graph mutations.
- `ObsReplayEngine` builds one graph from normalized `Config`.
- Desktop mode uses monitor capture plus automatic game detection/fallback.
- Windows 10 desktop capture explicitly uses DXGI Desktop Duplication, including
  monitor rebinds and recovery. OBS Auto can select WGC and show a yellow border.
  Windows 11 (build 22000+) prefers WGC only when Borderless API support exists
  and permission preflight returns Allowed. Unsupported APIs, denied/undeclared
  access, exceptions, and timeouts force DXGI rather than OBS Auto. The permission
  result is cached for the process lifetime; restart after changing permissions.
- Game mode uses OBS Game Capture directly.
- Both modes select an explicit foreground game window with executable matching.
  Known utility processes and unknown desktop UI window classes are rejected.
  The game catalog, common game-engine window classes, game-library paths, or
  fullscreen native windows identify candidates. Unknown games remain eligible;
  this is a heuristic, not an exhaustive game-only classification. Focus loss
  clears the native target; another foreground game replaces it. Window-title
  changes alone do not reset an existing hook. GameOnly validates foreground
  identity before resuming replay; Auto returns to desktop when focus changes.
- Replay mode keeps a rolling buffer and saves snapshots on demand.
- `DiskReplayBufferEnabled` switches replay storage from OBS RAM to split
  `ffmpeg_muxer` segments. Continuous recording ignores this preference.
- `DiskReplayBuffer` owns a marked GUID session under `<output>/.captail-buffer`.
  Segment names contain `.tmp.` so the library excludes them. Cleanup never
  traverses reparse directories or recursively deletes an output folder.
- Recording mode writes a continuous file and routes it after clean shutdown.
- `ReplayPaths` validates and organizes completed files.

## Primary files

- `src/Captail/App.xaml.cs`
- `src/Captail/Config.cs`
- `src/Captail/ObsReplayEngine.cs`
- `src/Captail/ObsNative.cs`
- `src/Captail/Interop/CaptureInterop.cs`
- `src/Captail/ReplayPaths.cs`
- `native/ObsBridge/`
- `InstantScreenshot`: independent physical-monitor PNG capture through the native
  DXGI bridge. It never owns or changes the OBS graph.

## Invariants

- Never mutate OBS concurrently.
- Internal canvas and process-audio mixer scenes use `obs_scene_create_private`.
  Public scenes are retained by OBS's main canvas until shutdown, after capture
  module unload; private scenes drain with the engine's explicit source teardown.
- After context shutdown, release libobs's retained `win-capture.dll` reference.
  OBS 32.2.2's hook initialization join guard is static and one-shot; leaving it
  loaded allows the next initialization worker to outlive its module context.
- `native/ObsWinrt` rebuilds pinned OBS 32.2.2 WinRT capture with null guards for
  repeated frame-pool/session release. Build/publish must use this DLL.
- Disk replay uses two-second segments/keyframes (one-second keyframes with a
  size limit). Closed segments expire by encoded time and approximate size;
  the active segment and leased save files may temporarily exceed the limit.
  Split callbacks only capture file ownership. Saving leases closed segments,
  waits for the native writer to close them, and stream-copies every track with
  bundled FFmpeg. A keyframe-aligned clip can include one extra GOP.
- Snapshot readiness is explicit for disk replay; increasing file bytes must
  never trigger an early replay reset. Reset advances the retained window
  without stopping capture. Game idle restart assigns a fresh initial path.
- Disk save cancellation/output failure releases leases; clean shutdown removes
  owned temporary segments. An exclusive session marker protects live sessions
  while the next start removes abandoned data after a crash.
- Encoder tuning defaults preserve automatic load profiles. Manual CBR/CQP,
  provider presets, keyframe interval, B-frames, and NVENC options are applied
  before encoder creation. A replay size limit still forces one-second keyframes;
  NVENC HEVC still forces zero B-frames. Profile metadata/disclosure alone must
  not restart capture; encoding/audio changes use App's existing restart path.
- A failed restart must restore a usable previous configuration when possible.
- Output-folder and replay-organization changes must not restart active capture.
- Stretched game resolutions fill the configured OBS canvas.
- Notifications must not activate or steal input from a game.
- Capture recovery and save completion must update both dashboard and tray.
- App owns one background `CaptureMonitor`: capture state every 500 ms and
  pipeline health every 2 seconds. Source selection, monitor rebind, GameOnly
  pause/resume, and recovery must progress without pumping WPF. Native work stays
  on the serialized OBS scheduler; only presentation is posted to the UI.
- A state probe has an 8-second deadline. Cancel timed-out queued work and
  quarantine an in-flight probe until it drains; never accumulate more probes.
  Observe OBS worker busy time independently of the pipeline gate (30-second
  deadline). A hung worker uses existing process recovery and restart-loop guard.
  Stop observation before teardown; queued probes check cancellation before native
  access and before publication. Initial WGC permission preflight remains on UI;
  recovery reuses its completed backend choice.
- Desktop capture re-enumerates monitors on the serialized OBS thread. Preserve
  selected identity across reordering; match desktop position/overlap when a dual-mode
  display exposes a replacement identity. Fall back to an available display and
  restore the preferred display when it returns.
- Debounce monitor changes before replacing the desktop source. Keep encoders,
  replay history, continuous recording, and the original output canvas alive;
  stretch the replacement desktop to that canvas until the next pipeline start.
- Instant screenshots work with replay disabled. Resolve the shared Video
  `MonitorIndex` on every press; do not silently fall back to another monitor.
- App owns one `DisplayHdrMonitor` background thread for its normal lifetime,
  including when recording is off. It samples all displays every 250 ms through
  `DisplayColorState` (DisplayConfig active mode and Windows "Use HDR" together;
  fresh DXGI on older Windows). It publishes complete snapshots shared by both
  capture modes. WPF timers and the OBS/pipeline queue never run these queries.
  Capability/WCG alone must not select HDR; transient failures retain known state.
- Observer notifications coalesce behind App's pipeline gate, then apply color
  changes on the serialized OBS scheduler. Preserve the latest SDR/Windows-enable
  transition revisions so off/on or inactive/active pairs observed while OBS is
  busy are not lost. Stop/cancel observation before native capture teardown.
- Desktop capture leaves `force_sdr=false`: WGC/DXGI select SDR/HDR surfaces and
  OBS tone maps HDR into the fixed SDR NV12/BT.709 canvas. Game Capture updates
  only `rgb10a2_space` from Windows HDR state; SDR8 and float scRGB retain native
  handling. This is a display-based default for ambiguous 10-bit game input,
  not detection of a game's HDR setting. Color changes keep output/history alive.
- When Game Capture observes HDR inactive or Windows "Use HDR" off, latch SDR
  for the capture session. Hook/source changes and active-mode recovery cannot
  release it. Windows "Use HDR" off/on releases the latch once HDR output is
  active; a new capture session chooses its initial mode again. Older Windows
  without the separate user-setting flag can only use active HDR transitions.
- OBS 32.2.2 clears `get_hooked` executable metadata on a settings update even
  when the hook remains active. Retain the known executable after a color update
  until hook loss or fresh metadata; otherwise automatic capture can fall back
  to Desktop spuriously. Initial/changed color selections are logged.
- Desktop Duplication may initially deliver pointer-only updates; wait for a
  desktop presentation before copying pixels. Convert HDR scRGB through Windows
  tone mapping and color management, and compensate display rotation.

## Local diagnostics

- `Log` writes dated, leveled records with time-zone offset, session, PID,
  thread, and optional operation ID. `DiagnosticLogFile` flushes at least once
  per second and immediately on warnings/errors. It retains four 8 MiB archives
  beside `log.txt`; concurrent processes use a separate `log-<PID>.txt`.
- `AppDiagnostics` records build/module identity, distribution, environment,
  normalized capture configuration, and unhandled managed exceptions. Exception
  handlers flush evidence without changing exception-handling/recovery policy.
  A background resource sample records process memory/handles/CPU time and local
  output-drive free space; slow storage probes never overlap or run on the UI/OBS thread.
- Replay saves correlate gate waits, native split/saved callbacks, disk snapshot
  ownership, file-close waits, probe/remux processes, routing, and completion.
  Five-second pending records identify hangs; timeout records contain the actual
  stage and deadline, independently of localized notification wording.
- Native capture state is sampled on the serialized OBS thread every 30 seconds
  and before saves. Error handlers use cached state with its sampling timestamp.
  Repetitive native timestamps/segment rotations are counted over 30-second windows;
  native warnings/errors remain unsuppressed. FFmpeg records arguments, child PID,
  exit status, elapsed time, cancellation/deadline flags, and bounded stderr tails.
- `DiagnosticLogExporter` supports both timestamp formats and the latest archive,
  prioritizes failures and correlated events, and redacts structured fields before
  creating the short public bug-form excerpt. Raw logs remain local.

## Diagnostic validation

```powershell
dotnet run --project .\tools\BorderlessCaptureQa -c Release
dotnet run --project .\tools\BorderlessCaptureQa -c Release -- --probe --output result.json

dotnet run --project .\tools\DisplayTopologyQa -c Release -- --hdr-only
dotnet run --project .\tools\DisplayTopologyQa -c Release -- --hdr-monitor-only
dotnet run --project .\tools\DisplayTopologyQa -c Release -- --capture-monitor-only
dotnet run --project .\tools\DiagnosticsQa -c Release
dotnet run --project .\tools\ProcessAudioFoundationQa -c Release -- --game-selection
dotnet run --project .\tools\DisplayTopologyQa -c Release -- --game-selection-only
```

The source-linked QA isolates its log destination and verifies flushing, concurrency,
retention, writer contention/recovery, correlation, native throttling, and public
excerpt redaction. Native capture/save paths still need their runtime smoke tests.

## Validation

```powershell
.\tools\TestCapturePlayerRegression.ps1
.\tools\TestIssue41Fixes.ps1
.\tools\TestFullscreenOverlayHotkeys.ps1
.\tools\TestInstantScreenshots.ps1
.\tools\TestDisplayTopology.ps1
dotnet run --project .\tools\QuickPanelQa -c Release
dotnet run --project .\tools\QuickPanelQa -c Release -- --capture --wgc-release-twice
dotnet run --project .\tools\QuickPanelQa -c Release -- --capture --user-config --toggle-only
dotnet run --project .\tools\RecordingPresetsQa -c Release -- --capture=.qa\recording-presets\capture
dotnet run --project .\tools\RecordingPresetsQa -c Release -- --disk=.qa\disk-replay
dotnet build .\Captail.sln -c Release --no-restore
```

Use relevant `--qa-game-*`, `--qa-replay-*`, or recording QA entry points for
runtime changes. Real Game Capture changes require a real game test.
RecordingPresetsQa verifies staged profile controls and native settings for all
encoder families. Its optional capture smoke test feeds an FFmpeg test pattern
through the production OBS encoders/output, decodes each file, and rejects black
frames. It verifies the local GPU only, independently of desktop/game capture.
