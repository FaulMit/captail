# Microsoft Store releases

Captail Store packages are built and published by the `Microsoft Store release`
GitHub Actions workflow. Store publishing is intentionally separate from the
ordinary GitHub Release workflow.

## One-time repository setup

Create a GitHub environment named `microsoft-store-production` and add these
environment secrets:

- `AZURE_AD_TENANT_ID`
- `AZURE_AD_APPLICATION_CLIENT_ID`
- `AZURE_AD_APPLICATION_SECRET`
- `SELLER_ID`

Add this environment variable:

- `STORE_PRODUCT_ID` = `9PKVNVLKPTPS`

The Microsoft Entra application must be associated with the Partner Center
account and assigned the `Manager` role. Rotate the client secret before its
expiration date and update `AZURE_AD_APPLICATION_SECRET` in GitHub.

## Publishing an update

1. Merge the release-ready code into `main`.
2. Open **Actions** and select **Microsoft Store release**.
3. Select **Run workflow** from `main`.
4. Enter the three-part version, for example `0.2.0`.
5. Keep package revision `0` for a new visible version. Increase it when
   republishing the same visible version, for example `0.2.1` revision `1`
   produces MSIX identity version `0.2.1.1` while Captail still displays
   `0.2.1`.
6. Select one mode:
   - `build-only`: build and validate the package without contacting Partner Center.
   - `draft`: upload the package but leave the submission as a draft.
   - `submit`: upload the package and submit it for Microsoft certification.
7. Keep **Synchronize EN/RU Store text and screenshots** enabled for a normal
   release. Disable it only when intentionally testing package upload without
   changing the Store listing.
8. Review the workflow summary and Partner Center submission status.

The workflow stores the `.msixupload` package for three days. Microsoft Store
certification and rollout continue in Partner Center after the workflow ends.

## Store listing source

Store text and screenshot selection live in `store-listing/listing.json`.
Before a release:

1. Update `ReleaseVersion` and localized `ReleaseNotes` for English and Russian.
2. Capture current real-product screenshots with
   `tools/CaptureReadmeScreenshots.ps1`.
3. Generate Store-sized 1920 x 1080 images with
   `tools/New-StoreListingScreenshots.ps1`.
4. Review every generated image in `store-listing/images`.
5. Validate locally:

   ```powershell
   ./tools/SyncStoreListing.ps1 -Version 0.2.1 -ValidateOnly
   ```

The validator enforces required locales, Store text limits, PNG format,
4-10 screenshots, the 50 MB file limit, and the 1366 x 768 desktop minimum.
CI runs the same validation.

For `draft` and `submit`, the workflow uploads the package without committing,
updates the same Partner Center draft, merges the localized screenshots into
the submission bundle, and commits only after every listing step succeeds.
If synchronization fails, the submission stays an uncommitted draft.

## Safety rules

- Partner Center uploads run only from `main`.
- The `microsoft-store-production` environment gates access to Store credentials.
- External actions and the Microsoft Store CLI version are pinned.
- The package SHA-256 is verified before upload.
- Store listing metadata and screenshots are validated before Partner Center is
  contacted.
- EN/RU listing synchronization is enabled by default and never commits a
  partially updated submission.
- `build-only` is the default mode.
- Never put Microsoft Entra credentials in source files, workflow inputs, logs,
  issues, or pull requests.
