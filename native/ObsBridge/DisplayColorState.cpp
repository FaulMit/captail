#include <Windows.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdint>
#include <cwchar>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
int32_t legacy_hdr_state(HMONITOR monitor)
{
    // Older Windows versions do not expose activeColorMode. Query current
    // output state, rather than treating Advanced Color/WCG support as HDR.
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        return -1;
    ComPtr<IDXGIAdapter> adapter;
    for (UINT i = 0; SUCCEEDED(factory->EnumAdapters(i, &adapter)); ++i) {
        ComPtr<IDXGIOutput> output;
        for (UINT j = 0; SUCCEEDED(adapter->EnumOutputs(j, &output)); ++j) {
            DXGI_OUTPUT_DESC desc{};
            if (FAILED(output->GetDesc(&desc)) || desc.Monitor != monitor)
                continue;
            ComPtr<IDXGIOutput6> output6;
            DXGI_OUTPUT_DESC1 color{};
            if (FAILED(output.As(&output6)) || FAILED(output6->GetDesc1(&color)))
                return -1;
            return color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? 1 : 0;
        }
    }
    return -1;
}
}

// Bit 0: active HDR output; bit 1: Windows "Use HDR" enabled. Keep both
// in one snapshot: the user setting may precede a display mode transition.
extern "C" __declspec(dllexport) int32_t __cdecl
captail_get_monitor_hdr_status(void *handle)
try {
    auto monitor = static_cast<HMONITOR>(handle);
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!monitor || !GetMonitorInfoW(monitor, &info))
        return -1;

    // Refresh on every query; display handles/dimensions need not change when
    // the user toggles "Use HDR" in Windows Settings or through Win+Alt+B.
    for (int attempt = 0; attempt < 3; ++attempt) {
        UINT path_count = 0, mode_count = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &path_count, &mode_count) != ERROR_SUCCESS)
            break;
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(path_count);
        std::vector<DISPLAYCONFIG_MODE_INFO> modes(mode_count);
        LONG result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &path_count, paths.data(),
            &mode_count, modes.data(), nullptr);
        if (result == ERROR_INSUFFICIENT_BUFFER)
            continue;
        if (result != ERROR_SUCCESS)
            break;
        for (UINT index = 0; index < path_count; ++index) {
            const auto &path = paths[index];
            DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
            source.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(source),
                path.sourceInfo.adapterId, path.sourceInfo.id};
            if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS ||
                wcscmp(source.viewGdiDeviceName, info.szDevice) != 0)
                continue;
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 color{};
            color.header = {DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2, sizeof(color),
                path.targetInfo.adapterId, path.targetInfo.id};
            if (DisplayConfigGetDeviceInfo(&color.header) == ERROR_SUCCESS)
                return (color.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR ? 1 : 0) |
                    (color.highDynamicRangeUserEnabled ? 2 : 0);
            int32_t legacy = legacy_hdr_state(monitor);
            // Older Windows only exposes active HDR, not the separate setting.
            return legacy < 0 ? -1 : legacy * 3;
        }
        break;
    }
    return -1;
} catch (...) {
    // Never let allocation/API failures cross the managed/native boundary.
    return -1;
}

extern "C" __declspec(dllexport) int32_t __cdecl
captail_get_monitor_hdr_state(void *handle)
{
    int32_t status = captail_get_monitor_hdr_status(handle);
    return status < 0 ? -1 : status & 1;
}
