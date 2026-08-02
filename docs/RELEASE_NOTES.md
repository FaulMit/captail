# Writing Captail release notes

Release notes answer three user questions:

1. What changed?
2. Why does it matter to me?
3. Must I do anything before or after updating?

`CHANGELOG.md` is the source for GitHub Release descriptions. The release workflow extracts the matching version section and adds package, compatibility, verification, and full-changelog links automatically.

## Before writing

Review every commit and merged pull request since the previous tag. For each user-visible change, record:

- what changed;
- which users or workflows it affects;
- the practical benefit or resolved problem;
- any required upgrade action;
- limitations that remain.

Do not infer a feature from a filename or commit title. Check the implementation, pull request description, or validation evidence.

## Required changelog format

Move completed entries from `[Unreleased]` into a version section before starting the release workflow:

```markdown
## [0.2.0] - 2026-08-15

### Added

- **Replay search:** Find saved clips by game or filename without browsing folders manually.

### Improved

- **Game detection:** Games are recognized sooner after launch, reducing desktop footage at the start of a clip.

### Fixed

- Fixed microphone audio disappearing after its device reconnects.

### Upgrade notes

- **Action required:** Install this version manually if updating from 0.1.3 or 0.1.4.
```

The version must match the workflow input exactly. Use `YYYY-MM-DD` for the date. Omit empty categories.

## Categories

- `Added`: new user-facing capabilities.
- `Improved`: an existing workflow became faster, clearer, or more capable.
- `Fixed`: a user-visible problem no longer occurs.
- `Upgrade notes`: users must take an action or should expect a one-time behavior.
- `Breaking changes`: existing settings, files, hotkeys, or workflows stop being compatible.
- `Known limitations`: important constraints still present in this release.

## Writing rules

- Lead with user impact, not implementation details.
- Use one bullet per change and keep it to one or two sentences.
- Use plain language. Explain technical terms only when users need them to act.
- Be specific about affected versions, capture modes, codecs, or hardware.
- Include measured performance only when the test setup and result are recorded.
- State uncertainty directly. Do not claim untested GPU or codec support.
- Do not paste commit titles, ticket numbers, validation logs, or dependency bumps into user notes.
- Do not describe routine refactoring unless it changes behavior, reliability, security, or performance.
- Do not use promotional claims such as “best,” “perfect,” or “production-ready.”

## Generate a local preview

```powershell
.\tools\New-ReleaseNotes.ps1 `
  -Version 0.2.0 `
  -PreviousTag v0.1.5 `
  -OutputPath "$env:TEMP\captail-release-notes.md"

Get-Content "$env:TEMP\captail-release-notes.md"
```

Check that the preview:

- begins with the changes rather than build-system details;
- includes every required user action;
- contains no empty heading;
- uses correct package names and compare links;
- matches the English wording used in Captail.

## After publishing

Open the release page and confirm its version, text, assets, checksums, prerelease state, and full-changelog link. Never replace binaries under an existing tag; publish a new patch version instead.
