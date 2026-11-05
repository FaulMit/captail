using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Diagnostics;
using System.IO;

namespace Captail;

internal static class AppDiagnostics
{
    private static Timer? _resources;
    private static string? _outputDirectory;
    private static int _sampling;

    internal static void Install(Application application)
    {
        application.DispatcherUnhandledException += (_, args) =>
            Fatal("exception.dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Event(DiagnosticLevel.Fatal, "Runtime", "exception.unhandled",
                new { args.IsTerminating }, exception: args.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Event(DiagnosticLevel.Error, "Runtime", "exception.unobserved-task", exception: args.Exception);
            Log.Flush();
        };
        // Logging does not mark exceptions handled or change crash/recovery policy.
    }

    private static void Fatal(string name, Exception exception)
    {
        Log.Event(DiagnosticLevel.Fatal, "Runtime", name, exception: exception);
        Log.Flush();
    }

    internal static void SetOutputDirectory(string path) => Volatile.Write(ref _outputDirectory, path);
    internal static void Stop() => Interlocked.Exchange(ref _resources, null)?.Dispose();

    private static void SampleResources()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
            return;
        try
        {
            using Process process = Process.GetCurrentProcess();
            Log.Event(DiagnosticLevel.Info, "Runtime", "process.resources", new
            {
                residentBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false), process.HandleCount,
                cpuTotalMs = process.TotalProcessorTime.TotalMilliseconds,
                systemUptimeSeconds = Environment.TickCount64 / 1000,
            });
            string? output = Volatile.Read(ref _outputDirectory);
            string? root = output is null ? null : Path.GetPathRoot(Path.GetFullPath(output));
            if (string.IsNullOrEmpty(root))
                return;
            var drive = new DriveInfo(root);
            // Never query remote free space from a capture/UI thread or queue overlapping probes.
            Log.Event(DiagnosticLevel.Info, "Storage", "storage.state", new
            {
                root, type = drive.DriveType.ToString(),
                availableBytes = drive.DriveType is DriveType.Fixed or DriveType.Ram ? drive.AvailableFreeSpace : (long?)null,
            });
        }
        catch (Exception exception)
        {
            Log.Event(DiagnosticLevel.Warning, "Runtime", "resources.unavailable", new { exception.Message });
        }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    internal static void Start(IEnumerable<string> arguments)
    {
        Assembly assembly = typeof(App).Assembly;
        Log.Event(DiagnosticLevel.Info, "Runtime", "session.start", new
        {
            version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            moduleId = assembly.ManifestModule.ModuleVersionId,
            distribution = AppDistribution.IsMicrosoftStore ? "Store" : "Portable",
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            timeZone = TimeZoneInfo.Local.Id,
            executable = Environment.ProcessPath,
            preferredLogPath = Log.Path,
            // Preserve launch mode, never arbitrary command-line values or user paths.
            switches = arguments.Where(arg => arg.StartsWith("--", StringComparison.Ordinal))
                .Select(arg => arg.Split('=')[0]).ToArray(),
        });
        Log.Flush();
        _resources ??= new Timer(_ => SampleResources(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    internal static object Configuration(Config config) => new
    {
        config.CaptureMode, config.CaptureSource, config.ReplayEnabled,
        storage = config.DiskReplayBufferEnabled ? "disk" : "ram",
        config.BufferSeconds, config.FrameRate, config.BitrateMbps,
        config.Codec, config.RecordingResolution, config.AudioCodec,
        config.CaptureSystemAudio, config.CaptureMicrophone, config.SeparateAudioTracks,
        config.SystemAudioDeviceId, config.MicrophoneDeviceId, config.OutputDirectory,
        config.MonitorIndex, config.EncoderTuning,
    };
}
