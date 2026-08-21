# Microsoft Store listing

This directory is the source of truth for Captail's English and Russian
Microsoft Store listings.

- `listing.json` contains descriptions, features, search keywords, license
  terms, current release notes, and screenshot order.
- `images/` contains validated 1920 x 1080 PNG assets uploaded to both locales.
- `tools/SyncStoreListing.ps1` validates the source and synchronizes an existing
  Partner Center draft.
- `tools/New-StoreListingScreenshots.ps1` rebuilds Store-sized images from the
  latest real screenshots in `docs/`.

Do not put credentials, private paths, account identifiers, personal recordings,
or diagnostic logs in listing files or screenshots.

## Updating a release

1. Refresh real Captail screenshots in `docs/`.
2. Run `./tools/New-StoreListingScreenshots.ps1`.
3. Review every file in `images/`.
4. Update `ReleaseVersion` and both localized `ReleaseNotes` values in
   `listing.json`.
5. Run:

   ```powershell
   ./tools/SyncStoreListing.ps1 -Version 0.2.1 -ValidateOnly
   ```

6. Commit listing changes with the release.

GitHub Actions performs authenticated synchronization. Local validation never
contacts Partner Center and needs no Store credentials.
