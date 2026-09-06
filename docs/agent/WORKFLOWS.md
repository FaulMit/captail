# Development and release workflows

Commands run from repository root in PowerShell.

## Build

```powershell
dotnet restore .\src\Captail\Captail.csproj --locked-mode
dotnet build .\Captail.sln -c Release --no-restore
```

Do not restore on every edit when the lock file and SDK are unchanged.

## Validation by area

| Area | Commands |
| --- | --- |
| Capture | `TestCapturePlayerRegression.ps1`, `TestIssue41Fixes.ps1`, `TestFullscreenOverlayHotkeys.ps1` |
| Audio | `TestIssue41Fixes.ps1`, `TestIssue41Followup.ps1` plus relevant process-audio QA |
| Player | `TestCapturePlayerRegression.ps1`, `TestNextFeatureRegression.ps1` |
| UI | `TestReportedUiRuntimeRegressions.ps1`, `TestStartupUiSurfaces.ps1`, `TestLocalizedLayout.ps1` |
| Distribution | Store isolation tests and `TestStoreReleaseWorkflow.ps1` |
| Context docs | `TestAgentContext.ps1` |

Run scripts as `./tools/<name>`. Runtime `--qa-*` entry points live in
`App.xaml.cs`; use them when a static contract cannot prove behavior.

## Installed test build

Only when the user asks to test the current local work:

```powershell
.\tools\DeployTestBuild.ps1 -Version 0.1.10
```

This safely replaces `D:\Captail-0.1.10` with a Portable build and validates
required runtime files. It does not publish or modify Microsoft Store state.

## GitHub release

Follow `docs/RELEASING.md`. Required before dispatch:

- version and dated changelog section;
- generated release notes preview;
- complete real screenshot baseline from exact candidate;
- significant-feature screenshots;
- local build/package validation.

GitHub release binaries come from the pinned Actions workflow. Never replace
assets under an existing tag.

## Microsoft Store

Follow `docs/MICROSOFT-STORE-RELEASES.md` and `packaging/msix/README.md`.
Store release is separate from GitHub release. Normal submissions synchronize
EN/RU listing text and screenshots from `store-listing/`.

Do not call Partner Center, submit certification, or alter a draft without an
explicit user request.

## Screenshots

Follow `docs/SCREENSHOTS.md`. Every release gets a fresh real baseline from the
exact candidate. Showcase settings require hardware AV1, 4K, and 240 FPS.
