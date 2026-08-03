# Microsoft Store package

This package channel is separate from Captail's GitHub Installer and Portable
releases. Microsoft Store installs and updates this build. Captail's GitHub
self-updater is disabled at compile time and guarded again at runtime whenever
Windows reports package identity.

## Partner Center identity

- Package name: `faulmit.Captail`
- Publisher: `CN=1BD9448E-C83F-401D-B530-ED5258C6319A`
- Publisher display name: `faulmit`
- Package family name: `faulmit.Captail_ryepxmzt2jqew`
- Store product ID: `9PKVNVLKPTPS`

Do not change package name or publisher. Partner Center rejects packages whose
manifest identity does not match the reserved product.

## Build

From repository root:

```powershell
.\tools\BuildStorePackage.ps1 -Version 0.1.5
```

Output:

```text
artifacts\store\0.1.5\Captail-0.1.5.0-x64.msix
artifacts\store\0.1.5\Captail-0.1.5.0-x64.msixupload
artifacts\store\0.1.5\SHA256SUMS.txt
```

Upload `.msixupload` on Partner Center's **Packages** page. Do not publish this
artifact as a GitHub Release. Microsoft Store signs the accepted package for
distribution.

Generated package is intentionally unsigned. It is ready for Partner Center,
but Windows will not allow direct sideloading until a trusted test certificate
is applied. Store customers receive Microsoft's signed package after
certification.

## Versioning

MSIX requires four numeric components. Captail `0.1.5` becomes package version
`0.1.5.0`. Every Store submission must use a strictly higher package version.
Never reuse a version already submitted to Partner Center.

## Store-specific behavior

- Updates are delivered only by Microsoft Store.
- Footer shows `Microsoft Store` instead of an update action.
- Startup uses the package `windows.startupTask` extension.
- Installer and Portable builds continue using GitHub Releases and HKCU Run.

## Before submission

1. Build package from clean `main` commit.
2. Check `SHA256SUMS.txt` and inspect generated manifest identity.
3. Test install, first launch, tray, capture, replay save, microphone permission,
   startup toggle, uninstall, and Store update from previous package version.
4. Complete Partner Center privacy, age rating, properties, screenshots, and
   certification notes.
