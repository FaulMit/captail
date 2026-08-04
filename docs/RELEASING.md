# Releasing Captail

Captail releases are built entirely by GitHub Actions on a pinned Windows runner.

Each release contains:

- Self-contained Portable ZIP
- Self-contained Windows installer with uninstaller
- SHA-256 checksum file
- GitHub build-provenance attestations

## Create a release

Before running the workflow:

1. Review changes since the previous tag.
2. Move completed entries from `[Unreleased]` into a dated version section in `CHANGELOG.md`.
3. Follow [RELEASE_NOTES.md](RELEASE_NOTES.md) for categories, wording, and required upgrade notices.
4. Preview the generated GitHub Release description locally:

```powershell
.\tools\New-ReleaseNotes.ps1 `
  -Version 0.1.7 `
  -PreviousTag v0.1.6 `
  -OutputPath "$env:TEMP\captail-release-notes.md"
```

Commit and merge the changelog before dispatching the release. The workflow stops if it cannot find a non-empty section matching the requested version.

Then start the release from GitHub:

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

The workflow validates the version, builds and verifies both packages, creates tag `v0.1.1`, and publishes the GitHub Release using the matching changelog section.

After publishing, verify:

- release title, version, and prerelease status;
- Installer, Portable ZIP, and `SHA256SUMS.txt` assets;
- generated release notes and full-changelog link;
- GitHub build-provenance attestations.

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

## Microsoft Store release

Microsoft Store is a separate package channel. It must not be attached to a
GitHub Release because Store installs, signs, and updates the package.

Build an upload-ready package locally:

```powershell
.\tools\BuildStorePackage.ps1 -Version 0.1.7
```

Upload `artifacts\store\0.1.7\Captail-0.1.7.0-x64.msixupload` in Partner
Center. Full identity, validation, update-channel, and submission instructions
are in [`packaging/msix/README.md`](../packaging/msix/README.md).
