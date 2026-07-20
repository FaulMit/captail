using System.Runtime.InteropServices;

namespace InstantReplay;

internal static class ObsLogBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(
        int level,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    private static readonly LogCallback Callback = Write;

    [DllImport("EverloopObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool everloop_install_obs_log_handler(LogCallback callback);

    [DllImport("EverloopObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void everloop_remove_obs_log_handler();

    internal static bool Install()
    {
        bool installed = everloop_install_obs_log_handler(Callback);
        if (!installed)
            Log.Write("OBS log bridge не установился.");
        return installed;
    }

    internal static void Remove() => everloop_remove_obs_log_handler();

    private static void Write(int level, string message) =>
        Log.Write($"libobs[{level}]: {message}");
}
