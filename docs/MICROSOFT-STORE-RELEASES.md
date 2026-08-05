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
4. Enter the three-part version, for example `0.1.8`.
5. Select one mode:
   - `build-only`: build and validate the package without contacting Partner Center.
   - `draft`: upload the package but leave the submission as a draft.
   - `submit`: upload the package and submit it for Microsoft certification.
6. Review the workflow summary and Partner Center submission status.

The workflow stores the `.msixupload` package for three days. Microsoft Store
certification and rollout continue in Partner Center after the workflow ends.

## Safety rules

- Partner Center uploads run only from `main`.
- The `microsoft-store-production` environment gates access to Store credentials.
- External actions and the Microsoft Store CLI version are pinned.
- The package SHA-256 is verified before upload.
- `build-only` is the default mode.
- Never put Microsoft Entra credentials in source files, workflow inputs, logs,
  issues, or pull requests.
