using System.Runtime.InteropServices;

namespace Captail;

internal static class DisplayColorState
{
    [DllImport("CaptailObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int captail_get_monitor_hdr_status(nint monitor);

    internal readonly record struct HdrState(bool UserEnabled, bool Active,
        long Revision = 0, long LastSdrRevision = 0, long LastEnableRevision = 0);

    internal static HdrState? ReadHdrState(nint monitor)
    {
        int status = captail_get_monitor_hdr_status(monitor);
        return status < 0 ? null : new HdrState((status & 2) != 0, (status & 1) != 0);
    }

    // null means the display is temporarily unavailable during a mode change.
    internal static bool? IsHdrEnabled(nint monitor) => ReadHdrState(monitor)?.Active;
}
