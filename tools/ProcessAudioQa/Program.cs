using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Captail;
using Captail.Interop;

namespace ProcessAudioQa;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            Directory.CreateDirectory(options.OutputDirectory);

            using var session = new ObsSession(options);
            _ = options.Sources
                .Select(source => session.CreateProbe(source, options.Frequencies))
                .ToList();

            Console.WriteLine($"READY elapsed_ms={session.ElapsedMilliseconds}");
            long nextStatus = 1_000;
            while (session.ElapsedMilliseconds < options.Duration.TotalMilliseconds)
            {
                Thread.Sleep(50);
                session.RefreshWatchedProcesses();
                if (session.ElapsedMilliseconds < nextStatus)
                    continue;

                foreach (AudioProbe probe in session.ActiveProbes)
                    Console.WriteLine(probe.StatusLine(session.ElapsedMilliseconds));
                nextStatus += 1_000;
            }

            QaReport report = session.CreateReport();
            string reportPath = Path.Combine(options.OutputDirectory, options.ReportName);
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            foreach (AudioProbe probe in session.Probes)
                probe.WriteWaveFile(options.OutputDirectory);

            Console.WriteLine($"REPORT {reportPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed record SourceRule(
    string Label,
    string Executable,
    ProcessIdentity? Identity = null);

internal sealed class Options
{
    internal required string RuntimeRoot { get; init; }
    internal required string OutputDirectory { get; init; }
    internal required string ReportName { get; init; }
    internal required TimeSpan Duration { get; init; }
    internal required IReadOnlyList<SourceRule> Sources { get; init; }
    internal required IReadOnlyList<double> Frequencies { get; init; }
    internal required IReadOnlyList<string> WatchedExecutables { get; init; }
    internal long CreationTimeOffset { get; init; }
    internal string? ProcessAudioPluginPath { get; init; }
    internal string? LogBridgePath { get; init; }

    internal static Options Parse(string[] args)
    {
        string repositoryRoot = FindRepositoryRoot();
        string runtimeRoot = Path.Combine(repositoryRoot, "runtime", "obs");
        string outputDirectory = Path.Combine(repositoryRoot, ".qa", "process-audio");
        string reportName = $"report-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        TimeSpan duration = TimeSpan.FromSeconds(15);
        var sources = new List<SourceRule>();
        var frequencies = new List<double> { 440, 523.25, 659.25, 880, 997 };
        var watchedExecutables = new List<string>();
        string? bridgePath = null;
        string? processAudioPluginPath = null;
        long creationTimeOffset = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string value = NextValue(args, ref i);
            switch (args[i - 1])
            {
                case "--runtime":
                    runtimeRoot = Path.GetFullPath(value);
                    break;
                case "--output":
                    outputDirectory = Path.GetFullPath(value);
                    break;
                case "--report":
                    reportName = value;
                    break;
                case "--duration":
                    duration = TimeSpan.FromSeconds(
                        double.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "--source":
                    int separator = value.IndexOf('=');
                    if (separator <= 0 || separator == value.Length - 1)
                        throw new ArgumentException("--source must be label=executable.exe");
                    sources.Add(new SourceRule(value[..separator], value[(separator + 1)..]));
                    break;
                case "--frequency":
                    frequencies.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "--bridge":
                    bridgePath = Path.GetFullPath(value);
                    break;
                case "--plugin":
                    processAudioPluginPath = Path.GetFullPath(value);
                    break;
                case "--watch-executable":
                    watchedExecutables.Add(value);
                    break;
                case "--creation-time-offset":
                    creationTimeOffset = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i - 1]}");
            }
        }

        if (sources.Count == 0 && watchedExecutables.Count == 0)
        {
            throw new ArgumentException(
                "At least one --source or --watch-executable is required.");
        }
        if (!File.Exists(Path.Combine(runtimeRoot, "bin", "obs.dll")))
            throw new FileNotFoundException("OBS runtime was not found.", runtimeRoot);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        return new Options
        {
            RuntimeRoot = runtimeRoot,
            OutputDirectory = outputDirectory,
            ReportName = reportName,
            Duration = duration,
            Sources = sources,
            Frequencies = frequencies.Distinct().Order().ToArray(),
            WatchedExecutables = watchedExecutables,
            CreationTimeOffset = creationTimeOffset,
            ProcessAudioPluginPath = processAudioPluginPath,
            LogBridgePath = bridgePath,
        };
    }

    private static string NextValue(string[] args, ref int index)
    {
        string name = args[index];
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {name}");
        index++;
        return args[index];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Captail.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Captail.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Captail repository root was not found.");
    }
}

internal sealed class ObsSession : IDisposable
{
    private readonly Options _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<AudioProbe> _probes = [];
    private readonly HashSet<nint> _activeProbeSources = [];
    private readonly ConcurrentQueue<ObsLogEntry> _logs = new();
    private readonly nint _dllDirectoryCookie;
    private readonly nint _obsLibrary;
    private readonly QaLogBridge? _logBridge;
    private readonly ProcessAudioReconciler? _reconciler;
    private long _nextReconcileMilliseconds;
    private int _dynamicProbeNumber;
    private bool _obsStarted;
    private bool _disposed;

    internal ObsSession(Options options)
    {
        _options = options;
        string binDirectory = Path.Combine(options.RuntimeRoot, "bin");
        NativeMethods.SetDefaultDllDirectories(
            NativeMethods.LoadLibrarySearchDefaultDirs |
            NativeMethods.LoadLibrarySearchUserDirs);
        _dllDirectoryCookie = NativeMethods.AddDllDirectory(binDirectory);
        if (_dllDirectoryCookie == 0)
            throw new InvalidOperationException("Could not add the OBS runtime DLL directory.");

        _obsLibrary = NativeLibrary.Load(Path.Combine(binDirectory, "obs.dll"));
        NativeLibrary.SetDllImportResolver(
            typeof(ObsNative).Assembly,
            (libraryName, _, _) => string.Equals(
                libraryName,
                "obs.dll",
                StringComparison.OrdinalIgnoreCase)
                    ? _obsLibrary
                    : 0);
        if (!string.IsNullOrWhiteSpace(options.LogBridgePath))
            _logBridge = new QaLogBridge(options.LogBridgePath, _clock, _logs);

        string configDirectory = Path.Combine(options.OutputDirectory, "obs-config");
        Directory.CreateDirectory(configDirectory);
        _obsStarted = ObsNative.obs_startup("en-US", configDirectory, 0);
        if (!_obsStarted)
            throw new InvalidOperationException("obs_startup failed.");

        string version = Marshal.PtrToStringUTF8(ObsNative.obs_get_version_string()) ?? "unknown";
        Console.WriteLine($"OBS version={version}");

        string dataRoot = Path.Combine(options.RuntimeRoot, "data");
        ObsNative.obs_add_data_path(ToObsPath(Path.Combine(dataRoot, "libobs")) + "/");
        ResetVideo(binDirectory);

        var audio = new ObsNative.AudioInfo
        {
            SamplesPerSecond = 48_000,
            Speakers = ObsNative.SpeakerLayout.Stereo,
        };
        if (!ObsNative.obs_reset_audio(ref audio))
            throw new InvalidOperationException("obs_reset_audio failed.");

        if (options.Sources.Count > 0)
        {
            LoadModule(
                Path.Combine(options.RuntimeRoot, "obs-plugins", "64bit", "win-wasapi.dll"),
                Path.Combine(dataRoot, "obs-plugins", "win-wasapi"));
        }
        if (options.WatchedExecutables.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(options.ProcessAudioPluginPath) ||
                !File.Exists(options.ProcessAudioPluginPath))
            {
                throw new FileNotFoundException(
                    "The PID-aware OBS plugin was not found.",
                    options.ProcessAudioPluginPath);
            }
            LoadModule(options.ProcessAudioPluginPath, options.OutputDirectory);
        }
        ObsNative.obs_post_load_modules();

        if (options.Sources.Count > 0 &&
            !HasInputType("wasapi_process_output_capture"))
            throw new InvalidOperationException("wasapi_process_output_capture was not registered.");
        if (options.WatchedExecutables.Count > 0)
        {
            if (!HasInputType("captail_process_audio_capture"))
            {
                throw new InvalidOperationException(
                    "captail_process_audio_capture was not registered.");
            }
            _reconciler = new ProcessAudioReconciler(
                CreatePidProbe,
                DestroyProbe,
                message => Console.WriteLine($"RECONCILE {message}"));
        }
    }

    internal long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
    internal IReadOnlyList<AudioProbe> Probes => _probes;
    internal IEnumerable<AudioProbe> ActiveProbes =>
        _probes.Where(probe => _activeProbeSources.Contains(probe.Source));

    internal void RefreshWatchedProcesses()
    {
        if (_reconciler is null ||
            _clock.ElapsedMilliseconds < _nextReconcileMilliseconds)
        {
            return;
        }

        _nextReconcileMilliseconds = _clock.ElapsedMilliseconds + 250;
        ProcessSnapshot snapshot = Task.Run(ProcessSnapshot.Capture)
            .GetAwaiter()
            .GetResult();
        ProcessAudioReconcileResult result = _reconciler.Reconcile(
            snapshot,
            _options.WatchedExecutables.Select(executable =>
                new ProcessAudioTarget(executable, 1)));
        Console.WriteLine(
            $"RECONCILE elapsed_ms={_clock.ElapsedMilliseconds} " +
            $"desired={result.DesiredSources} active={result.ActiveSources} " +
            $"created={result.CreatedSources} destroyed={result.DestroyedSources} " +
            $"failed={result.FailedSources}");
    }

    internal AudioProbe CreateProbe(SourceRule rule, IReadOnlyList<double> frequencies)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            string sourceId;
            if (rule.Identity is ProcessIdentity identity)
            {
                sourceId = "captail_process_audio_capture";
                ObsNative.obs_data_set_int(settings, "target_pid", identity.ProcessId);
                ObsNative.obs_data_set_int(
                    settings,
                    "target_creation_time",
                    identity.CreationTime);
            }
            else
            {
                sourceId = "wasapi_process_output_capture";
                ObsNative.obs_data_set_string(settings, "window", $"::{rule.Executable}");
                ObsNative.obs_data_set_int(settings, "priority", 2);
            }
            nint source = ObsNative.obs_source_create(
                sourceId,
                $"Process Audio QA - {rule.Label}",
                settings,
                0);
            if (source == 0)
                throw new InvalidOperationException($"Could not create source for {rule.Executable}.");
            if (rule.Identity is not null && !HasProcessAudioStatus(source))
            {
                ObsNative.obs_source_remove(source);
                ObsNative.obs_source_release(source);
                throw new InvalidOperationException("The PID-aware source rejected its identity.");
            }

            var probe = new AudioProbe(rule, source, _clock, frequencies);
            _probes.Add(probe);
            _activeProbeSources.Add(source);
            ObsNative.obs_source_add_audio_capture_callback(source, probe.Callback, 0);
            ObsNative.obs_source_inc_active(source);
            return probe;
        }
        finally
        {
            ObsNative.obs_data_release(settings);
        }
    }

    internal QaReport CreateReport() => new(
        DateTimeOffset.Now,
        Marshal.PtrToStringUTF8(ObsNative.obs_get_version_string()) ?? "unknown",
        _clock.ElapsedMilliseconds,
        _probes.Select(probe => probe.CreateResult()).ToArray(),
        _logs.ToArray());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _reconciler?.Dispose();
        foreach (AudioProbe probe in Enumerable.Reverse(_probes))
            DestroyProbe(probe.Source);
        _probes.Clear();
        Console.WriteLine($"CLEAN_SOURCES elapsed_ms={_clock.ElapsedMilliseconds}");

        if (_obsStarted)
        {
            ObsNative.obs_wait_for_destroy_queue();
            Console.WriteLine($"CLEAN_DESTROY_QUEUE elapsed_ms={_clock.ElapsedMilliseconds}");
            ObsNative.obs_shutdown();
            Console.WriteLine($"CLEAN_OBS_SHUTDOWN elapsed_ms={_clock.ElapsedMilliseconds}");
            _obsStarted = false;
        }

        _logBridge?.Dispose();
        Console.WriteLine($"CLEAN_LOG_BRIDGE elapsed_ms={_clock.ElapsedMilliseconds}");
        NativeLibrary.Free(_obsLibrary);
        if (_dllDirectoryCookie != 0)
            NativeMethods.RemoveDllDirectory(_dllDirectoryCookie);
        Console.WriteLine($"CLEAN elapsed_ms={_clock.ElapsedMilliseconds}");
    }

    private static bool HasInputType(string expected)
    {
        for (nuint index = 0; ObsNative.obs_enum_input_types(index, out nint id); index++)
        {
            if (string.Equals(Marshal.PtrToStringUTF8(id), expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasProcessAudioStatus(nint source)
    {
        nint handler = ObsNative.obs_source_get_proc_handler(source);
        if (handler == 0)
            return false;

        const int stackSize = 256;
        nint stack = Marshal.AllocHGlobal(stackSize);
        try
        {
            for (int offset = 0; offset < stackSize; offset += sizeof(long))
                Marshal.WriteInt64(stack, offset, 0);
            var callData = new ObsNative.CallData
            {
                Stack = stack,
                Size = (nuint)nint.Size,
                Capacity = stackSize,
                Fixed = true,
            };
            return ObsNative.proc_handler_call(handler, "get_status", ref callData) &&
                   ObsNative.calldata_get_data(
                       ref callData,
                       "state",
                       out long _,
                       sizeof(long));
        }
        finally
        {
            Marshal.FreeHGlobal(stack);
        }
    }

    private nint CreatePidProbe(ProcessIdentity identity, int _)
    {
        ProcessIdentity sourceIdentity = identity with
        {
            CreationTime = checked(identity.CreationTime + _options.CreationTimeOffset),
        };
        SourceRule rule = new(
            $"pid-root-{++_dynamicProbeNumber}",
            "pid-aware",
            sourceIdentity);
        return CreateProbe(rule, _options.Frequencies).Source;
    }

    private void DestroyProbe(nint source)
    {
        if (!_activeProbeSources.Remove(source))
            return;
        AudioProbe? probe = _probes.FirstOrDefault(candidate => candidate.Source == source);
        if (probe is not null)
        {
            ObsNative.obs_source_remove_audio_capture_callback(
                source,
                probe.Callback,
                0);
            probe.MarkDestroyed();
        }
        ObsNative.obs_source_dec_active(source);
        ObsNative.obs_source_remove(source);
        ObsNative.obs_source_release(source);
    }

    private static void LoadModule(string modulePath, string dataPath)
    {
        int openResult = ObsNative.obs_open_module(
            out nint module,
            ToObsPath(modulePath),
            ToObsPath(dataPath));
        if (openResult != 0 || module == 0 || !ObsNative.obs_init_module(module))
        {
            throw new InvalidOperationException(
                $"Could not load OBS module (result {openResult}).");
        }
    }

    private static void ResetVideo(string binDirectory)
    {
        nint graphicsModule = Marshal.StringToCoTaskMemUTF8(
            Path.Combine(binDirectory, "libobs-d3d11.dll"));
        try
        {
            var video = new ObsNative.VideoInfo
            {
                GraphicsModule = graphicsModule,
                FpsNum = 30,
                FpsDen = 1,
                BaseWidth = 640,
                BaseHeight = 360,
                OutputWidth = 640,
                OutputHeight = 360,
                OutputFormat = ObsNative.VideoFormat.Nv12,
                Adapter = 0,
                GpuConversion = true,
                ColorSpace = ObsNative.VideoColorSpace.Cs709,
                VideoRange = ObsNative.VideoRange.Partial,
                ScaleType = ObsNative.ScaleType.Bilinear,
            };
            int result = ObsNative.obs_reset_video(ref video);
            if (result != 0)
                throw new InvalidOperationException($"obs_reset_video failed with code {result}.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(graphicsModule);
        }
    }

    private static string ToObsPath(string path) => path.Replace('\\', '/');
}

internal sealed class AudioProbe
{
    private const double SignalThreshold = 0.002;
    private readonly object _gate = new();
    private readonly Stopwatch _clock;
    private readonly IReadOnlyList<double> _frequencies;
    private readonly List<float[]> _blocks = [];
    private readonly List<SignalInterval> _intervals = [];
    private long? _firstCallbackMs;
    private long? _firstSignalMs;
    private long? _lastSignalMs;
    private long _callbackBlocks;
    private long _nonSilentBlocks;
    private long _frames;
    private double _maxRms;
    private bool _destroyed;

    internal AudioProbe(
        SourceRule rule,
        nint source,
        Stopwatch clock,
        IReadOnlyList<double> frequencies)
    {
        Rule = rule;
        Source = source;
        _clock = clock;
        _frequencies = frequencies;
        Callback = OnAudio;
    }

    internal SourceRule Rule { get; }
    internal nint Source { get; }
    internal ObsNative.AudioCaptureCallback Callback { get; }

    internal string StatusLine(long elapsedMilliseconds)
    {
        lock (_gate)
        {
            return $"STATUS elapsed_ms={elapsedMilliseconds} label={Rule.Label} " +
                   $"active={!_destroyed && ObsNative.obs_source_active(Source)} callbacks={_callbackBlocks} " +
                   $"signal_blocks={_nonSilentBlocks} last_signal_ms={_lastSignalMs?.ToString() ?? "none"} " +
                   $"max_rms={_maxRms:F6}";
        }
    }

    internal ProbeResult CreateResult()
    {
        List<float[]> blocks;
        lock (_gate)
        {
            blocks = [.. _blocks];
            if (_lastSignalMs is long last &&
                (_intervals.Count == 0 || _intervals[^1].EndMs is null))
            {
                SignalInterval open = _intervals[^1];
                _intervals[^1] = open with { EndMs = last };
            }
        }

        IReadOnlyDictionary<string, double> amplitudes = AnalyzeFrequencies(blocks, _frequencies);
        lock (_gate)
        {
            return new ProbeResult(
                Rule.Label,
                Rule.Executable,
                Rule.Identity?.ProcessId,
                Rule.Identity?.CreationTime,
                !_destroyed && ObsNative.obs_source_active(Source),
                _firstCallbackMs,
                _firstSignalMs,
                _lastSignalMs,
                _callbackBlocks,
                _nonSilentBlocks,
                _frames,
                _maxRms,
                [.. _intervals],
                amplitudes);
        }
    }

    internal void MarkDestroyed()
    {
        lock (_gate)
            _destroyed = true;
    }

    internal void WriteWaveFile(string outputDirectory)
    {
        List<float[]> blocks;
        lock (_gate)
            blocks = [.. _blocks];
        if (blocks.Count == 0)
            return;

        string safeLabel = string.Concat(Rule.Label.Select(
            character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string path = Path.Combine(outputDirectory, $"{safeLabel}.wav");
        int sampleCount = blocks.Sum(block => block.Length);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleCount * sizeof(float));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)3);
        writer.Write((ushort)1);
        writer.Write(48_000);
        writer.Write(48_000 * sizeof(float));
        writer.Write((ushort)sizeof(float));
        writer.Write((ushort)32);
        writer.Write("data"u8);
        writer.Write(sampleCount * sizeof(float));
        foreach (float[] block in blocks)
        {
            foreach (float sample in block)
                writer.Write(sample);
        }
    }

    private void OnAudio(nint parameter, nint source, nint audioDataPointer, bool muted)
    {
        if (muted || audioDataPointer == 0)
            return;

        ObsNative.AudioData data = Marshal.PtrToStructure<ObsNative.AudioData>(audioDataPointer);
        if (data.Data0 == 0 || data.Frames == 0 || data.Frames > 48_000)
            return;

        var samples = new float[data.Frames];
        Marshal.Copy(data.Data0, samples, 0, samples.Length);
        double sumSquares = 0;
        foreach (float sample in samples)
            sumSquares += sample * sample;
        double rms = Math.Sqrt(sumSquares / samples.Length);
        long elapsed = _clock.ElapsedMilliseconds;

        lock (_gate)
        {
            _firstCallbackMs ??= elapsed;
            _callbackBlocks++;
            _frames += samples.Length;
            _maxRms = Math.Max(_maxRms, rms);
            _blocks.Add(samples);

            if (rms < SignalThreshold)
                return;

            _firstSignalMs ??= elapsed;
            _nonSilentBlocks++;
            if (_lastSignalMs is null || elapsed - _lastSignalMs > 500)
            {
                if (_intervals.Count > 0 && _intervals[^1].EndMs is null)
                    _intervals[^1] = _intervals[^1] with { EndMs = _lastSignalMs };
                _intervals.Add(new SignalInterval(elapsed, null));
            }
            _lastSignalMs = elapsed;
        }
    }

    private static IReadOnlyDictionary<string, double> AnalyzeFrequencies(
        IReadOnlyList<float[]> blocks,
        IReadOnlyList<double> frequencies)
    {
        var totals = frequencies.ToDictionary(frequency => frequency, _ => 0.0);
        double normalization = 0;
        foreach (float[] block in blocks)
        {
            if (block.Length == 0)
                continue;
            double windowSum = 0;
            foreach (double frequency in frequencies)
            {
                double sin = 0;
                double cos = 0;
                double angular = 2 * Math.PI * frequency / 48_000;
                for (int i = 0; i < block.Length; i++)
                {
                    double window = block.Length == 1
                        ? 1
                        : 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (block.Length - 1));
                    sin += block[i] * window * Math.Sin(angular * i);
                    cos += block[i] * window * Math.Cos(angular * i);
                    if (frequency == frequencies[0])
                        windowSum += window;
                }
                totals[frequency] += 2 * Math.Sqrt(sin * sin + cos * cos);
            }
            normalization += windowSum;
        }

        return totals.ToDictionary(
            pair => pair.Key.ToString("0.##", CultureInfo.InvariantCulture),
            pair => normalization == 0 ? 0 : pair.Value / normalization);
    }
}

internal sealed class QaLogBridge : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool InstallDelegate(LogCallback callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RemoveDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(int level, nint message);

    private readonly nint _library;
    private readonly Stopwatch _clock;
    private readonly ConcurrentQueue<ObsLogEntry> _logs;
    private readonly RemoveDelegate _remove;
    private readonly LogCallback _callback;

    internal QaLogBridge(
        string path,
        Stopwatch clock,
        ConcurrentQueue<ObsLogEntry> logs)
    {
        _clock = clock;
        _logs = logs;
        _library = NativeLibrary.Load(path);
        var install = Marshal.GetDelegateForFunctionPointer<InstallDelegate>(
            NativeLibrary.GetExport(_library, "captail_install_obs_log_handler"));
        _remove = Marshal.GetDelegateForFunctionPointer<RemoveDelegate>(
            NativeLibrary.GetExport(_library, "captail_remove_obs_log_handler"));
        _callback = OnLog;
        if (!install(_callback))
            throw new InvalidOperationException("Could not install the OBS log bridge.");
    }

    public void Dispose()
    {
        _remove();
        NativeLibrary.Free(_library);
    }

    private void OnLog(int level, nint messagePointer)
    {
        string message = Marshal.PtrToStringUTF8(messagePointer) ?? string.Empty;
        var entry = new ObsLogEntry(_clock.ElapsedMilliseconds, level, message);
        _logs.Enqueue(entry);
        if (message.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("process", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"OBS elapsed_ms={entry.ElapsedMs} level={level} message={message}");
        }
    }
}

internal sealed record SignalInterval(long StartMs, long? EndMs);
internal sealed record ObsLogEntry(long ElapsedMs, int Level, string Message);
internal sealed record ProbeResult(
    string Label,
    string Executable,
    uint? ProcessId,
    long? CreationTime,
    bool ActiveAtEnd,
    long? FirstCallbackMs,
    long? FirstSignalMs,
    long? LastSignalMs,
    long CallbackBlocks,
    long NonSilentBlocks,
    long Frames,
    double MaxRms,
    IReadOnlyList<SignalInterval> SignalIntervals,
    IReadOnlyDictionary<string, double> FrequencyAmplitudes);
internal sealed record QaReport(
    DateTimeOffset CreatedAt,
    string ObsVersion,
    long DurationMs,
    IReadOnlyList<ProbeResult> Sources,
    IReadOnlyList<ObsLogEntry> ObsLog);

internal static class NativeMethods
{
    internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    internal const uint LoadLibrarySearchUserDirs = 0x00000400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveDllDirectory(nint cookie);
}

internal static class ObsNative
{
    private const string Library = "obs.dll";

    internal enum VideoFormat { None, I420, Nv12 }
    internal enum VideoColorSpace { Default, Cs601, Cs709, Srgb }
    internal enum VideoRange { Default, Partial, Full }
    internal enum ScaleType { Disable, Point, Bicubic, Bilinear, Lanczos, Area }
    internal enum SpeakerLayout { Unknown, Mono, Stereo }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoInfo
    {
        internal nint GraphicsModule;
        internal uint FpsNum;
        internal uint FpsDen;
        internal uint BaseWidth;
        internal uint BaseHeight;
        internal uint OutputWidth;
        internal uint OutputHeight;
        internal VideoFormat OutputFormat;
        internal uint Adapter;
        [MarshalAs(UnmanagedType.I1)] internal bool GpuConversion;
        internal VideoColorSpace ColorSpace;
        internal VideoRange VideoRange;
        internal ScaleType ScaleType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioInfo
    {
        internal uint SamplesPerSecond;
        internal SpeakerLayout Speakers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioData
    {
        internal nint Data0;
        internal nint Data1;
        internal nint Data2;
        internal nint Data3;
        internal nint Data4;
        internal nint Data5;
        internal nint Data6;
        internal nint Data7;
        internal uint Frames;
        internal ulong Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CallData
    {
        internal nint Stack;
        internal nuint Size;
        internal nuint Capacity;
        [MarshalAs(UnmanagedType.I1)] internal bool Fixed;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AudioCaptureCallback(
        nint parameter,
        nint source,
        nint audioData,
        [MarshalAs(UnmanagedType.I1)] bool muted);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_startup(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string locale,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string moduleConfigPath,
        nint profilerStore);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_shutdown();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_wait_for_destroy_queue();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_get_version_string();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_add_data_path(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int obs_reset_video(ref VideoInfo videoInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_reset_audio(ref AudioInfo audioInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int obs_open_module(
        out nint module,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string binPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dataPath);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_init_module(nint module);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_post_load_modules();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_enum_input_types(nuint index, out nint id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_data_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_release(nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_set_string(
        nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_data_set_int(
        nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        long value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_source_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint settings,
        nint hotkeyData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_remove(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_release(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_inc_active(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_dec_active(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint obs_source_get_proc_handler(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool proc_handler_call(
        nint handler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref CallData callData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool calldata_get_data(
        ref CallData callData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out long value,
        nuint size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool obs_source_active(nint source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_add_audio_capture_callback(
        nint source,
        AudioCaptureCallback callback,
        nint parameter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void obs_source_remove_audio_capture_callback(
        nint source,
        AudioCaptureCallback callback,
        nint parameter);
}
