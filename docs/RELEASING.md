# Releasing Captail

Captail releases are built entirely by GitHub Actions on a pinned Windows runner.

Each release contains:

- Self-contained Portable ZIP
- Self-contained Windows installer with uninstaller
- SHA-256 checksum file
- GitHub build-provenance attestations

## Create a release

From GitHub:

1. Open **Actions**.
2. Select **Build release**.
3. Click **Run workflow**.
4. Enter a semantic version without `v`, for example `0.1.1`.
5. Keep **Pre-release** enabled while Captail is in preview.

From GitHub CLI:

```powershell
gh workflow run release.yml `
  --repo FaulMit/captail `
  --ref main `
  -f version=0.1.1 `
  -f prerelease=true
```

The workflow validates the version, builds and verifies both packages, creates tag `v0.1.1`, and publishes the GitHub Release.

## Local package build

Install Inno Setup, then run:

```powershell
.\tools\AcquireObsRuntime.ps1
.\tools\BuildRelease.ps1 `
  -Version 0.1.1 `
  -InnoSetupCompiler "C:\Program Files\Inno Setup 7\ISCC.exe"
```

Output is written to `artifacts\release\0.1.1`.

## Version rules

- Patch: compatible bug fix (`0.1.0` → `0.1.1`)
- Minor: compatible feature (`0.1.x` → `0.2.0`)
- Major: breaking change (`0.x` → `1.0.0`)

Never replace published binaries under an existing tag. Publish a new version.
