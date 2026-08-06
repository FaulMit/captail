using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Captail.Interop;

namespace Captail;

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "libobs is thread-affine; finalizer-thread native shutdown is unsafe.")]
public sealed class ObsReplayEngine : IDisposable
{
    private const string RequiredObsVersion = "32.1.2";
    private const int AutomaticHookStableChecks = 2;
    private const int Windows11InitialBuild = 22000;
    private const long MonitorCaptureMethodAuto = 0;
    private const long MonitorCaptureMethodWgc = 2;
    private static readonly string[] CapabilityCodecNames = ["h264", "hevc", "av1"];
    private static readonly HashSet<string> AutomaticCaptureRejectedProcesses = new(
        [
            "applicationframehost", "brave", "captail", "chrome", "discord",
            "discordcanary", "discordptb", "dwm", "eadesktop", "epicgameslauncher",
            "explorer", "firefox", "galaxyclient", "lockapp", "livelywpf",
            "mediaplayer", "mpv", "ms-teams", "msedge", "nvcontainer", "obs32",
            "obs64", "opera", "opera_gx", "searchapp", "searchhost",
            "shellexperiencehost", "signal", "slack", "spotify", "startmenuexperiencehost",
            "steam", "steamwebhelper", "systemsettings", "telegram", "textinputhost",
            "ubisoftconnect", "vivaldi", "vlc", "wallpaper32", "wallpaper64",
            "wallpaper_engine", "webviewhost", "whatsapp", "wmplayer", "zoom",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DiagnosticEffectNames =
    [
        "default.effect", "opaque.effect", "solid.effect",
        "format_conversion.effect", "premultiplied_alpha.effect",
    ];
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
    private nint _desktopVideoSource;
    private nint _gameVideoSource;
    private nint _videoEncoder;
    private nint _output;
    private nint _outputSignals;
    private TaskCompletionSource<string>? _pendingSave;
    private bool _started;
    private bool _obsStarted;
    private bool _logBridgeInstalled;
    private bool _disposing;
    private bool _resettingReplayWindow;
    private uint _previousFrameCount;
    private DateTime _previousFrameCheckUtc;
    private DateTime _replayWindowStartedUtc;
    private uint _outputWidth;
    private uint _outputHeight;
    private uint _baseWidth;
    private uint _baseHeight;
    private bool _automaticGameActive;
    private bool _automaticGameSourceShowing;
    private string _activeGameExecutable = "";
    private string _pendingAutomaticGameExecutable = "";
    private int _automaticHookStableChecks;
    private string _lastRejectedAutomaticExecutable = "";

    public event Action<string>? Faulted;

    public string ActiveCodec { get; private set; } = "";
    public string ActiveEncoder { get; private set; } = "";
    public string ActiveEncoderDisplayName { get; private set; } = "";
    public int ActiveBitrateMbps { get; private set; }
    public EncoderCapabilities Capabilities { get; private set; } =
        EncoderCapabilities.Failed(
            Localization.Text("L.Engine.CapabilitiesPending"));
    public bool IsGameCapture { get; }
    public bool IsAutomaticCapture { get; }
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
            if (IsAutomaticCapture)
            {
                string gameName = Path.GetFileNameWithoutExtension(
                    _activeGameExecutable);
                return _automaticGameActive
                    ? Localization.Format(
                        "L.Engine.AutoGameCaptured",
                        string.IsNullOrWhiteSpace(gameName)
                            ? Localization.Text("L.Video.Game")
                            : gameName)
                    : Localization.Text("L.Engine.AutoDesktop");
            }
            if (!IsGameCapture)
                return Localization.Text("L.Video.Desktop");
            return IsGameHooked
                ? Localization.Text("L.Engine.GameCaptured")
                : Localization.Text("L.Engine.GameWaiting");
        }
    }

    public bool IsGameHooked =>
        _gameVideoSource != 0 &&
        ReadBoolProcedure(
            ObsNative.obs_source_get_proc_handler(_gameVideoSource),
            "get_hooked",
            "hooked");

    public string ActiveGameExecutable => _activeGameExecutable;

    internal static bool IsAutomaticCaptureCandidate(string executable)
    {
        string processName = Path.GetFileNameWithoutExtension(executable.Trim());
        return processName.Length > 0 &&
               !AutomaticCaptureRejectedProcesses.Contains(processName);
    }

    internal static long RecommendedMonitorCaptureMethod(Version osVersion) =>
        osVersion.Major > 10 ||
        (osVersion.Major == 10 && osVersion.Build >= Windows11InitialBuild)
            ? MonitorCaptureMethodWgc
            : MonitorCaptureMethodAuto;

    internal static bool ShouldUseAutomaticGameCapture(
        string hookedExecutable,
        string foregroundExecutable,
        bool hasVideo) =>
        hasVideo &&
        IsAutomaticCaptureCandidate(hookedExecutable) &&
        string.Equals(
            Path.GetFileNameWithoutExtension(hookedExecutable),
            Path.GetFileNameWithoutExtension(foregroundExecutable),
            StringComparison.OrdinalIgnoreCase);

    internal static string? ResolveReplayGameExecutable(
        bool isAutomaticCapture,
        bool automaticGameActive,
        string activeGameExecutable,
        bool isGameHooked,
        string hookedExecutable)
    {
        if (isAutomaticCapture)
        {
            return automaticGameActive &&
                   !string.IsNullOrWhiteSpace(activeGameExecutable)
                ? activeGameExecutable
                : null;
        }

        return isGameHooked && !string.IsNullOrWhiteSpace(hookedExecutable)
            ? hookedExecutable
            : null;
    }

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
    public int AvailableReplaySeconds
    {
        get
        {
            if (!IsActive || _replayWindowStartedUtc == default)
                return 0;

            int elapsed = (int)Math.Floor(
                (DateTime.UtcNow - _replayWindowStartedUtc).TotalSeconds);
            return Math.Clamp(elapsed, 0, _config.BufferSeconds);
        }
    }
    public uint TotalRenderedFrames => ObsNative.obs_get_total_frames();
    public uint LaggedRenderedFrames => ObsNative.obs_get_lagged_frames();

    /// <summary>
    /// Updates game-hook metadata and Desktop fallback without restarting
    /// encoder or replay buffer.
    /// Must be called on Captail's serialized OBS thread.
    /// </summary>
    public bool RefreshCaptureState()
    {
        if (_gameVideoSource == 0)
            return false;

        bool hooked = IsGameHooked;
        string executable = hooked
            ? ReadStringProcedure(
                ObsNative.obs_source_get_proc_handler(_gameVideoSource),
                "get_hooked",
                "executable")
            : "";

        if (IsGameCapture)
        {
            _activeGameExecutable = hooked ? executable : "";
            return false;
        }

        if (_desktopVideoSource == 0)
            return false;

        bool processCandidate = hooked &&
                                IsAutomaticCaptureCandidate(executable);
        bool hasVideo = hooked &&
                        ObsNative.obs_source_get_width(_gameVideoSource) > 0 &&
                        ObsNative.obs_source_get_height(_gameVideoSource) > 0;
        string foregroundExecutable = CaptureInterop.ForegroundExecutable();
        bool candidate = ShouldUseAutomaticGameCapture(
            executable,
            foregroundExecutable,
            hasVideo);
        if (!candidate)
        {
            ResetPendingAutomaticHook();
            if (!hooked)
                _lastRejectedAutomaticExecutable = "";
            if (hooked && !processCandidate &&
                !string.Equals(
                    _lastRejectedAutomaticExecutable,
                    executable,
                    StringComparison.OrdinalIgnoreCase))
            {
                _lastRejectedAutomaticExecutable = executable;
                Log.Write(
                    $"Automatic Game Capture ignored non-game process: " +
                    $"{Path.GetFileName(executable)}");
            }
            if (_automaticGameActive)
                return SwitchAutomaticCapture(useGame: false, executable: "");
            return false;
        }

        _lastRejectedAutomaticExecutable = "";
        if (_automaticGameActive)
        {
            _activeGameExecutable = executable;
            return false;
        }

        if (!string.Equals(
                _pendingAutomaticGameExecutable,
                executable,
                StringComparison.OrdinalIgnoreCase))
        {
            _pendingAutomaticGameExecutable = executable;
            _automaticHookStableChecks = 1;
            return false;
        }
        _automaticHookStableChecks++;
        if (_automaticHookStableChecks < AutomaticHookStableChecks)
            return false;

        ResetPendingAutomaticHook();
        return SwitchAutomaticCapture(useGame: true, executable);
    }

    private bool SwitchAutomaticCapture(bool useGame, string executable)
    {
        if (useGame == _automaticGameActive)
            return false;

        nint target = useGame ? _gameVideoSource : _desktopVideoSource;
        ObsNative.obs_set_output_source(0, target);
        _videoSource = target;
        _automaticGameActive = useGame;
        _activeGameExecutable = useGame ? executable : "";
        Log.Write(
            useGame
                ? $"Automatic capture switched to Game Capture: " +
                  $"{Path.GetFileName(_activeGameExecutable)}"
                : "Automatic capture returned to Desktop Capture.");
        return true;
    }

    private void ResetPendingAutomaticHook()
    {
        _pendingAutomaticGameExecutable = "";
        _automaticHookStableChecks = 0;
    }

    public ObsReplayEngine(Config config)
    {
        _config = config;
        IsGameCapture = string.Equals(
            config.CaptureSource,
            "game",
            StringComparison.OrdinalIgnoreCase);
        IsAutomaticCapture = !IsGameCapture;
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
        lock (ContextGate)
        {
            if (_contextOwned)
                return EncoderCapabilities.Failed(
                    Localization.Text("L.Engine.InUse"));
            _contextOwned = true;
        }

        ObsReplayEngine probe;
        try
        {
            probe = new ObsReplayEngine(config);
        }
        catch
        {
            lock (ContextGate)
                _contextOwned = false;
            throw;
        }

        using (probe)
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
    }

    public ReplaySaveOperation BeginSaveReplay(
        CancellationToken cancellationToken = default)
    {
        if (IsAutomaticCapture)
            RefreshCaptureState();

        Task<string> completion;
        ulong initialMuxBytes;
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
            initialMuxBytes = BufferedBytes;
            nint procedures = ObsNative.obs_output_get_proc_handler(_output);
            if (procedures == 0 ||
                !ObsNative.proc_handler_call(procedures, "save", 0))
            {
                _pendingSave = null;
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.SaveRejected"));
            }
        }

        bool isGameHooked = IsGameHooked;
        string hookedExecutable = isGameHooked
            ? ReadStringProcedure(
                ObsNative.obs_source_get_proc_handler(_gameVideoSource),
                "get_hooked",
                "executable")
            : "";
        string? replayGameExecutable = ResolveReplayGameExecutable(
            IsAutomaticCapture,
            _automaticGameActive,
            _activeGameExecutable,
            isGameHooked,
            hookedExecutable);
        if (IsAutomaticCapture && isGameHooked &&
            !string.Equals(
                replayGameExecutable,
                hookedExecutable,
                StringComparison.OrdinalIgnoreCase))
        {
            Log.Write(
                "Replay routing ignored inactive Game Capture hook: " +
                Path.GetFileName(hookedExecutable));
        }

        return new ReplaySaveOperation(
            WaitForSaveCompletionAsync(completion, cancellationToken),
            initialMuxBytes,
            replayGameExecutable);
    }

    public Task<string> SaveReplayAsync(
        CancellationToken cancellationToken = default) =>
        BeginSaveReplay(cancellationToken).Completion;

    public bool HasSaveSnapshotStarted(ReplaySaveOperation operation) =>
        BufferedBytes != operation.InitialMuxBytes;

    public void ResetReplayWindow()
    {
        if (!IsActive)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.BufferStopped"));

        _resettingReplayWindow = true;
        try
        {
            ObsNative.obs_output_stop(_output);
            for (int attempt = 0;
                 attempt < 80 && ObsNative.obs_output_active(_output);
                 attempt++)
            {
                Thread.Sleep(25);
            }
            if (ObsNative.obs_output_active(_output))
            {
                ObsNative.obs_output_force_stop(_output);
                for (int attempt = 0;
                     attempt < 40 && ObsNative.obs_output_active(_output);
                     attempt++)
                {
                    Thread.Sleep(25);
                }
            }
            if (ObsNative.obs_output_active(_output) ||
                !ObsNative.obs_output_start(_output))
            {
                string error = PtrToString(
                    ObsNative.obs_output_get_last_error(_output));
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? Localization.Text("L.Engine.BufferStartFailed")
                        : error);
            }

            _replayWindowStartedUtc = DateTime.UtcNow;
            Log.Write("Replay window advanced after save.");
        }
        finally
        {
            _resettingReplayWindow = false;
        }
    }

    private async Task<string> WaitForSaveCompletionAsync(
        Task<string> completion,
        CancellationToken cancellationToken)
    {
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

        string configDirectory = AppDataPaths.ObsConfigDirectory;
        Directory.CreateDirectory(configDirectory);
        _obsStarted = ObsNative.obs_startup(
            Localization.IsRussian ? "ru-RU" : "en-US",
            configDirectory,
            0);
        if (!_obsStarted)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.InitFailed"));

        string version = ObsVersion();
        if (!string.Equals(version, RequiredObsVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VersionMismatch"));
        }

        string dataRoot = Path.Combine(baseDirectory, "data");
        ObsNative.obs_add_data_path(
            ToObsPath(Path.Combine(dataRoot, "libobs")) + "/");

        // OBS injects graphics-hook*.dll into captured games. Windows can keep
        // that image mapped until the game exits, even after OBS shuts down.
        // Serve plugin data from a user cache so games never lock Captail's
        // installation or portable directory.
        string pluginDataRoot = ObsPluginDataCache.Prepare(dataRoot);
        Log.Write($"OBS plugin data cache ready: {pluginDataRoot}");

        List<CaptureInterop.MonitorInfo> monitors = CaptureInterop.EnumerateMonitors();
        CaptureInterop.MonitorInfo monitor =
            _config.MonitorIndex >= 0 && _config.MonitorIndex < monitors.Count
                ? monitors[_config.MonitorIndex]
                : monitors.FirstOrDefault()
                  ?? throw new InvalidOperationException(
                      Localization.Text("L.Engine.MonitorMissing"));
        // Both automatic modes can hook different games over pipeline lifetime,
        // so fixed OBS canvas follows selected monitor rather than one process.
        (int captureWidth, int captureHeight) = (monitor.Width, monitor.Height);
        _baseWidth = (uint)captureWidth;
        _baseHeight = (uint)captureHeight;
        (uint outputWidth, uint outputHeight) = ResolveOutputSize(
            _baseWidth,
            _baseHeight,
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
                BaseWidth = _baseWidth,
                BaseHeight = _baseHeight,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                OutputFormat = ObsNative.VideoFormat.Nv12,
                Adapter = 0,
                GpuConversion = true,
                ColorSpace = ObsNative.VideoColorSpace.Cs709,
                Range = ObsNative.VideoRange.Partial,
                ScaleType = _config.FrameRate >= 120
                    ? ObsNative.ScaleType.Bilinear
                    : ObsNative.ScaleType.Bicubic,
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
            ToObsPath(Path.Combine(pluginDataRoot, "obs-plugins", "%module%")));
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
            CapabilityCodecNames
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
        uint sourceWidth,
        uint sourceHeight,
        string setting) =>
        setting.ToLowerInvariant() switch
        {
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "1440p" => (2560, 1440),
            "2160p" => (3840, 2160),
            _ => (sourceWidth, sourceHeight),
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
            foreach (string name in DiagnosticEffectNames)
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

        if (IsAutomaticCapture)
        {
            _desktopVideoSource = CreateMonitorSource(monitor);
            _gameVideoSource = CreateGameSource(desktopFallback: true);
            _videoSource = _desktopVideoSource;

            // Game Capture stops looking for a target when it is not visible.
            // Keep only its lightweight hook detector visible while Desktop is
            // rendered. Output still contains one video source at a time.
            ObsNative.obs_source_inc_showing(_gameVideoSource);
            _automaticGameSourceShowing = true;
        }
        else
        {
            _gameVideoSource = CreateGameSource(desktopFallback: false);
            _videoSource = _gameVideoSource;
        }

        if (_videoSource == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VideoSourceFailed"));

        const uint systemMix = 1u;
        if (IsGameCapture && _config.CaptureSystemAudio)
        {
            ObsNative.obs_source_set_audio_mixers(_gameVideoSource, systemMix);
            ObsNative.obs_source_set_volume(
                _gameVideoSource,
                NormalizeVolume(_config.SystemAudioVolume));
        }

        // Captail always has one video source. Connecting it directly avoids an
        // extra scene-composition pass, which matters at 144/240 FPS.
        ObsNative.obs_set_output_source(0, _videoSource);

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

    private nint CreateGameSource(bool desktopFallback)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(
                settings,
                "capture_mode",
                "any_fullscreen");
            ObsNative.obs_data_set_bool(settings, "capture_cursor", true);
            ObsNative.obs_data_set_bool(settings, "limit_framerate", false);
            ObsNative.obs_data_set_bool(settings, "anti_cheat_hook", true);
            ObsNative.obs_data_set_int(settings, "hook_rate", 1);
            // Automatic mode keeps WASAPI active across source changes. This
            // prevents an audio gap and duplicate system/game audio at switch.
            ObsNative.obs_data_set_bool(
                settings,
                "capture_audio",
                !desktopFallback && _config.CaptureSystemAudio);
            nint source = ObsNative.obs_source_create(
                "game_capture",
                desktopFallback
                    ? "Captail Automatic Game Capture"
                    : "Captail Game Capture",
                settings,
                0);
            if (source == 0)
            {
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.VideoSourceFailed"));
            }
            return source;
        }
        finally
        {
            ObsNative.obs_data_release(settings);
        }
    }

    private static nint CreateMonitorSource(CaptureInterop.MonitorInfo monitor)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            // Windows 10 shows an unavoidable system border around WGC display
            // capture. OBS Auto prefers DXGI there and retains WGC fallback for
            // displays DXGI cannot access. Windows 11 keeps forced WGC for its
            // stronger recovery behavior and borderless modern capture path.
            long captureMethod =
                RecommendedMonitorCaptureMethod(Environment.OSVersion.Version);
            ObsNative.obs_data_set_int(
                settings,
                "method",
                captureMethod);
            Log.Write(
                $"Desktop capture backend: " +
                $"{(captureMethod == MonitorCaptureMethodWgc ? "WGC" : "Auto (DXGI preferred)")}; " +
                $"Windows {Environment.OSVersion.Version}");
            ObsNative.obs_data_set_string(settings, "monitor_id", monitor.DeviceId);
            ObsNative.obs_data_set_bool(settings, "capture_cursor", true);
            ObsNative.obs_data_set_bool(settings, "force_sdr", false);
            nint source = ObsNative.obs_source_create(
                "monitor_capture",
                "Captail Display Capture",
                settings,
                0);
            if (source == 0)
            {
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.VideoSourceFailed"));
            }
            return source;
        }
        finally
        {
            ObsNative.obs_data_release(settings);
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
                ConfigureNvenc(settings, loadProfile, encoder.Codec);
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
        EncoderLoadProfile loadProfile,
        string codec)
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
            RecommendedNvencBFrames(
                codec,
                loadProfile == EncoderLoadProfile.Standard));
    }

    internal static int RecommendedNvencBFrames(string codec, bool standardLoad) =>
        standardLoad &&
        !codec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 0;

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
        // Game target is discovered at runtime. Save to root first, then move
        // completed file into game folder without copying across volumes.
        string captureDirectory = _config.OutputDirectory;
        Directory.CreateDirectory(captureDirectory);
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
            ObsNative.obs_data_set_string(settings, "directory", captureDirectory);
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
        _replayWindowStartedUtc = DateTime.UtcNow;
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
        if (_disposing || _resettingReplayWindow)
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

        for (uint channel = 0; channel < 6; channel++)
            ObsNative.obs_set_output_source(channel, 0);

        if (_automaticGameSourceShowing && _gameVideoSource != 0)
        {
            ObsNative.obs_source_dec_showing(_gameVideoSource);
            _automaticGameSourceShowing = false;
        }

        if (_desktopVideoSource != 0)
            ObsNative.obs_source_remove(_desktopVideoSource);
        if (_gameVideoSource != 0)
            ObsNative.obs_source_remove(_gameVideoSource);
        foreach (nint source in _audioSources)
            ObsNative.obs_source_remove(source);

        if (_desktopVideoSource != 0)
        {
            ObsNative.obs_source_release(_desktopVideoSource);
            _desktopVideoSource = 0;
        }
        if (_gameVideoSource != 0)
        {
            ObsNative.obs_source_release(_gameVideoSource);
            _gameVideoSource = 0;
        }
        _videoSource = 0;
        foreach (nint source in _audioSources)
            ObsNative.obs_source_release(source);
        _audioSources.Clear();

        ObsNative.obs_wait_for_destroy_queue();
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

public sealed record ReplaySaveOperation(
    Task<string> Completion,
    ulong InitialMuxBytes,
    string? GameExecutable);
