# Current development handoff

Snapshot date: 2026-11-05

Verify the live branch, HEAD, and changed paths with `GetAgentContext.ps1` before acting on this snapshot.

## Release candidate

- Branch: `release/0.3.0`, based on `abe84a7` from `main`.
- Candidate version: `0.3.0`.
- Includes accumulated local test changes: recording presets, advanced settings, quick panel, instant screenshots, disk replay, Bulgarian, localized track labels, persistent trim editor, audio routing, and capture recovery.
- Preserve the existing README showcase video. Do not add application screenshots or regenerate listing images during this release.
- `CHANGELOG.md` supplies GitHub notes; Store EN/RU notes describe the same release scope.

## Validation

- Release solution build: zero warnings and errors.
- Focused capture/player, audio, hotkey, startup, dependency, Store workflow, and agent-context contracts passed.
- Runtime preset, localization, diagnostics, replay-library, process-audio planning, and quick-panel checks passed.
- Store metadata validation passed for two locales and the six existing images.
- Consult candidate `.qa/` logs and GitHub Actions for final runtime and package results before publishing.

## Open verification

- Long disk-buffer saves on slow drives remain a known limitation; no save-speed fix is claimed.
- Physical dual-mode display behavior and actual games require testing on affected hardware.
- Voicemeeter Separate tracks routing does not establish support for every virtual mixer.
- Microsoft Store certification and rollout continue after successful submission.

## Next checkpoint

Finish candidate validation, publish GitHub `v0.3.0` from `main`, and submit the Store package with synchronized EN/RU notes and existing listing images.
