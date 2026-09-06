# Current development handoff

Snapshot date: 2026-09-06

This file preserves intent of the v0.2.3 release batch. Verify live branch,
HEAD, and changed paths with `GetAgentContext.ps1`. Replace this snapshot after
the release is published or the batch is abandoned.

## Git state at snapshot

- Branch: `codex/bugfix-capture-player`
- Base HEAD: `02cb024` (`Refresh Captail landing page for v0.2.2`)
- Release candidate version is `0.2.3`.
- Worktree contains the validated v0.2.3 feature/fix batch. Production publish
  was authorized after user testing.

## Intent already implemented locally

- Replay/Recording mode selector moved to main dashboard. Recording mode owns
  continuous-file lifecycle and mode-specific status/notifications.
- Output-folder and organization changes no longer restart active OBS capture.
- non-native Game Capture geometry stretches to configured output canvas.
- Player gained resizable preview, recent-clips sidebar, clip navigation,
  fullscreen controls, wheel/keyboard volume, and hardware-decoding policy.
- Preview mixes selected audio tracks; trim preserves or explicitly merges them.
- Player transport order is Next replay on left, Previous replay on right.
- About links use packaged-safe URI launching.
- Accent themes are persisted and applied through shared resources.
- Fullscreen notifications reassert native topmost state on active monitor.
- F13-F24 bindings use a non-blocking low-level keyboard fallback.
- Store manifest requests borderless graphics-capture capability.

## Verification completed before this snapshot

- Release solution build completed with zero errors and warnings.
- Capture/player, reported UI, next-feature, issue 41, startup UI, and fullscreen
  hotkey/overlay regression suites passed.
- Runtime QA passed for recording output, replay navigation and library actions,
  multi-track audio mixing, AV1/4K preview geometry, trim overwrite, GPU recovery,
  Game Capture hook isolation, and process-audio routing.
- Store package v0.2.5.0 was built locally and passed package validation.
- Debug fullscreen QA verified F23/F24 matching, repeat suppression, native
  overlay styles, active monitor selection, and z-order above a fullscreen
  surrogate window.
- Latest Portable test build was deployed to `D:\Captail-0.1.10`.

## Still needs physical verification

- Notification visibility in a real target fullscreen game.
- F13-F24 from the reporter's physical Wooting layer.
- Yellow-border behavior on the affected Windows 11/custom-build machine.
- HEVC high-motion preview behavior on GTX 1080 while a game is active.
- Microsoft Store package behavior after the current batch is release-ready.

## Next checkpoint

Commit and push the validated batch, publish GitHub v0.2.3, then submit Store
package identity v0.2.5.0 with the synchronized listing and screenshots.
