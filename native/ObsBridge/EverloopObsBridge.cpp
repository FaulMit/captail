#include <Windows.h>
#include <cstdarg>
#include <cstdio>

using managed_log_callback = void(__cdecl *)(int level, const char *message);
using obs_log_handler = void(__cdecl *)(int level, const char *format, va_list args, void *context);
using base_set_log_handler_proc = void(__cdecl *)(obs_log_handler handler, void *context);

namespace {
managed_log_callback callback = nullptr;

void __cdecl obs_log(int level, const char *format, va_list args, void *)
{
    if (!callback || !format)
        return;

    char message[8192]{};
    vsnprintf_s(message, sizeof(message), _TRUNCATE, format, args);
    callback(level, message);
}

base_set_log_handler_proc get_setter()
{
    HMODULE obs = GetModuleHandleW(L"obs.dll");
    if (!obs)
        obs = LoadLibraryW(L"obs.dll");
    return obs
        ? reinterpret_cast<base_set_log_handler_proc>(
              GetProcAddress(obs, "base_set_log_handler"))
        : nullptr;
}
} // namespace

extern "C" __declspec(dllexport) bool __cdecl
everloop_install_obs_log_handler(managed_log_callback managed_callback)
{
    auto setter = get_setter();
    if (!setter)
        return false;
    callback = managed_callback;
    setter(obs_log, nullptr);
    return true;
}

extern "C" __declspec(dllexport) void __cdecl everloop_remove_obs_log_handler()
{
    auto setter = get_setter();
    if (setter)
        setter(nullptr, nullptr);
    callback = nullptr;
}
