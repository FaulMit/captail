# Distribution area

## Channels

Portable ZIP and Setup share ordinary desktop behavior and GitHub update flow.
Microsoft Store uses MSIX identity, package-managed state, Store updates, and a
separate Partner Center release workflow.

## State and lifetime

- `AppDistribution` detects package channel.
- `AppDataPaths` selects roaming/local paths for ordinary builds and
  `ApplicationData` paths for Store builds.
- `StorePackageLifecycle` responds to package update/removal lifetime events.
- `Autostart` uses the channel-appropriate mechanism.
- `UpdateService` applies only to ordinary GitHub-distributed builds.
- `DiagnosticLogExporter` sanitizes local diagnostic context before a bug form
  is opened; logs and recordings are never attached automatically.

## Packaging

- `tools/BuildRelease.ps1`: Portable/Setup artifacts.
- `tools/BuildStorePackage.ps1`: upload-ready MSIX archive.
- `packaging/msix/AppxManifest.xml.template`: Store identity and capabilities.
- `store-listing/`: localized Store text and images.
- `.github/workflows/release.yml`: GitHub release.
- `.github/workflows/store-release.yml`: Store package/listing submission.

## Invariants

- Never mix Store package files into a GitHub Release.
- Never hard-code a Store Package Family Name in product logic or docs.
- Store internal identity has four fields and ends in `.0`.
- Package-root native dependency collisions fail validation.
- External publishing requires explicit user authorization.

## Validation

Run `TestStoreLifecycleIsolation.ps1`, `TestStoreFfmpegIsolation.ps1`,
`TestStoreNativeDependencies.ps1`, `TestStoreIconAssets.ps1`, and
`TestStoreReleaseWorkflow.ps1`, then follow the channel-specific release doc.
