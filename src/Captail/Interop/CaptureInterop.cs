using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

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

    public static string BuildObsWindowSelector(string executablePath)
    {
        Process? target = FindProcess(executablePath, requireWindow: true);
        if (target is null)
            return $"::{Encode(Path.GetFileName(executablePath))}";

        using (target)
        {
            var className = new StringBuilder(256);
            _ = GetClassName(target.MainWindowHandle, className, className.Capacity);
            return $"{Encode(target.MainWindowTitle)}:{Encode(className.ToString())}:" +
                   $"{Encode(Path.GetFileName(executablePath))}";
        }
    }

    public static bool IsProcessRunning(string executablePath)
    {
        using Process? process = FindProcess(executablePath, requireWindow: false);
        return process is not null;
    }

    public static (int Width, int Height)? GetGameClientSize(string executablePath)
    {
        using Process? process = FindProcess(executablePath, requireWindow: true);
        if (process is null ||
            !GetClientRect(process.MainWindowHandle, out Rect rect))
        {
            return null;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static Process? FindProcess(string executablePath, bool requireWindow)
    {
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if ((!requireWindow || process.MainWindowHandle != 0) &&
                    string.Equals(
                        process.MainModule?.FileName,
                        executablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                // Protected system processes cannot be the selected game.
            }

            process.Dispose();
        }

        return null;
    }

    private static string Encode(string value) =>
        value.Replace("#", "#22", StringComparison.Ordinal)
            .Replace(":", "#3A", StringComparison.Ordinal);
}
