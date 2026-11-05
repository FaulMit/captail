# Borderless capture preflight QA

This harness compiles the production `BorderlessCaptureAccess` source directly.
It does not initialize OBS, capture frames, or require a hardware video encoder.

```powershell
dotnet run --project tools/BorderlessCaptureQa -c Release
dotnet run --project tools/BorderlessCaptureQa -c Release -- --probe --output result.json
```

Default mode tests missing APIs, every access status, unknown statuses, metadata
failure, request failure, and timeout. `--probe` calls the real Windows API on the
WPF dispatcher and records OS version, package identity, support, access status,
and readiness. The probe may trigger the normal Windows consent prompt.

Publish self-contained to test a Windows 10 VM without an installed .NET SDK:

```powershell
dotnet publish tools/BorderlessCaptureQa -c Release -r win-x64 --self-contained true -o .qa/borderless-probe
```

For the Windows 11 desktop-app permission test, launch a fresh probe process with
"Allow desktop apps to turn off the screenshot border" enabled, disabled, and
enabled again. Record each result separately. This verifies preflight responses;
it does not prove that a WGC capture session has no visible border. Packaged-app
permissions must be tested under that package identity separately.
