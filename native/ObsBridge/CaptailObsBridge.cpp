#include <Windows.h>
#include <dxgi1_6.h>
#include <atomic>
#include <cstdint>
#include <cstdarg>
#include <cstdio>

using managed_log_callback = void(__cdecl *)(int level, const char *message);
using obs_log_handler = void(__cdecl *)(int level, const char *format, va_list args, void *context);
using base_set_log_handler_proc = void(__cdecl *)(obs_log_handler handler, void *context);

namespace {
std::atomic<managed_log_callback> callback{nullptr};

void __cdecl obs_log(int level, const char *format, va_list args, void *)
{
    managed_log_callback current = callback.load(std::memory_order_acquire);
    if (!current || !format)
        return;

    char message[8192]{};
    vsnprintf_s(message, sizeof(message), _TRUNCATE, format, args);
    current(level, message);
}

base_set_log_handler_proc get_setter()
{
    HMODULE obs = GetModuleHandleW(L"obs.dll");
    return obs
        ? reinterpret_cast<base_set_log_handler_proc>(
              GetProcAddress(obs, "base_set_log_handler"))
        : nullptr;
}
} // namespace

extern "C" __declspec(dllexport) bool __cdecl
captail_install_obs_log_handler(managed_log_callback managed_callback)
{
    auto setter = get_setter();
    if (!setter)
        return false;
    callback.store(managed_callback, std::memory_order_release);
    setter(obs_log, nullptr);
    return true;
}

extern "C" __declspec(dllexport) void __cdecl captail_remove_obs_log_handler()
{
    auto setter = get_setter();
    if (setter)
        setter(nullptr, nullptr);
    callback.store(nullptr, std::memory_order_release);
}

extern "C" __declspec(dllexport) int32_t __cdecl
captail_get_high_performance_adapter_index()
{
    IDXGIFactory6 *factory = nullptr;
    HRESULT result = CreateDXGIFactory1(
        __uuidof(IDXGIFactory6),
        reinterpret_cast<void **>(&factory));
    if (FAILED(result) || !factory)
        return -1;

    IDXGIAdapter1 *preferred = nullptr;
    result = factory->EnumAdapterByGpuPreference(
        0,
        DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
        __uuidof(IDXGIAdapter1),
        reinterpret_cast<void **>(&preferred));
    if (FAILED(result) || !preferred) {
        factory->Release();
        return -1;
    }

    DXGI_ADAPTER_DESC1 preferred_desc{};
    result = preferred->GetDesc1(&preferred_desc);
    preferred->Release();
    if (FAILED(result) ||
        (preferred_desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) {
        factory->Release();
        return -1;
    }

    int32_t selected_index = -1;
    for (UINT index = 0;; ++index) {
        IDXGIAdapter1 *candidate = nullptr;
        result = factory->EnumAdapters1(index, &candidate);
        if (result == DXGI_ERROR_NOT_FOUND)
            break;
        if (FAILED(result) || !candidate)
            continue;

        DXGI_ADAPTER_DESC1 candidate_desc{};
        result = candidate->GetDesc1(&candidate_desc);
        candidate->Release();
        if (SUCCEEDED(result) &&
            candidate_desc.AdapterLuid.HighPart == preferred_desc.AdapterLuid.HighPart &&
            candidate_desc.AdapterLuid.LowPart == preferred_desc.AdapterLuid.LowPart) {
            selected_index = static_cast<int32_t>(index);
            break;
        }
    }

    factory->Release();
    return selected_index;
}
