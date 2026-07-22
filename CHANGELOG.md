# Changelog

All notable user-facing changes are documented here.

## [Unreleased]

## [0.1.2] - 2026-07-22

### Fixed

- Settings, audio-source changes, replay toggles, and replay saves no longer block the UI while libobs starts, stops, or writes a replay.
- Failed settings changes now roll back configuration, hotkeys, autostart state, and the recording pipeline instead of leaving Captail partially configured.
- Rapid pipeline operations are serialized to prevent save, restart, watchdog recovery, and shutdown races.
- Configuration writes are atomic and automatically recover from the last valid backup after a damaged file.
- Game Capture now uses the selected game's client size for source resolution and avoids an unnecessary scene-composition pass.
- Installer upgrades request a graceful Captail shutdown and uninstall removes its startup entry.

### Changed

- Moved libobs lifetime operations to a dedicated thread and reduced synchronous disk, device, process, and log work on the UI thread.
- Hardened native library loading, OBS runtime acquisition, dependency locking, installer downloads, and GitHub Actions against dependency substitution.
- Added automated capability, codec, recovery, and high-frame-rate Game Capture diagnostics.
- Validated AV1 Game Capture at 2560x1440 and 240 unique frames per second on an NVIDIA GeForce RTX 5070, including concurrent synthetic GPU load.

## [0.1.1] - 2026-07-21

### Fixed

- Saving a replay now shows a "Saving replay…" notification immediately; the "Replay saved" confirmation replaces it once the file is on disk, instead of appearing only after the write finished.
- Overlay notifications no longer let a previous notification's fade-out dismiss a newer notification that appeared during it.

### Changed

- Renamed remaining solution, project, namespace, icon, and native bridge identifiers to Captail.
- Added a real interface screenshot and an OBS Replay Buffer comparison to the README.
- Standardized repository comments and diagnostic messages in English.

## [0.1.0] - 2026-07-20

First public preview.

### Added

- Desktop and selected-game capture through libobs.
- AV1, HEVC, and H.264 hardware encoder detection.
- 30–240 FPS and source/720p/1080p/1440p/4K output options.
- System or game audio, microphone, volume controls, boost, and separate tracks.
- Rolling replay buffer with duration and size limits.
- Save and replay-toggle global hotkeys.
- Watchdog recovery for stopped or stalled recording pipelines.
- Tray controls, double-click restore, startup integration, and overlay notifications.
- English interface with live Russian language switching.

### Known limitations

- First public preview; hardware-specific bugs are expected.
- NVIDIA RTX 40 and RTX 50 series are tested. Other GPU families need public testing.
- Release binaries are not Authenticode-signed.
