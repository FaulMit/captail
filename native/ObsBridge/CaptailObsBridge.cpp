#include <Windows.h>
#include <atomic>
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
