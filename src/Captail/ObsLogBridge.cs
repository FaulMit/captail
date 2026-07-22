using System.Runtime.InteropServices;

namespace Captail;

internal static class ObsLogBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(
        int level,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    private static readonly LogCallback Callback = Write;

    [DllImport("CaptailObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool captail_install_obs_log_handler(LogCallback callback);

    [DllImport("CaptailObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void captail_remove_obs_log_handler();

    internal static bool Install()
    {
        bool installed = captail_install_obs_log_handler(Callback);
        if (!installed)
            Log.Write("OBS log bridge could not be installed.");
        return installed;
    }

    internal static void Remove() => captail_remove_obs_log_handler();

    private static void Write(int level, string message) =>
        Log.Write($"libobs[{level}]: {message}");
}
