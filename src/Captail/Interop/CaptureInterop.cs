using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Captail.Interop;

internal static class CaptureInterop
{
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clip,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoNative info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoNative
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;
        internal uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;
    }

    private const uint EddGetDeviceInterfaceName = 1;

    public sealed record MonitorInfo(
        nint Handle,
        int Width,
        int Height,
        int Index,
        string DeviceId);

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect rect, nint _) =>
        {
            string deviceId = "";
            var monitorInfo = new MonitorInfoNative
            {
                Size = (uint)Marshal.SizeOf<MonitorInfoNative>(),
                Device = "",
            };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var display = new DisplayDevice
                {
                    Size = (uint)Marshal.SizeOf<DisplayDevice>(),
                    DeviceName = "",
                    DeviceString = "",
                    DeviceId = "",
                    DeviceKey = "",
                };
                if (EnumDisplayDevices(
                        monitorInfo.Device,
                        0,
                        ref display,
                        EddGetDeviceInterfaceName))
                {
                    deviceId = display.DeviceId;
                }
            }

            monitors.Add(new MonitorInfo(
                monitor,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                monitors.Count,
                deviceId));
            return true;
        }, 0);
        return monitors;
    }

    public static string ForegroundExecutable()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint processId) == 0)
            return "";
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "";
        }
    }

}
