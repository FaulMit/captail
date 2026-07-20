using System.IO;
using System.Runtime.InteropServices;
using Captail.Interop;

namespace Captail;

public sealed class ObsReplayEngine : IDisposable
{
    private const string RequiredObsVersion = "32.1.2";
    private static readonly object ContextGate = new();
    private static nint _obsLibrary;
    private static bool _contextOwned;

    private readonly Config _config;
    private readonly object _saveGate = new();
    private readonly ObsNative.SignalCallback _savedCallback;
    private readonly ObsNative.SignalCallback _stoppedCallback;
    private readonly List<nint> _audioSources = [];
    private readonly List<nint> _audioEncoders = [];

    private nint _videoSource;
    private nint _scene;
    private nint _videoEncoder;
    private nint _output;
    private nint _outputSignals;
    private TaskCompletionSource<string>? _pendingSave;
    private bool _started;
    private bool _obsStarted;
    private bool _logBridgeInstalled;
    private bool _disposing;
    private uint _previousFrameCount;
    private DateTime _previousFrameCheckUtc;
    private uint _outputWidth;
    private uint _outputHeight;

    public event Action<string>? Faulted;

    public string ActiveCodec { get; private set; } = "";
    public string ActiveEncoder { get; private set; } = "";
    public string ActiveEncoderDisplayName { get; private set; } = "";
    public int ActiveBitrateMbps { get; private set; }
    public EncoderCapabilities Capabilities { get; private set; } =
        EncoderCapabilities.Failed(
            Localization.Text("L.Engine.CapabilitiesPending"));
    public bool IsGameCapture { get; }
    public bool IsActive =>
        _started &&
        _output != 0 &&
        ObsNative.obs_output_active(_output) &&
        _videoEncoder != 0 &&
        ObsNative.obs_encoder_active(_videoEncoder);

    public string Description
    {
        get
        {
            if (!IsGameCapture)
                return Localization.Text("L.Video.Desktop");
            return IsGameHooked
                ? Localization.Text("L.Engine.GameCaptured")
                : Localization.Text("L.Engine.GameWaiting");
        }
    }

    public bool IsGameHooked =>
        IsGameCapture && _videoSource != 0 && ReadBoolProcedure(
            ObsNative.obs_source_get_proc_handler(_videoSource),
            "get_hooked",
            "hooked");

    public bool IsHealthy
    {
        get
        {
            if (!IsActive)
                return false;

            uint frames = ObsNative.obs_get_total_frames();
            DateTime now = DateTime.UtcNow;
            if (_previousFrameCheckUtc == default ||
                now - _previousFrameCheckUtc >= TimeSpan.FromSeconds(3))
            {
                bool progressing = _previousFrameCheckUtc == default ||
                                   frames != _previousFrameCount;
                _previousFrameCount = frames;
                _previousFrameCheckUtc = now;
                return progressing;
            }

            return true;
        }
    }

    public int EncodedFrameCount =>
        _output == 0 ? 0 : ObsNative.obs_output_get_total_frames(_output);

    public ulong BufferedBytes =>
        _output == 0 ? 0 : ObsNative.obs_output_get_total_bytes(_output);

    public ObsReplayEngine(Config config)
    {
        _config = config;
        IsGameCapture = string.Equals(
            config.CaptureSource,
            "game",
            StringComparison.OrdinalIgnoreCase);
        _savedCallback = OnReplaySaved;
        _stoppedCallback = OnOutputStopped;
    }

    public void Start()
    {
        if (_started)
            return;

        lock (ContextGate)
        {
            if (_contextOwned)
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.InUse"));
            _contextOwned = true;
        }

        try
        {
            InitializeObs();
            Capabilities = DetectCapabilities();
            EnsureConfiguredCodecIsSupported();
            CreateSources();
            CreateEncoders();
            CreateReplayBuffer();
            _started = true;

            Log.Write(
                $"OBS pipeline: version={ObsVersion()}, source={Description}, " +
                $"gpu={Capabilities.AdapterName}, encoder={ActiveEncoder}, " +
                $"codec={ActiveCodec}, bitrate={ActiveBitrateMbps} Mbps, " +
                $"fps={_config.FrameRate}, maxSize={_config.MaxReplaySizeMb} MB, " +
                $"mic={_config.MicrophoneVolume}%+{_config.MicrophoneBoostDb}dB");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static EncoderCapabilities ProbeCapabilities(Config config)
    {
        var probe = new ObsReplayEngine(config);
        lock (ContextGate)
        {
            if (_contextOwned)
                return EncoderCapabilities.Failed(
                    Localization.Text("L.Engine.InUse"));
            _contextOwned = true;
        }

        try
        {
            probe.InitializeObs();
            return probe.DetectCapabilities();
        }
        catch (Exception exception)
        {
            Log.Write($"GPU capability detection failed: {exception}");
            return EncoderCapabilities.Failed(exception.Message);
        }
        finally
        {
            probe.Dispose();
        }
    }

    public async Task<string> SaveReplayAsync(CancellationToken cancellationToken = default)
    {
        Task<string> completion;
        lock (_saveGate)
        {
            if (!IsActive)
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.BufferStopped"));
            if (_pendingSave is not null)
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.SavePending"));

            _pendingSave = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _pendingSave.Task;
            nint procedures = ObsNative.obs_output_get_proc_handler(_output);
            if (procedures == 0 ||
                !ObsNative.proc_handler_call(procedures, "save", 0))
            {
                _pendingSave = null;
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.SaveRejected"));
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            return await completion.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_saveGate)
                _pendingSave = null;
            throw new TimeoutException(
                Localization.Text("L.Engine.SaveTimeout"));
        }
    }

    private void InitializeObs()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string obsPath = Path.Combine(baseDirectory, ObsNative.Library);
        if (!File.Exists(obsPath))
        {
            throw new FileNotFoundException(
                Localization.Text("L.Engine.ModuleMissing"),
                obsPath);
        }

        if (_obsLibrary == 0)
            _obsLibrary = NativeLibrary.Load(obsPath);
        _logBridgeInstalled = ObsLogBridge.Install();

        string configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captail",
            "obs");
        Directory.CreateDirectory(configDirectory);
        _obsStarted = ObsNative.obs_startup(
            Localization.IsRussian ? "ru-RU" : "en-US",
            configDirectory,
            0);
        if (!_obsStarted)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.InitFailed"));

        string version = ObsVersion();
        if (!version.StartsWith(RequiredObsVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VersionMismatch"));
        }

        string dataRoot = Path.Combine(baseDirectory, "data");
        ObsNative.obs_add_data_path(
            ToObsPath(Path.Combine(dataRoot, "libobs")) + "/");

        List<CaptureInterop.MonitorInfo> monitors = CaptureInterop.EnumerateMonitors();
        CaptureInterop.MonitorInfo monitor =
            _config.MonitorIndex >= 0 && _config.MonitorIndex < monitors.Count
                ? monitors[_config.MonitorIndex]
                : monitors.FirstOrDefault()
                  ?? throw new InvalidOperationException(
                      Localization.Text("L.Engine.MonitorMissing"));
        (uint outputWidth, uint outputHeight) = ResolveOutputSize(
            monitor,
            _config.RecordingResolution);
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;

        nint graphicsModule = Marshal.StringToCoTaskMemUTF8(
            Path.Combine(baseDirectory, "libobs-d3d11.dll"));
        try
        {
            var video = new ObsNative.VideoInfo
            {
                GraphicsModule = graphicsModule,
                FpsNum = (uint)_config.FrameRate,
                FpsDen = 1,
                BaseWidth = (uint)monitor.Width,
                BaseHeight = (uint)monitor.Height,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                OutputFormat = ObsNative.VideoFormat.Nv12,
                Adapter = 0,
                GpuConversion = true,
                ColorSpace = ObsNative.VideoColorSpace.Cs709,
                Range = ObsNative.VideoRange.Partial,
                ScaleType = ObsNative.ScaleType.Bicubic,
            };
            int result = ObsNative.obs_reset_video(ref video);
            if (result != 0)
            {
                DiagnoseEffects(baseDirectory, dataRoot);
                throw new InvalidOperationException(
                    Localization.Format("L.Engine.VideoFailed", result));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(graphicsModule);
        }

        var audio = new ObsNative.AudioInfo
        {
            SamplesPerSecond = 48_000,
            Speakers = ObsNative.SpeakerLayout.Stereo,
        };
        if (!ObsNative.obs_reset_audio(ref audio))
            throw new InvalidOperationException(
                Localization.Text("L.Engine.AudioFailed"));

        ObsNative.obs_add_module_path(
            ToObsPath(Path.Combine(baseDirectory, "obs-plugins", "64bit")),
            ToObsPath(Path.Combine(dataRoot, "obs-plugins", "%module%")));
        ObsNative.obs_load_all_modules();
        ObsNative.obs_post_load_modules();
    }

    private EncoderCapabilities DetectCapabilities()
    {
        string adapterName = Localization.Text("L.Gpu.Generic");
        ObsNative.AdapterCallback callback = (_, name, id) =>
        {
            if (id == 0)
                adapterName = PtrToString(name);
            return true;
        };

        ObsNative.obs_enter_graphics();
        try
        {
            ObsNative.gs_enum_adapters(callback, 0);
        }
        finally
        {
            ObsNative.obs_leave_graphics();
        }

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (nuint index = 0;
             ObsNative.obs_enum_encoder_types(index, out nint encoderId);
             index++)
        {
            string id = PtrToString(encoderId);
            if (!string.IsNullOrWhiteSpace(id))
                registered.Add(id);
        }

        var capabilities = new EncoderCapabilities(
            adapterName,
            EncoderCatalog.Available(registered, adapterName));
        string available = string.Join(
            ", ",
            new[] { "h264", "hevc", "av1" }
                .Where(capabilities.Supports)
                .Select(codec =>
                    $"{codec}:{capabilities.Preferred(codec)!.FamilyDisplayName}"));
        Log.Write(
            $"GPU capability: adapter={capabilities.AdapterName}; " +
            $"hardware encoders={available}");
        return capabilities;
    }

    private void EnsureConfiguredCodecIsSupported()
    {
        if (Capabilities.Supports(_config.Codec))
            return;

        string? fallback = Capabilities.FallbackCodec();
        if (fallback is null)
        {
            throw new InvalidOperationException(
                Localization.Text("L.Engine.NoEncoder"));
        }

        Log.Write(Localization.Format(
            "L.Engine.CodecFallback",
            _config.Codec,
            Capabilities.AdapterName,
            fallback));
        _config.Codec = fallback;
    }

    private static string ToObsPath(string path) => path.Replace('\\', '/');

    private static (uint Width, uint Height) ResolveOutputSize(
        CaptureInterop.MonitorInfo monitor,
        string setting) =>
        setting.ToLowerInvariant() switch
        {
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "1440p" => (2560, 1440),
            "2160p" => (3840, 2160),
            _ => ((uint)monitor.Width, (uint)monitor.Height),
        };

    private static void DiagnoseEffects(string baseDirectory, string dataRoot)
    {
        nint graphics = 0;
        bool entered = false;
        try
        {
            int result = ObsNative.gs_create(
                out graphics,
                Path.Combine(baseDirectory, "libobs-d3d11.dll"),
                0);
            if (result != 0 || graphics == 0)
            {
                Log.Write($"OBS effect diagnostic: gs_create={result}");
                return;
            }

            ObsNative.gs_enter_context(graphics);
            entered = true;
            foreach (string name in new[]
                     {
                         "default.effect", "opaque.effect", "solid.effect",
                         "format_conversion.effect", "premultiplied_alpha.effect"
                     })
            {
                nint error = 0;
                nint effect = ObsNative.gs_effect_create_from_file(
                    ToObsPath(Path.Combine(dataRoot, "libobs", name)),
                    out error);
                string details = error == 0
                    ? ""
                    : Marshal.PtrToStringUTF8(error) ?? "";
                Log.Write(
                    $"OBS effect diagnostic: {name}={(effect == 0 ? "FAIL" : "OK")}" +
                    (details.Length == 0 ? "" : $"; {details}"));
                if (effect != 0)
                    ObsNative.gs_effect_destroy(effect);
                if (error != 0)
                    ObsNative.bfree(error);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"OBS effect diagnostic failed: {ex.Message}");
        }
        finally
        {
            if (entered)
                ObsNative.gs_leave_context();
            if (graphics != 0)
                ObsNative.gs_destroy(graphics);
        }
    }

    private void CreateSources()
    {
        List<CaptureInterop.MonitorInfo> monitors = CaptureInterop.EnumerateMonitors();
        CaptureInterop.MonitorInfo monitor =
            _config.MonitorIndex >= 0 && _config.MonitorIndex < monitors.Count
                ? monitors[_config.MonitorIndex]
                : monitors.First();

        nint videoSettings = ObsNative.obs_data_create();
        try
        {
            if (IsGameCapture)
            {
                string selector = CaptureInterop.BuildObsWindowSelector(
                    _config.GameExecutablePath);
                ObsNative.obs_data_set_string(videoSettings, "capture_mode", "window");
                ObsNative.obs_data_set_string(videoSettings, "window", selector);
                ObsNative.obs_data_set_int(videoSettings, "priority", 2);
                ObsNative.obs_data_set_bool(videoSettings, "capture_cursor", true);
                ObsNative.obs_data_set_bool(videoSettings, "limit_framerate", false);
                ObsNative.obs_data_set_bool(videoSettings, "anti_cheat_hook", true);
                ObsNative.obs_data_set_int(videoSettings, "hook_rate", 1);
                ObsNative.obs_data_set_bool(
                    videoSettings,
                    "capture_audio",
                    _config.CaptureSystemAudio);
                _videoSource = ObsNative.obs_source_create(
                    "game_capture",
                    "Captail Game Capture",
                    videoSettings,
                    0);
            }
            else
            {
                // WGC handles secure/DRM surfaces and DXGI resets more reliably;
                // protected regions become black without stopping the source.
                ObsNative.obs_data_set_int(videoSettings, "method", 2);
                ObsNative.obs_data_set_string(
                    videoSettings,
                    "monitor_id",
                    monitor.DeviceId);
                ObsNative.obs_data_set_bool(videoSettings, "capture_cursor", true);
                ObsNative.obs_data_set_bool(videoSettings, "force_sdr", false);
                _videoSource = ObsNative.obs_source_create(
                    "monitor_capture",
                    "Captail Display Capture",
                    videoSettings,
                    0);
            }
        }
        finally
        {
            ObsNative.obs_data_release(videoSettings);
        }

        if (_videoSource == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VideoSourceFailed"));

        uint systemMix = _config.SeparateAudioTracks ? 1u : 1u;
        if (IsGameCapture && _config.CaptureSystemAudio)
        {
            ObsNative.obs_source_set_audio_mixers(_videoSource, systemMix);
            ObsNative.obs_source_set_volume(
                _videoSource,
                NormalizeVolume(_config.SystemAudioVolume));
        }

        _scene = ObsNative.obs_scene_create("Captail Scene");
        if (_scene == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.SceneFailed"));
        nint item = ObsNative.obs_scene_add(_scene, _videoSource);
        if (item == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.SourceAttachFailed"));

        var bounds = new ObsNative.Vec2
        {
            X = monitor.Width,
            Y = monitor.Height,
        };
        var position = new ObsNative.Vec2
        {
            X = monitor.Width / 2f,
            Y = monitor.Height / 2f,
        };
        ObsNative.obs_sceneitem_set_alignment(item, 0);
        ObsNative.obs_sceneitem_set_bounds_alignment(item, 0);
        ObsNative.obs_sceneitem_set_bounds_type(item, ObsNative.BoundsType.ScaleInner);
        ObsNative.obs_sceneitem_set_bounds(item, ref bounds);
        ObsNative.obs_sceneitem_set_pos(item, ref position);
        ObsNative.obs_sceneitem_set_scale_filter(item, ObsNative.ScaleType.Bicubic);
        ObsNative.obs_set_output_source(0, ObsNative.obs_scene_get_source(_scene));

        if (!IsGameCapture && _config.CaptureSystemAudio)
        {
            nint system = CreateAudioSource(
                "wasapi_output_capture",
                "Captail System Audio",
                _config.SystemAudioDeviceId,
                _config.SeparateAudioTracks ? 1u : 1u,
                NormalizeVolume(_config.SystemAudioVolume));
            _audioSources.Add(system);
            ObsNative.obs_set_output_source(1, system);
        }

        if (_config.CaptureMicrophone)
        {
            uint micMix = _config.SeparateAudioTracks &&
                          _config.CaptureSystemAudio
                ? 2u
                : 1u;
            nint microphone = CreateAudioSource(
                "wasapi_input_capture",
                "Captail Microphone",
                _config.MicrophoneDeviceId,
                micMix,
                NormalizeVolume(_config.MicrophoneVolume) *
                DecibelsToLinear(_config.MicrophoneBoostDb));
            _audioSources.Add(microphone);
            ObsNative.obs_set_output_source(2, microphone);
        }
    }

    private static nint CreateAudioSource(
        string sourceId,
        string name,
        string deviceId,
        uint mixers,
        float volume)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(
                settings,
                "device_id",
                string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId);
            ObsNative.obs_data_set_bool(
                settings,
                "use_device_timing",
                sourceId == "wasapi_output_capture");
            nint source = ObsNative.obs_source_create(sourceId, name, settings, 0);
            if (source == 0)
                throw new InvalidOperationException(
                    Localization.Format("L.Engine.AudioSourceFailed", name));
            ObsNative.obs_source_set_audio_mixers(source, mixers);
            ObsNative.obs_source_set_volume(source, volume);
            Log.Write(
                $"Audio gain: source={name}, requested={volume:0.000}, " +
                $"applied={ObsNative.obs_source_get_volume(source):0.000}");
            return source;
        }
        finally
        {
            ObsNative.obs_data_release(settings);
        }
    }

    private static float NormalizeVolume(int percent) =>
        Math.Clamp(percent, 0, 100) / 100f;

    private static float DecibelsToLinear(int decibels) =>
        MathF.Pow(10f, Math.Clamp(decibels, 0, 20) / 20f);

    private void CreateEncoders()
    {
        EncoderLoadProfile loadProfile = SelectLoadProfile();
        foreach (CodecCapability candidate in Capabilities.Candidates(_config.Codec))
        {
            nint settings = ObsNative.obs_data_create();
            try
            {
                ConfigureEncoderSettings(settings, candidate, loadProfile);
                _videoEncoder = ObsNative.obs_video_encoder_create(
                    candidate.EncoderId,
                    $"Captail {candidate.EncoderId}",
                    settings,
                    0);
                if (_videoEncoder == 0)
                {
                    Log.Write(
                        $"Encoder {candidate.EncoderId} rejected profile " +
                        $"{loadProfile}; trying the next candidate.");
                    continue;
                }

                ActiveEncoder = candidate.EncoderId;
                ActiveEncoderDisplayName = candidate.FamilyDisplayName;
                ActiveCodec = _config.Codec.ToLowerInvariant();
                break;
            }
            finally
            {
                ObsNative.obs_data_release(settings);
            }
        }

        if (_videoEncoder == 0)
        {
            throw new InvalidOperationException(
                Localization.Format(
                    "L.Engine.EncoderFailed",
                    _config.Codec.ToUpperInvariant()));
        }
        ObsNative.obs_encoder_set_video(_videoEncoder, ObsNative.obs_get_video());

        int audioTrackCount = AudioTrackCount();
        string audioEncoderId = string.Equals(
            _config.AudioCodec,
            "opus",
            StringComparison.OrdinalIgnoreCase)
            ? "ffmpeg_opus"
            : "ffmpeg_aac";
        for (int index = 0; index < audioTrackCount; index++)
        {
            nint audioSettings = ObsNative.obs_data_create();
            try
            {
                ObsNative.obs_data_set_int(
                    audioSettings,
                    "bitrate",
                    _config.AudioBitrateKbps);
                nint encoder = ObsNative.obs_audio_encoder_create(
                    audioEncoderId,
                    $"Captail Audio {index + 1}",
                    audioSettings,
                    (nuint)index,
                    0);
                if (encoder == 0)
                {
                    throw new InvalidOperationException(
                        Localization.Format(
                            "L.Engine.AudioEncoderFailed",
                            _config.AudioCodec.ToUpperInvariant()));
                }
                ObsNative.obs_encoder_set_audio(encoder, ObsNative.obs_get_audio());
                _audioEncoders.Add(encoder);
            }
            finally
            {
                ObsNative.obs_data_release(audioSettings);
            }
        }
    }

    private void ConfigureEncoderSettings(
        nint settings,
        CodecCapability encoder,
        EncoderLoadProfile loadProfile)
    {
        int bitrateMbps = _config.BitrateMbps > 0
            ? _config.BitrateMbps
            : AutomaticBitrateMbps(loadProfile, _config.Codec);
        if (encoder.Family == "qsv")
            bitrateMbps = Math.Min(bitrateMbps, 65);
        ActiveBitrateMbps = bitrateMbps;

        ObsNative.obs_data_set_int(settings, "bitrate", bitrateMbps * 1000L);
        ObsNative.obs_data_set_int(
            settings,
            "keyint_sec",
            _config.MaxReplaySizeMb > 0 ? 1 : 2);
        ObsNative.obs_data_set_string(settings, "rate_control", "CBR");

        switch (encoder.Family)
        {
            case "nvenc":
                ConfigureNvenc(settings, loadProfile);
                break;
            case "amf":
                ConfigureAmf(settings, loadProfile);
                break;
            case "qsv":
                ConfigureQsv(settings, loadProfile);
                break;
        }

        string profile = _config.Codec.Equals(
            "h264",
            StringComparison.OrdinalIgnoreCase)
            ? "high"
            : "main";
        ObsNative.obs_data_set_string(settings, "profile", profile);
    }

    private static void ConfigureNvenc(
        nint settings,
        EncoderLoadProfile loadProfile)
    {
        ObsNative.obs_data_set_string(
            settings,
            "preset",
            loadProfile switch
            {
                EncoderLoadProfile.Standard => "p4",
                EncoderLoadProfile.High => "p3",
                _ => "p2",
            });
        ObsNative.obs_data_set_string(
            settings,
            "tune",
            loadProfile == EncoderLoadProfile.Standard ? "hq" : "ll");
        ObsNative.obs_data_set_string(settings, "multipass", "disabled");
        ObsNative.obs_data_set_bool(settings, "lookahead", false);
        ObsNative.obs_data_set_bool(
            settings,
            "adaptive_quantization",
            loadProfile == EncoderLoadProfile.Standard);
        ObsNative.obs_data_set_int(
            settings,
            "bf",
            loadProfile == EncoderLoadProfile.Standard ? 2 : 0);
    }

    private static void ConfigureAmf(
        nint settings,
        EncoderLoadProfile loadProfile)
    {
        ObsNative.obs_data_set_string(
            settings,
            "preset",
            loadProfile switch
            {
                EncoderLoadProfile.Standard => "quality",
                EncoderLoadProfile.High => "balanced",
                _ => "speed",
            });
        ObsNative.obs_data_set_bool(settings, "pre_analysis", false);
        ObsNative.obs_data_set_int(
            settings,
            "bf",
            loadProfile == EncoderLoadProfile.Standard ? 2 : 0);
    }

    private static void ConfigureQsv(
        nint settings,
        EncoderLoadProfile loadProfile)
    {
        ObsNative.obs_data_set_string(
            settings,
            "target_usage",
            loadProfile switch
            {
                EncoderLoadProfile.Standard => "TU4",
                EncoderLoadProfile.High => "TU6",
                _ => "TU7",
            });
        ObsNative.obs_data_set_string(
            settings,
            "latency",
            loadProfile == EncoderLoadProfile.Standard ? "low" : "ultra-low");
        ObsNative.obs_data_set_int(
            settings,
            "bframes",
            loadProfile == EncoderLoadProfile.Standard ? 2 : 0);
    }

    private EncoderLoadProfile SelectLoadProfile()
    {
        ulong pixelsPerSecond =
            (ulong)_outputWidth * _outputHeight * (uint)_config.FrameRate;
        if (_config.FrameRate >= 240 || pixelsPerSecond > 600_000_000)
            return EncoderLoadProfile.Extreme;
        if (_config.FrameRate >= 120 || pixelsPerSecond > 220_000_000)
            return EncoderLoadProfile.High;
        return EncoderLoadProfile.Standard;
    }

    private static int AutomaticBitrateMbps(
        EncoderLoadProfile loadProfile,
        string codec) =>
        (loadProfile, codec.ToLowerInvariant()) switch
        {
            (EncoderLoadProfile.Standard, "av1") => 15,
            (EncoderLoadProfile.Standard, "hevc") => 18,
            (EncoderLoadProfile.Standard, _) => 25,
            (EncoderLoadProfile.High, "av1") => 35,
            (EncoderLoadProfile.High, "hevc") => 45,
            (EncoderLoadProfile.High, _) => 55,
            (EncoderLoadProfile.Extreme, "av1") => 50,
            (EncoderLoadProfile.Extreme, "hevc") => 65,
            _ => 80,
        };

    private enum EncoderLoadProfile
    {
        Standard,
        High,
        Extreme,
    }

    private void CreateReplayBuffer()
    {
        Directory.CreateDirectory(_config.OutputDirectory);
        bool opus = string.Equals(
            _config.AudioCodec,
            "opus",
            StringComparison.OrdinalIgnoreCase);
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_int(
                settings,
                "max_time_sec",
                _config.BufferSeconds);
            ObsNative.obs_data_set_int(
                settings,
                "max_size_mb",
                Math.Max(0, _config.MaxReplaySizeMb));
            ObsNative.obs_data_set_string(settings, "directory", _config.OutputDirectory);
            ObsNative.obs_data_set_string(
                settings,
                "format",
                "Replay_%CCYY-%MM-%DD_%hh-%mm-%ss");
            ObsNative.obs_data_set_string(settings, "extension", opus ? "mkv" : "mp4");
            ObsNative.obs_data_set_bool(settings, "allow_spaces", false);
            if (!opus)
            {
                ObsNative.obs_data_set_string(
                    settings,
                    "muxer_settings",
                    "movflags=frag_keyframe+empty_moov+delay_moov");
            }

            _output = ObsNative.obs_output_create(
                "replay_buffer",
                "Captail Replay Buffer",
                settings,
                0);
        }
        finally
        {
            ObsNative.obs_data_release(settings);
        }

        if (_output == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.BufferUnavailable"));

        ObsNative.obs_output_set_video_encoder(_output, _videoEncoder);
        for (int index = 0; index < _audioEncoders.Count; index++)
        {
            ObsNative.obs_output_set_audio_encoder(
                _output,
                _audioEncoders[index],
                (nuint)index);
        }

        _outputSignals = ObsNative.obs_output_get_signal_handler(_output);
        ObsNative.signal_handler_connect(
            _outputSignals,
            "saved",
            _savedCallback,
            0);
        ObsNative.signal_handler_connect(
            _outputSignals,
            "stop",
            _stoppedCallback,
            0);

        if (!ObsNative.obs_output_start(_output))
        {
            string error = PtrToString(ObsNative.obs_output_get_last_error(_output));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? Localization.Text("L.Engine.BufferStartFailed")
                    : error);
        }
    }

    private int AudioTrackCount()
    {
        int enabled = (_config.CaptureSystemAudio ? 1 : 0) +
                      (_config.CaptureMicrophone ? 1 : 0);
        if (enabled == 0)
            return 1; // Replay Buffer requires an audio encoder; this track remains silent.
        return _config.SeparateAudioTracks && enabled > 1 ? 2 : 1;
    }

    private void OnReplaySaved(nint _, nint __)
    {
        string path = ReadStringProcedure(
            ObsNative.obs_output_get_proc_handler(_output),
            "get_last_replay",
            "path");
        lock (_saveGate)
        {
            TaskCompletionSource<string>? completion = _pendingSave;
            _pendingSave = null;
            if (string.IsNullOrWhiteSpace(path))
                completion?.TrySetException(
                    new IOException(
                        Localization.Text("L.Engine.SavedPathMissing")));
            else
                completion?.TrySetResult(Path.GetFullPath(path));
        }
    }

    private void OnOutputStopped(nint _, nint __)
    {
        if (_disposing)
            return;
        string error = PtrToString(ObsNative.obs_output_get_last_error(_output));
        Faulted?.Invoke(
            string.IsNullOrWhiteSpace(error)
                ? Localization.Text("L.Engine.BufferUnexpectedStop")
                : Localization.Format("L.Engine.BufferError", error));
    }

    private static bool ReadBoolProcedure(nint handler, string procedure, string name)
    {
        if (handler == 0)
            return false;

        const int stackSize = 4096;
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
            if (!ObsNative.proc_handler_call(handler, procedure, ref callData) ||
                !ObsNative.calldata_get_data(
                    ref callData,
                    name,
                    out byte value,
                    1))
            {
                return false;
            }
            return value != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(stack);
        }
    }

    private static string ReadStringProcedure(nint handler, string procedure, string name)
    {
        if (handler == 0)
            return "";

        const int stackSize = 4096;
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
            if (!ObsNative.proc_handler_call(handler, procedure, ref callData) ||
                !ObsNative.calldata_get_string(ref callData, name, out nint value))
            {
                return "";
            }
            return PtrToString(value);
        }
        finally
        {
            Marshal.FreeHGlobal(stack);
        }
    }

    private static string ObsVersion() =>
        PtrToString(ObsNative.obs_get_version_string());

    private static string PtrToString(nint value) =>
        value == 0 ? "" : Marshal.PtrToStringUTF8(value) ?? "";

    public void Dispose()
    {
        if (_disposing)
            return;
        _disposing = true;

        lock (_saveGate)
        {
            _pendingSave?.TrySetCanceled();
            _pendingSave = null;
        }

        if (!_obsStarted)
        {
            if (_logBridgeInstalled)
            {
                ObsLogBridge.Remove();
                _logBridgeInstalled = false;
            }
            _started = false;
            lock (ContextGate)
                _contextOwned = false;
            return;
        }

        if (_outputSignals != 0)
        {
            ObsNative.signal_handler_disconnect(
                _outputSignals,
                "saved",
                _savedCallback,
                0);
            ObsNative.signal_handler_disconnect(
                _outputSignals,
                "stop",
                _stoppedCallback,
                0);
            _outputSignals = 0;
        }

        if (_output != 0 && ObsNative.obs_output_active(_output))
        {
            ObsNative.obs_output_stop(_output);
            for (int attempt = 0;
                 attempt < 40 && ObsNative.obs_output_active(_output);
                 attempt++)
            {
                Thread.Sleep(25);
            }
            if (ObsNative.obs_output_active(_output))
                ObsNative.obs_output_force_stop(_output);
        }

        if (_output != 0)
        {
            ObsNative.obs_output_release(_output);
            _output = 0;
        }
        if (_videoEncoder != 0)
        {
            ObsNative.obs_encoder_release(_videoEncoder);
            _videoEncoder = 0;
        }
        foreach (nint encoder in _audioEncoders)
            ObsNative.obs_encoder_release(encoder);
        _audioEncoders.Clear();

        for (uint channel = 0; channel <= 6; channel++)
            ObsNative.obs_set_output_source(channel, 0);

        if (_scene != 0)
        {
            ObsNative.obs_scene_release(_scene);
            _scene = 0;
        }
        if (_videoSource != 0)
        {
            ObsNative.obs_source_release(_videoSource);
            _videoSource = 0;
        }
        foreach (nint source in _audioSources)
            ObsNative.obs_source_release(source);
        _audioSources.Clear();

        ObsNative.obs_shutdown();
        _obsStarted = false;
        if (_logBridgeInstalled)
        {
            ObsLogBridge.Remove();
            _logBridgeInstalled = false;
        }

        _started = false;
        lock (ContextGate)
            _contextOwned = false;
    }
}
