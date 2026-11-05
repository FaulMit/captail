using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Captail;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 2 || !System.IO.File.Exists(args[0]) || !System.IO.File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: PlayerSyncQa <clip-path> <libmpv-path> [single]");
            Environment.ExitCode = 2;
            return;
        }
        NativeLibrary.SetDllImportResolver(typeof(MpvHost).Assembly, (name, _, _) =>
            name == "libmpv-2.dll" ? NativeLibrary.Load(System.IO.Path.GetFullPath(args[1])) : 0);
        var app = new Application();
        var host = new MpvHost();
        var window = new Window { Title = "Captail seek synchronization diagnostic", Width = 800, Height = 500, Content = host };
        int result = 0;
        window.Loaded += async (_, _) =>
        {
            int verdict = 0;
            try
            {
                int[] tracks = args.Length > 2 && args[2] == "single" ? [1] : [1, 2, 3];
                var field = typeof(MpvHost).GetField("_mpvHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
                nint handle = (nint)field.GetValue(host)!;
                var native = typeof(MpvHost).GetNestedType("MpvNative", BindingFlags.NonPublic)!;
                var getter = native.GetMethod("GetPropertyStringValue", BindingFlags.Static | BindingFlags.NonPublic)!;
                string Read(string name) => (string?)getter.Invoke(null, [handle, name]) ?? "unavailable";
                double Number(string name) => double.TryParse(Read(name), CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
                await host.LoadAsync(args[0], TimeSpan.Zero, tracks);
                host.SetVolumePercent(0);
                host.Play();
                Console.WriteLine($"CONFIG tracks={tracks.Length} hwdec={Read("hwdec-current")} framedrop={Read("framedrop")} fps={Read("container-fps")}");
                double duration = Number("duration");
                if (!double.IsFinite(duration) || duration < 8)
                    throw new ArgumentException("Synchronization QA requires a clip of at least eight seconds.");
                double span = Math.Min(25, duration - 3);
                foreach (double target in new double[] { 0, span * 0.6, span * 0.16, span * 0.8, span * 0.32 })
                {
                    if (target != 0)
                    {
                        // Match timeline/progress controls: pause, seek, resume.
                        host.Pause();
                        host.Seek(target, exact: true);
                        host.Play();
                    }
                    double maximum = 0;
                    double maximumClockDifference = 0;
                    int samples = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(150);
                        double sync = Number("avsync");
                        double clocks = Math.Abs(Number("time-pos") - Number("audio-pts"));
                        if (i >= 6 && Read("seeking") == "no" && double.IsFinite(sync) && double.IsFinite(clocks))
                        {
                            maximum = Math.Max(maximum, Math.Abs(sync));
                            maximumClockDifference = Math.Max(maximumClockDifference, clocks);
                            samples++;
                        }
                        if (i is 6 or 19)
                            Console.WriteLine($"SAMPLE target={target} seeking={Read("seeking")} video={Read("time-pos")} audio={Read("audio-pts")} avsync={Read("avsync")} hwdec={Read("hwdec-current")} drops={Read("decoder-frame-drop-count")}/{Read("frame-drop-count")}");
                    }
                    bool passed = samples >= 5 && maximum < 0.1 && maximumClockDifference < 0.1;
                    Console.WriteLine(FormattableString.Invariant($"SEEK_SYNC {(passed ? "PASS" : "FAIL")} target={target:F3} maxAbsSync={maximum:F6} maxClockDifference={maximumClockDifference:F6} samples={samples}"));
                    if (!passed) verdict = 1;
                }
            }
            catch (Exception exception) { Console.WriteLine(exception); verdict = 2; }
            finally { result = verdict; host.Shutdown(); window.Close(); app.Shutdown(); }
        };
        app.Run(window);
        Environment.ExitCode = result;
    }
}

namespace Captail
{
    internal static class Log
    {
        internal static void Write(string message) => Console.WriteLine(message);
    }
}
