# Changelog

All notable user-facing changes are documented here.

## [Unreleased]

## [0.2.1] - 2026-08-21

### Added

- **Replay player:** Open a recent replay for immediate playback without entering edit mode, then continue into Trim when needed. The player supports seeking, fullscreen playback, keyboard controls, and temporary speed feedback from `0.25×` to `2×`.
- **Replay-off game reminder:** An optional notification warns once when Captail detects a game while Instant Replay is disabled. It never starts recording without user action.
- **Unsaved-settings guard:** Editing settings reveals a compact notice with Cancel and Done actions in the title bar. Close, `Alt+F4`, and `Esc` attempts keep the settings open and use a short shake to draw attention to pending changes.

### Improved

- **Lower Game Capture idle load:** When no plausible game is running, Captail releases the replay output and keeps only a low-rate detector active. The normal replay pipeline resumes automatically after a game is detected.
- **Cleaner secondary actions:** GitHub, bug reports, feature requests, and privacy information now live in one compact About menu while the version and update state remain directly visible.
- **Replay status indicator:** Removed the dark halo and made the indicator fully opaque for consistent contrast on bright backgrounds.

### Fixed

- Prevented Game Capture from treating unrelated fullscreen applications as games while waiting for a real game candidate.
- Generated unplated Microsoft Store taskbar icons at every required target size and added package validation so placeholder-sized icons cannot ship unnoticed.
- Prevented changed settings from being lost when the settings window is dismissed accidentally.

## [0.2.0] - 2026-08-14

### Added

- **Eleven interface languages:** Captail now supports English, Russian, Ukrainian, Simplified Chinese, Spanish, Brazilian Portuguese, German, French, Japanese, Korean, and Polish. First launch follows Windows when its language is supported, otherwise it uses English. Simplified Chinese was contributed by [@zhuyouyi](https://github.com/zhuyouyi).
- **Compact language menu:** The title-bar language control now opens a focused native-name selector instead of cycling through languages.
- **Display identification:** A button beside the monitor selector briefly shows each display number on the matching screen.
- **Built-in feedback links:** The footer now opens dedicated GitHub forms for sanitized bug reports and focused feature requests.

### Improved

- **Safer automatic game detection:** Desktop mode recognizes common fullscreen game installations even when Game Capture cannot produce usable frames. Captail keeps desktop video active and associates the replay with the detected game instead of losing coverage or filing it under an unrelated application.
- **More useful bug reports:** Captail can prefill version, package channel, Windows, GPU, driver, recording settings, and a short sanitized diagnostic excerpt. The complete local log, personal paths, identifiers, device names, and recorded content are not attached.
- **Localized layout checks:** Release validation now checks all language dictionaries, formatting placeholders, critical translations, and compact UI regions.

### Fixed

- Kept bottom-corner replay indicators above the Windows taskbar and clock by positioning them inside each monitor's working area.
- Prevented the indicator's position timer from repeatedly raising it above Snipping Tool and other newer topmost system overlays.
- Made the indicator visible in Windows screenshots while keeping it excluded from Captail replay recordings.
- Returned the indicator to the chosen physical screen corner after a game is detected, while retaining taskbar-safe placement on the desktop.
- Fixed clipped long labels and incorrect free-space wording in supported translations.

## [0.1.9] - 2026-08-11

### Added

- **Replay status indicator:** An optional, click-through indicator now shows when Instant Replay is active, recovering, unavailable, or has just saved a replay. It is enabled by default and can be placed in any screen corner.

### Improved

- The indicator stays out of Captail recordings where Windows capture protection is available and keeps its animation stable instead of restarting on repeated status updates.

### Fixed

- Prevented the Windows Graphics Capture border from appearing around the desktop on Windows 10. Desktop capture now prefers DXGI there, with WGC retained as a fallback for displays DXGI cannot access; Windows 11 continues to use WGC directly.

## [0.1.8] - 2026-08-06

### Improved

- **Reliable Microsoft Store updates:** Store installations now keep application state inside package-managed locations and stop capture cleanly during package updates or removal.
- **Cleaner uninstallation:** The regular installer now closes Captail before removal and deletes its application data after uninstalling.
- **Correct per-game replay folders:** Automatic capture now files replays under the game Captail actively selected, instead of an inactive application that still had an old capture hook.

### Fixed

- Prevented Microsoft Store builds from loading libobs media DLLs into the bundled FFmpeg tools, which could stop replay metadata, waveform, and trim operations before FFmpeg started.
- Added the Microsoft Visual C++ runtime dependency required by OBS and other native components on clean Windows installations.

## [0.1.7] - 2026-08-04

### Added

- **Fast embedded replay preview:** The clip editor now uses a hardware-accelerated built-in player with responsive seeking, simultaneous playback of selected audio tracks, fullscreen viewing, keyboard controls, and a fullscreen progress bar.
- **Clear loading states:** The replay library and clip preview now distinguish loading, empty, failed, and ready states instead of appearing blank while work is in progress.
- **Microsoft Store availability:** Captail can now be installed from Microsoft Store with Store-managed updates.

### Improved

- **Safer clip editing:** Saving and overwriting show a focused progress overlay, release the preview file before replacement, retry short Windows file-lock races, and keep temporary working files out of the replay library.
- **Automatic capture switching:** Desktop mode uses Game Capture only while the hooked game is producing video and remains the foreground application. Alt-tabbing returns recording to the desktop instead of leaving a stale game frame active.

### Fixed

- Fixed black or incorrectly cropped video in the clip editor while keeping fast hardware-accelerated playback.
- Fixed native video covering overwrite confirmation and saving overlays.
- Fixed non-game applications such as Telegram being selected as the active automatic capture source.
- Fixed closing Captail leaving its installation or Portable folder locked by a running game. Game Capture hooks now load from a validated per-user cache instead of the application directory.
- Fixed misaligned loading indicators and saving text in the replay library and clip editor.

## [0.1.6] - 2026-08-03

### Added

- Multi-track replays can now mix all enabled audio tracks into one playback-friendly track when saving or overwriting a trimmed clip. Video remains untouched; only audio is re-encoded.

### Fixed

- Settings selected in the window now remain visible when the recording pipeline rejects a change, making it possible to correct the failing option without re-entering resolution, audio, and other pending choices.
- NVIDIA HEVC encoding no longer requests B-frames, fixing encoder startup on hardware such as the GeForce GTX 1080 where HEVC B-frames are unsupported.

## [0.1.5] - 2026-08-02

### Fixed

- Update downloads now close their file handles before promoting verified packages or replacing an invalid cached package, preventing Windows file-lock errors during one-click updates.

### Upgrade notes

- Captail 0.1.3 and 0.1.4 cannot complete an in-app update because the bug is inside those installed versions. Download and run the 0.1.5 Setup EXE once; later in-app updates will work normally.

## [0.1.4] - 2026-08-02

### Added

- Replay library with every saved clip in a vertically scrollable list.
- Built-in clip editor with responsive video preview, a single trim range, separate system/game and microphone track controls, save-as-copy, and confirmed overwrite.
- Clip details showing estimated trimmed size, original size, resolution, frame rate, and codec.
- Automatic desktop-to-game capture switching, plus a game-only mode that leaves the desktop out of recordings.

### Changed

- Replay cards now reveal compact actions on hover instead of using a large edge glow.
- FFmpeg and FFplay are included with Captail, so clip preview and trimming need no separate download.

### Fixed

- Clean installations now receive the FFmpeg runtime required by the editor.
- Preview rendering keeps the complete frame visible instead of showing only its upper-left region.

## [0.1.3] - 2026-07-26

### Added

- Concise English and Russian help tooltips for replay, video, and audio settings.
- Dashboard footer with repository access, current version, and one-click updates for installed and Portable builds.

### Fixed

- Watchdog checks no longer mistake an in-progress pipeline startup for a stopped recording module.
- Consecutive replay saves now advance the rolling window so later clips contain only footage recorded since the previous save.
- Save controls and notifications now show the currently available replay duration instead of always showing the configured maximum.

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
