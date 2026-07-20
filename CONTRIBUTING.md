# Contributing to Captail

Captail is an early project. Bug reports, hardware compatibility results, documentation fixes, and focused pull requests are welcome.

## Bug reports

Use the GitHub bug report form and include:

- Captail version and package type
- Windows version
- GPU model and graphics-driver version
- Capture source, codec, resolution, and FPS
- Audio configuration
- Exact reproduction steps
- Expected and actual behavior

Logs are stored at `%APPDATA%\Captail\log.txt`. Review logs before attaching them and remove personal paths or other sensitive information.

## Development setup

Requirements:

- Windows 10/11 x64
- .NET 9 SDK
- Visual Studio 2022 Build Tools with Desktop development with C++
- CMake 3.20+

```powershell
.\tools\AcquireObsRuntime.ps1
dotnet build .\Captail.sln -c Debug
```

## Pull requests

1. Open or reference an issue for non-trivial changes.
2. Keep changes focused.
3. Preserve existing architecture unless a change has a concrete benefit.
4. Build the Release configuration before submitting.
5. Test the real Windows UI for interface changes.
6. Describe GPU, codec, capture source, and FPS used for recording changes.
7. Do not commit OBS runtime files, build output, recordings, logs, or personal configuration.

## Code style

- Follow existing C# and XAML conventions.
- Use English for GitHub issues, pull requests, documentation, code comments, and diagnostic messages.
- Keep Russian text only in the Russian localization dictionary.
- Prefer clear behavior over abstraction.
- Keep user-facing text in EN/RU localization dictionaries.
- Do not present unavailable hardware functionality as enabled.
- Keep recovery failures visible to the user and useful in logs.

By contributing, you agree that your contribution is licensed under the repository license.
