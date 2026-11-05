# OBS WinRT capture shutdown guard

The four `winrt-capture.*` and `winrt-dispatch.*` files come from
[OBS Studio 32.2.2](https://github.com/obsproject/obs-studio/tree/32.2.2/libobs-winrt),
under the repository's GPL-2.0-or-later license (see the root `LICENSE`).

Captail adds only two null checks in `winrt_capture_device_loss_release`.
OBS clears the frame pool and session after release. A second release, including
video-thread shutdown after an unsuccessful rebuild, otherwise calls `Close()`
on a null WinRT object and terminates the process with an access violation.
The upstream `winrt_capture_free` path already uses these null checks.

Original SHA-256 values before the local patch:

| File | SHA-256 |
| --- | --- |
| winrt-capture.cpp | 685D104AEF86417BC6938FE1E59B82879AD298AAACAC46C9BDC0BE0143DF1191 |
| winrt-capture.h | C0F6AECB6D715279ABF4C10C12AF1723A54CD8E88C8E03DC76EA556A4858FAE4 |
| winrt-dispatch.cpp | 46C57111638F02ABF209C97BA346DFC3EBD73792188A93649519FA2393D81631 |
| winrt-dispatch.h | FD701B9D969842DCB74C9ABFB9D70B1400BFCF27C36D2F2AFD081C066BE56476 |

Build against the pinned OBS SDK and Windows SDK. The application replaces only
`libobs-winrt.dll`; all other OBS runtime files retain their pinned upstream build.

Regression: `dotnet run --project tools/QuickPanelQa -c Release -- --capture --wgc-release-twice`.
The test invokes release twice on the OBS graphics thread, then performs ordinary
engine shutdown. Unpatched OBS 32.2.2 crashes at `libobs-winrt.dll+0x190c`, matching
the reported crash dump. This is a fault-injection test; the exact preceding
sequence that left the user's capture object empty has not been established.
