using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Captail.Interop;

namespace Captail;

/// <summary>Captures the selected physical display independently of the OBS pipeline.</summary>
internal static class InstantScreenshot
{
    [DllImport("CaptailObsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int captail_capture_monitor(
        nint monitor, out nint pixels, out int width, out int height, out int format);

    internal static CaptureInterop.MonitorInfo ResolveMonitor(Config config)
    {
        List<CaptureInterop.MonitorInfo> monitors = CaptureInterop.EnumerateMonitors();
        CaptureInterop.MonitorInfo? monitor = monitors.FirstOrDefault(
            item => item.Index == config.MonitorIndex);
        return monitor ?? throw new InvalidOperationException(
            Localization.Text("L.Screenshot.MonitorUnavailable"));
    }

    internal static async Task<string> SaveAsync(Config config)
    {
        // Snapshot staged settings before dispatching work. No OBS calls or UI-thread I/O.
        Config snapshot = config.Clone();
        return await Task.Run(async () =>
        {
            BitmapSource? image = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                CaptureInterop.MonitorInfo monitor = ResolveMonitor(snapshot);
                int result = captail_capture_monitor(
                    monitor.Handle, out nint pixels, out int width, out int height, out int format);
                try
                {
                    if (result >= 0)
                    {
                        Log.Write($"Instant screenshot frame: monitor={monitor.Index + 1}, " +
                            $"size={width}x{height}, DXGI format={format}");
                        image = BitmapSource.Create(width, height, 96, 96,
                            PixelFormats.Bgra32, null, pixels,
                            checked(width * height * 4), checked(width * 4));
                        image.Freeze();
                        break;
                    }
                    // Display-mode switches invalidate duplication. Re-enumerate and retry.
                    bool transient = result is unchecked((int)0x887A0026) or
                        unchecked((int)0x887A0027) or unchecked((int)0x887A0005) or
                        unchecked((int)0x887A0007) or unchecked((int)0x887A0022);
                    if (!transient || attempt == 2)
                        throw new InvalidOperationException(Localization.Format(
                            "L.Screenshot.CaptureFailed", $"0x{result:X8}"));
                }
                finally
                {
                    if (pixels != 0)
                        Marshal.FreeCoTaskMem(pixels);
                }
                await Task.Delay(100).ConfigureAwait(false);
            }

            string directory = Path.Combine(snapshot.OutputDirectory, "Screenshots");
            Directory.CreateDirectory(directory);
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string path = Path.Combine(directory,
                $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{suffix}.png");
            string temporaryPath = path + ".tmp";
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image!));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
                    encoder.Save(stream);
                File.Move(temporaryPath, path);
                return path;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }).ConfigureAwait(false);
    }
}
