# Capture area

## Scope

Replay buffer, continuous recording, desktop/game source selection, encoder
capabilities, output lifecycle, recovery, and capture notifications.

## Flow and seams

- `Config.CaptureMode` selects `replay` or `recording`.
- `App.xaml.cs` owns start/stop/restart policy and serializes pipeline work.
- `SingleThreadTaskScheduler` isolates OBS graph mutations.
- `ObsReplayEngine` builds one graph from normalized `Config`.
- Desktop mode uses monitor capture plus automatic game detection/fallback.
- Game mode uses OBS Game Capture directly.
- Replay mode keeps a rolling buffer and saves snapshots on demand.
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

## Invariants

- Never mutate OBS concurrently.
- A failed restart must restore a usable previous configuration when possible.
- Output-folder and replay-organization changes must not restart active capture.
- Stretched game resolutions fill the configured OBS canvas.
- Notifications must not activate or steal input from a game.
- Capture recovery and save completion must update both dashboard and tray.

## Validation

```powershell
.\tools\TestCapturePlayerRegression.ps1
.\tools\TestIssue41Fixes.ps1
.\tools\TestFullscreenOverlayHotkeys.ps1
dotnet build .\Captail.sln -c Release --no-restore
```

Use relevant `--qa-game-*`, `--qa-replay-*`, or recording QA entry points for
runtime changes. Real Game Capture changes require a real game test.
