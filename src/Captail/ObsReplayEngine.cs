using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Captail.Interop;

namespace Captail;

internal enum AdvancedProcessAudioAvailability
{
    Available,
    UnsupportedWindowsVersion,
    SourceUnavailable,
}

internal sealed class AdvancedProcessAudioUnavailableException(
    AdvancedProcessAudioAvailability availability)
    : InvalidOperationException(GetMessage(availability))
{
    internal AdvancedProcessAudioAvailability Availability { get; } = availability;

    private static string GetMessage(AdvancedProcessAudioAvailability availability) =>
        availability switch
        {
            AdvancedProcessAudioAvailability.UnsupportedWindowsVersion =>
                Localization.Text("L.Engine.ProcessAudioUnsupportedWindows"),
            AdvancedProcessAudioAvailability.SourceUnavailable =>
                Localization.Text("L.Engine.ProcessAudioSourceUnavailable"),
            _ => Localization.Text("L.Engine.AudioFailed"),
        };
}

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "libobs is thread-affine; finalizer-thread native shutdown is unsafe.")]
public sealed class ObsReplayEngine : IDisposable
{
    private const string RequiredObsVersion = "32.2.2";
    private const float ObsSdrWhiteLevel = 300.0f;
    private const float ObsHdrNominalPeakLevel = 1000.0f;
    private const int AutomaticHookStableChecks = 2;
    private const int AutomaticFallbackStableChecks = 3;
    // Game Capture still needs occasional video ticks to discover a newly launched game.
    // Two ticks per second are the minimum verified rate for a reliable hook handshake.
    private const int GameCaptureIdleFrameRate = 2;
    private const int GameCaptureIdleReleaseSeconds = 10;
    private const int GameCaptureDetectorStartupSeconds = 8;
    private const int ProcessLoopbackMinimumBuild = 19041;
    private const int WindowsGraphicsCaptureMinimumBuild = 22000;
    private const long MonitorCaptureMethodAuto = 0;
    private const long MonitorCaptureMethodDxgi = 1;
    private const long MonitorCaptureMethodWgc = 2;
    private static readonly string[] CapabilityCodecNames = ["h264", "hevc", "av1"];
    private static readonly HashSet<string> AutomaticCaptureRejectedProcesses = new(
        [
            "applicationframehost", "aseprite", "blender", "brave", "captail", "chrome", "code", "devenv", "discord",
            "discordcanary", "discordptb", "dwm", "eadesktop", "epicgameslauncher",
            "explorer", "firefox", "galaxyclient", "lockapp", "livelywpf",
            "mediaplayer", "mpv", "ms-teams", "msedge", "nvcontainer", "obs32",
            "obs64", "opera", "opera_gx", "screenclippinghost", "searchapp",
            "searchhost", "shellexperiencehost", "signal", "slack", "snippingtool",
            "sharex", "greenshot", "flameshot", "paintdotnet", "photoshop", "premiere", "obsidian",
            "spotify", "startmenuexperiencehost",
            "steam", "steamwebhelper", "systemsettings", "telegram", "textinputhost",
            "ubisoftconnect", "vivaldi", "vlc", "wallpaper32", "wallpaper64",
            "wallpaper_engine", "webviewhost", "whatsapp", "wmplayer", "zoom",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] AutomaticGamePathMarkers =
    [
        @"\steamapps\common\",
        @"\xboxgames\",
        @"\epic games\",
        @"\gog galaxy\games\",
        @"\riot games\",
    ];
    private static readonly string[] DiagnosticEffectNames =
    [
        "default.effect", "opaque.effect", "solid.effect",
        "format_conversion.effect", "premultiplied_alpha.effect",
    ];
    private static readonly object ContextGate = new();
    private static nint _obsLibrary;
    private static bool _contextOwned;

    private readonly Config _config;
    private readonly Func<IReadOnlyList<CaptureInterop.MonitorInfo>> _enumerateMonitors;
    private readonly Func<nint, DisplayColorState.HdrState?> _readMonitorHdr;
    private readonly Func<CaptureInterop.ForegroundAppInfo> _readForeground;
    private readonly bool _windowsGraphicsCaptureReady;
    private readonly object _saveGate = new();
    private readonly ObsNative.SignalCallback _savedCallback;
    private readonly ObsNative.SignalCallback _stoppedCallback;
    private readonly ObsNative.SignalCallback _fileChangedCallback;
    private DiskReplayBuffer? _diskReplayBuffer;
    private string? _saveOperationId;
    private object? _diagnosticState;
    private long _lastDiagnosticTick;
    private readonly List<nint> _audioSources = [];
    private nint _microphoneSource;
    private readonly List<nint> _audioEncoders = [];
    private readonly Dictionary<nint, nint> _processAudioSceneItems = [];
    private ProcessAudioReconciler? _processAudioReconciler;
    private int _processAudioSourceNumber;
    private nint _separateSystemSource;
    private ProcessIdentity? _separateSystemIdentity;
    private readonly SeparateAudioRouting _separateAudioRouting = new();
    private bool _separateMixerProtection;
    private bool _separateMixerSystemSoundsLogged;

    private nint _videoSource;
    private nint _desktopVideoSource;
    private nint _desktopVideoScene;
    private nint _desktopVideoSceneItem;
    private nint _desktopVideoOutputSource;
    private CaptureInterop.MonitorInfo? _captureMonitor;
    private CaptureInterop.MonitorInfo? _preferredMonitor;
    private CaptureInterop.MonitorInfo? _pendingMonitor;
    private DateTime _pendingMonitorSinceUtc;
    private bool _monitorDisconnected;
    private nint _gameVideoSource;
    private nint _gameTargetWindow;
    private string _gameTargetExecutable = "";
    private nint _gameVideoScene;
    private nint _gameVideoOutputSource;
    private string _colorUpdateHookedExecutable = "";
    private bool _gameHdr;
    private bool? _windowsHdrUserEnabled;
    private bool _pendingWindowsHdrEnable;
    private long _windowsHdrRevision;
    private nint _videoEncoder;
    private nint _output;
    private nint _outputSignals;
    private nint _processAudioScene;
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
    private uint _adapterIndex;
    private int _videoFrameRate;
    private long _preferredMonitorCaptureMethod = MonitorCaptureMethodAuto;
    private bool _automaticGameActive;
    private bool _automaticDesktopFallbackActive;
    private bool _automaticGameSourceShowing;
    private bool _gameCaptureDetectorShowing;
    private DateTime _gameCaptureDetectorActivatedUtc;
    private bool _idleOutputTransition;
    private bool _gameOutputPaused;
    private DateTime _gameHookLostUtc;
    private string _activeGameExecutable = "";
    private string _recordingGameExecutable = "";
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
    internal AdvancedProcessAudioAvailability ProcessAudioAvailability
    {
        get;
        private set;
    } = AdvancedProcessAudioAvailability.SourceUnavailable;
    internal bool RequiresProcessAudioMonitoring =>
        ShouldMonitorProcessAudio(
            _config,
            ProcessAudioAvailability);
    public bool IsGameCapture { get; }
    public bool IsAutomaticCapture { get; }
    public bool IsContinuousRecording { get; }
    public string RecordingPath { get; private set; } = "";
    internal bool IsGameCaptureDetectorActive => _gameCaptureDetectorShowing;
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
                    : _automaticDesktopFallbackActive
                        ? Localization.Format(
                            "L.Engine.AutoGameFallback",
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

    public bool IsGameHooked
    {
        get
        {
            if (!IsRawGameHooked)
                return false;
            string executable = ReadHookedGameExecutable();
            return ShouldUseForegroundGameCapture(executable, _readForeground(), true);
        }
    }

    private bool IsRawGameHooked =>
        _gameVideoSource != 0 &&
        ReadBoolProcedure(
            ObsNative.obs_source_get_proc_handler(_gameVideoSource),
            "get_hooked",
            "hooked");

    public string ActiveGameExecutable => _activeGameExecutable;
    public string RecordingGameExecutable =>
        IsContinuousRecording && IsAutomaticCapture
            ? _recordingGameExecutable
            : _activeGameExecutable;

    internal static bool IsAutomaticCaptureCandidate(string executable)
    {
        string processName = Path.GetFileNameWithoutExtension(executable.Trim());
        return processName.Length > 0 &&
               !AutomaticCaptureRejectedProcesses.Contains(processName);
    }

    internal static long RecommendedMonitorCaptureMethod(
        Version osVersion,
        bool windowsGraphicsCaptureReady = true) =>
        windowsGraphicsCaptureReady &&
        (osVersion.Major > 10 ||
         (osVersion.Major == 10 &&
          osVersion.Build >= WindowsGraphicsCaptureMinimumBuild))
            ? MonitorCaptureMethodWgc
            : MonitorCaptureMethodDxgi;

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

    internal static bool ShouldUseAutomaticDesktopFallback(
        string foregroundExecutable,
        string foregroundPath,
        bool isFullscreen)
    {
        if (!isFullscreen ||
            !IsAutomaticCaptureCandidate(foregroundExecutable) ||
            string.IsNullOrWhiteSpace(foregroundPath))
        {
            return false;
        }

        string normalizedPath = foregroundPath.Replace('/', '\\');
        return AutomaticGamePathMarkers.Any(marker =>
            normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldWakeGameCapture(
        CaptureInterop.ForegroundAppInfo foreground)
    {
        if (!IsAutomaticCaptureCandidate(foreground.Executable))
            return false;

        bool knownGame = GameCatalog.ExactNameForExecutable(foreground.Executable) is not null;
        // Unknown browser/desktop UI windows are not game evidence, even fullscreen.
        if (!knownGame && IsDesktopApplicationWindow(foreground.WindowClass))
            return false;

        string normalizedPath = foreground.FullPath.Replace('/', '\\');
        return knownGame || foreground.IsFullscreen ||
               foreground.WindowClass is "UnityWndClass" or "UnrealWindow" or "SDL_app" or "GLFW30" ||
               AutomaticGamePathMarkers.Any(marker =>
                   normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDesktopApplicationWindow(string windowClass) =>
        windowClass.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase) ||
        windowClass.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase) ||
        windowClass.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase) ||
        windowClass.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
        windowClass.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) ||
        windowClass.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) ||
        windowClass.Equals("#32770", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldUseForegroundGameCapture(
        string hookedExecutable, CaptureInterop.ForegroundAppInfo foreground, bool hasVideo) =>
        ShouldWakeGameCapture(foreground) &&
        ShouldUseAutomaticGameCapture(hookedExecutable, foreground.Executable, hasVideo);

    internal static string GameCaptureWindowSetting(CaptureInterop.ForegroundAppInfo foreground)
    {
        if (foreground.Window == 0 || string.IsNullOrEmpty(foreground.WindowClass) ||
            !ShouldWakeGameCapture(foreground))
            return "";
        static string Encode(string value) => value.Replace("#", "#22").Replace(":", "#3A");
        return $"{Encode(foreground.Title)}:{Encode(foreground.WindowClass)}:{Encode(Path.GetFileName(foreground.Executable))}";
    }

    private void RefreshGameCaptureTarget(CaptureInterop.ForegroundAppInfo foreground)
    {
        string window = GameCaptureWindowSetting(foreground);
        nint handle = window.Length == 0 ? 0 : foreground.Window;
        string executable = handle == 0 ? "" : foreground.Executable;
        if (handle == _gameTargetWindow && string.Equals(executable, _gameTargetExecutable,
                StringComparison.OrdinalIgnoreCase))
            return;

        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(settings, "window", window);
            ObsNative.obs_source_update(_gameVideoSource, settings);
            _gameTargetWindow = handle;
            _gameTargetExecutable = executable;
            _colorUpdateHookedExecutable = "";
            Log.Event(DiagnosticLevel.Info, "Capture", "capture.game-target.changed",
                new { executable, window = $"0x{handle:X}" });
        }
        finally { ObsNative.obs_data_release(settings); }
    }

    internal static string? ResolveReplayGameExecutable(
        bool isAutomaticCapture,
        bool automaticGameIdentified,
        string activeGameExecutable,
        bool isGameHooked,
        string hookedExecutable)
    {
        if (isAutomaticCapture)
        {
            return automaticGameIdentified &&
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
            if (_started && IsGameCapture && !IsGameHooked)
                return true;
            if (!IsActive)
                return false;

            if (_gameOutputPaused)
                return true;

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
        _diskReplayBuffer?.BufferedBytes ?? (_output == 0 ? 0 : ObsNative.obs_output_get_total_bytes(_output));
    public int AvailableReplaySeconds
    {
        get
        {
            if (IsContinuousRecording || !IsActive ||
                _replayWindowStartedUtc == default)
                return 0;

            if (_diskReplayBuffer is not null)
                return _diskReplayBuffer.GetAvailableSeconds((double)EncodedFrameCount / _config.FrameRate);

            int elapsed = (int)Math.Floor(
                (DateTime.UtcNow - _replayWindowStartedUtc).TotalSeconds);
            return Math.Clamp(elapsed, 0, _config.BufferSeconds);
        }
    }
    public uint TotalRenderedFrames => ObsNative.obs_get_total_frames();
    public uint LaggedRenderedFrames => ObsNative.obs_get_lagged_frames();
    internal int CaptureMonitorIndex => _captureMonitor?.Index ?? _config.MonitorIndex;
    internal object? CachedDiagnosticState => Volatile.Read(ref _diagnosticState);

    // Only the serialized OBS thread samples native state. Error handlers use the cache.
    internal void WriteDiagnosticState(string reason, bool periodic = false)
    {
        long now = Environment.TickCount64;
        if (periodic && now - _lastDiagnosticTick < 30000)
            return;
        _lastDiagnosticTick = now;
        try
        {
            object state = new
            {
                reason, sampledAt = DateTimeOffset.UtcNow, active = IsActive, _started, _gameOutputPaused,
                mode = IsContinuousRecording ? "recording" : "replay",
                storage = _diskReplayBuffer is null ? "ram" : "disk",
                source = _config.CaptureSource, encoder = ActiveEncoder,
                frames = EncodedFrameCount, renderedFrames = TotalRenderedFrames,
                laggedFrames = LaggedRenderedFrames,
                ramBytes = _diskReplayBuffer is null ? BufferedBytes : (ulong?)null,
                disk = _diskReplayBuffer?.DiagnosticState,
                managedBytes = GC.GetTotalMemory(false),
            };
            Volatile.Write(ref _diagnosticState, state);
            Log.Event(DiagnosticLevel.Info, "Capture", "capture.state", state);
        }
        catch (Exception exception)
        {
            Log.Event(DiagnosticLevel.Warning, "Capture", "capture.state-unavailable",
                new { reason, exception.Message });
        }
    }

    /// <summary>
    /// Updates game-hook metadata and Desktop fallback without restarting
    /// encoder or replay buffer.
    /// Must be called on Captail's serialized OBS thread.
    /// </summary>
    public bool RefreshCaptureState()
    {
        bool monitorChanged = RefreshCaptureMonitor();
        if (_gameVideoSource == 0)
            return monitorChanged;

        CaptureInterop.ForegroundAppInfo foreground = _readForeground();
        RefreshGameCaptureTarget(foreground);
        bool hooked = IsRawGameHooked;
        string executable = hooked ? ReadHookedGameExecutable() : "";
        RefreshGameColorState(hooked, executable);

        if (IsGameCapture && !_gameCaptureDetectorShowing)
        {
            if (!ShouldWakeGameCapture(foreground))
                return false;

            SetGameCaptureDetectorShowing(true);
            Log.Write("Game candidate found; Game Capture detector activated.");
            return true;
        }

        if (IsGameCapture)
        {
            bool validHook = hooked && ShouldUseForegroundGameCapture(executable, foreground,
                ObsNative.obs_source_get_width(_gameVideoSource) > 0 &&
                ObsNative.obs_source_get_height(_gameVideoSource) > 0);
            string previousExecutable = _activeGameExecutable;
            bool outputWasActive = IsActive;
            bool pausedBefore = _gameOutputPaused;

            if (validHook)
            {
                _activeGameExecutable = executable;
                RememberContinuousRecordingGame(executable);
                _lastRejectedAutomaticExecutable = "";
                _gameHookLostUtc = default;
                ResumeOrStartGameReplayBuffer();
            }
            else
            {
                _activeGameExecutable = "";
                if (hooked)
                {
                    if (!string.Equals(
                            _lastRejectedAutomaticExecutable,
                            executable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _lastRejectedAutomaticExecutable = executable;
                        Log.Write(
                            "Game Capture ignored non-game process: " +
                            Path.GetFileName(executable));
                    }
                    if (!ShouldWakeGameCapture(foreground))
                        SetGameCaptureDetectorShowing(false);
                }
                else
                {
                    _lastRejectedAutomaticExecutable = "";
                }

                SuspendOrReleaseGameReplayBuffer();

                if (!hooked && !IsActive &&
                    DateTime.UtcNow - _gameCaptureDetectorActivatedUtc >=
                    TimeSpan.FromSeconds(GameCaptureDetectorStartupSeconds) &&
                    !ShouldWakeGameCapture(foreground))
                {
                    SetGameCaptureDetectorShowing(false);
                }
            }

            return outputWasActive != IsActive ||
                   pausedBefore != _gameOutputPaused ||
                   !string.Equals(
                       previousExecutable,
                       _activeGameExecutable,
                       StringComparison.OrdinalIgnoreCase);
        }

        if (_desktopVideoSource == 0)
            return false;

        bool processCandidate = hooked &&
                                IsAutomaticCaptureCandidate(executable);
        bool hasVideo = hooked &&
                        ObsNative.obs_source_get_width(_gameVideoSource) > 0 &&
                        ObsNative.obs_source_get_height(_gameVideoSource) > 0;
        bool hookCandidate = ShouldUseForegroundGameCapture(
            executable,
            foreground,
            hasVideo);
        if (hookCandidate)
        {
            _lastRejectedAutomaticExecutable = "";
            bool changed = SetAutomaticDesktopFallback(false, "") || monitorChanged;
            if (_automaticGameActive)
            {
                _activeGameExecutable = executable;
                RememberContinuousRecordingGame(executable);
                return changed;
            }

            if (!AdvanceAutomaticCandidate(
                    executable,
                    AutomaticHookStableChecks))
            {
                return changed;
            }

            return SwitchAutomaticCapture(useGame: true, executable) || changed;
        }

        bool stateChanged = monitorChanged;
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
            stateChanged |= SwitchAutomaticCapture(false, "");

        bool fallbackCandidate = ShouldWakeGameCapture(foreground) && ShouldUseAutomaticDesktopFallback(
            foreground.Executable,
            foreground.FullPath,
            foreground.IsFullscreen);
        if (!fallbackCandidate)
        {
            ResetPendingAutomaticHook();
            return SetAutomaticDesktopFallback(false, "") || stateChanged;
        }

        if (_automaticDesktopFallbackActive &&
            string.Equals(
                _activeGameExecutable,
                foreground.Executable,
                StringComparison.OrdinalIgnoreCase))
        {
            ResetPendingAutomaticHook();
            return stateChanged;
        }

        stateChanged |= SetAutomaticDesktopFallback(false, "");
        if (!AdvanceAutomaticCandidate(
                foreground.Executable,
                AutomaticFallbackStableChecks))
        {
            return stateChanged;
        }

        return SetAutomaticDesktopFallback(
                   true,
                   foreground.Executable) || stateChanged;
    }

    private bool SwitchAutomaticCapture(bool useGame, string executable)
    {
        if (useGame == _automaticGameActive)
            return false;

        nint target = useGame
            ? _gameVideoOutputSource
            : _desktopVideoOutputSource;
        ObsNative.obs_set_output_source(0, target);
        _videoSource = target;
        _automaticGameActive = useGame;
        _activeGameExecutable = useGame
            ? executable
            : _automaticDesktopFallbackActive
                ? _activeGameExecutable
                : "";
        if (useGame)
            RememberContinuousRecordingGame(executable);
        Log.Write(
            useGame
                ? $"Automatic capture switched to Game Capture: " +
                  $"{Path.GetFileName(_activeGameExecutable)}"
                : "Automatic capture returned to Desktop Capture.");
        return true;
    }

    private bool SetAutomaticDesktopFallback(bool active, string executable)
    {
        if (active == _automaticDesktopFallbackActive &&
            (!active || string.Equals(
                _activeGameExecutable,
                executable,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _automaticDesktopFallbackActive = active;
        _activeGameExecutable = active ? executable : "";
        if (active)
            RememberContinuousRecordingGame(executable);
        Log.Write(
            active
                ? $"Automatic capture identified game but kept Desktop Capture: " +
                  $"{Path.GetFileName(executable)}"
                : "Automatic Desktop fallback returned to normal Desktop Capture.");
        return true;
    }

    private void RememberContinuousRecordingGame(string executable)
    {
        if (!IsContinuousRecording || string.IsNullOrWhiteSpace(executable))
            return;

        _recordingGameExecutable = executable;
    }

    private bool AdvanceAutomaticCandidate(string executable, int requiredChecks)
    {
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
        if (_automaticHookStableChecks < requiredChecks)
            return false;

        ResetPendingAutomaticHook();
        return true;
    }

    private void ResetPendingAutomaticHook()
    {
        _pendingAutomaticGameExecutable = "";
        _automaticHookStableChecks = 0;
    }

    public ObsReplayEngine(
        Config config,
        bool windowsGraphicsCaptureReady = true)
        : this(config, windowsGraphicsCaptureReady, CaptureInterop.EnumerateMonitors)
    {
    }

    internal ObsReplayEngine(
        Config config,
        bool windowsGraphicsCaptureReady,
        Func<IReadOnlyList<CaptureInterop.MonitorInfo>> enumerateMonitors,
        Func<nint, DisplayColorState.HdrState?>? readMonitorHdr = null,
        Func<CaptureInterop.ForegroundAppInfo>? readForeground = null)
    {
        _config = config;
        _enumerateMonitors = enumerateMonitors;
        _readMonitorHdr = readMonitorHdr ?? DisplayColorState.ReadHdrState;
        _readForeground = readForeground ?? CaptureInterop.ForegroundApplication;
        _windowsGraphicsCaptureReady = windowsGraphicsCaptureReady;
        IsGameCapture = string.Equals(
            config.CaptureSource,
            "game",
            StringComparison.OrdinalIgnoreCase);
        IsAutomaticCapture = !IsGameCapture;
        IsContinuousRecording = string.Equals(
            config.CaptureMode,
            "recording",
            StringComparison.OrdinalIgnoreCase);
        _savedCallback = OnReplaySaved;
        _stoppedCallback = OnOutputStopped;
        _fileChangedCallback = OnDiskReplayFileChanged;
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
            ProcessAudioAvailability = DetectProcessAudioAvailability(
                Environment.OSVersion.Version,
                HasInputType("captail_process_audio_capture"));
            if (!IsAudioRoutingAvailable(_config, ProcessAudioAvailability))
            {
                throw new AdvancedProcessAudioUnavailableException(
                    ProcessAudioAvailability);
            }
            EnsureConfiguredCodecIsSupported();
            CreateSources();
            CreateEncoders();
            CreateOutput();
            ApplyPreferredMonitorCaptureMethod();
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
        if (IsContinuousRecording)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.ReplayUnavailableWhileRecording"));
        if (IsAutomaticCapture)
            RefreshCaptureState();
        if (IsGameCapture && _gameOutputPaused)
        {
            if (!ObsNative.obs_output_pause(_output, false))
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.BufferStartFailed"));
            _gameOutputPaused = false;
            _gameHookLostUtc = DateTime.UtcNow;
            Log.Write("Paused Game Capture buffer resumed for replay save.");
        }

        Task<string> completion;
        ulong initialMuxBytes;
        Task? snapshotReady = null;
        lock (_saveGate)
        {
            if (!IsActive)
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.BufferStopped"));
            if (_pendingSave is not null)
                throw new InvalidOperationException(
                    Localization.Text("L.Engine.SavePending"));

            initialMuxBytes = BufferedBytes;
            _saveOperationId = Log.OperationId ?? Guid.NewGuid().ToString("N")[..12];
            Log.Event(DiagnosticLevel.Info, "Replay", "replay.native-request", new
            {
                storage = _diskReplayBuffer is null ? "ram" : "disk",
                initialMuxBytes, state = CachedDiagnosticState,
            }, _saveOperationId);
            if (_diskReplayBuffer is not null)
            {
                (completion, snapshotReady) = _diskReplayBuffer.BeginSave(() =>
                {
                    nint procedures = ObsNative.obs_output_get_proc_handler(_output);
                    bool accepted = ReadBoolProcedure(procedures, "split_file", "split_file_enabled");
                    Log.Event(accepted ? DiagnosticLevel.Info : DiagnosticLevel.Error, "Replay", "replay.split-request",
                        new { accepted }, _saveOperationId);
                    return accepted;
                }, cancellationToken, _saveOperationId);
            }
            else
            {
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
        }

        bool isGameHooked = IsGameHooked;
        string hookedExecutable = isGameHooked ? ReadHookedGameExecutable() : "";
        string? replayGameExecutable = ResolveReplayGameExecutable(
            IsAutomaticCapture,
            _automaticGameActive || _automaticDesktopFallbackActive,
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
            ObserveReplaySaveAsync(_diskReplayBuffer is null
                ? WaitForSaveCompletionAsync(completion, cancellationToken) : completion, _saveOperationId),
            initialMuxBytes,
            replayGameExecutable) { SnapshotReady = snapshotReady };
    }

    private async Task<string> ObserveReplaySaveAsync(Task<string> completion, string operationId)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string path = await completion.ConfigureAwait(false);
            Log.Event(DiagnosticLevel.Info, "Replay", "replay.native-completed",
                new { path, elapsedMs = elapsed.ElapsedMilliseconds }, operationId);
            return path;
        }
        catch (Exception exception)
        {
            bool cancelled = exception is OperationCanceledException;
            Log.Event(cancelled ? DiagnosticLevel.Info : DiagnosticLevel.Error, "Replay",
                cancelled ? "replay.native-cancelled" : "replay.native-failed",
                new { elapsedMs = elapsed.ElapsedMilliseconds, state = CachedDiagnosticState }, operationId, exception);
            throw;
        }
        finally
        {
            lock (_saveGate)
                if (_saveOperationId == operationId)
                    _saveOperationId = null;
        }
    }

    public Task<string> SaveReplayAsync(
        CancellationToken cancellationToken = default) =>
        BeginSaveReplay(cancellationToken).Completion;

    public bool HasSaveSnapshotStarted(ReplaySaveOperation operation) =>
        operation.SnapshotReady is not null
            ? operation.SnapshotReady.IsCompletedSuccessfully
            : BufferedBytes != operation.InitialMuxBytes;

    public void ResetReplayWindow()
    {
        if (IsContinuousRecording)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.ReplayUnavailableWhileRecording"));
        if (!IsActive)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.BufferStopped"));

        if (_diskReplayBuffer is not null)
        {
            _diskReplayBuffer.AdvanceWindow();
            _replayWindowStartedUtc = DateTime.UtcNow;
            Log.Write("Disk replay window advanced after snapshot.");
            return;
        }

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
            Log.Event(DiagnosticLevel.Error, "Replay", "replay.timeout",
                new { stage = "native-saved-callback", limitSeconds = 45, state = CachedDiagnosticState }, _saveOperationId);
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
            Localization.CultureName,
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

        IReadOnlyList<CaptureInterop.MonitorInfo> monitors = _enumerateMonitors();
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
        _adapterIndex = GpuPreference.HighPerformanceAdapterIndex();

        ResetObsVideo(
            IsGameCapture && !IsContinuousRecording
                ? GameCaptureIdleFrameRate
                : _config.FrameRate,
            diagnoseOnFailure: true);

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

    private void ResetObsVideo(int frameRate, bool diagnoseOnFailure = false)
    {
        if (_videoFrameRate == frameRate)
            return;

        string baseDirectory = AppContext.BaseDirectory;
        nint graphicsModule = Marshal.StringToCoTaskMemUTF8(
            Path.Combine(baseDirectory, "libobs-d3d11.dll"));
        try
        {
            var video = new ObsNative.VideoInfo
            {
                GraphicsModule = graphicsModule,
                FpsNum = (uint)frameRate,
                FpsDen = 1,
                BaseWidth = _baseWidth,
                BaseHeight = _baseHeight,
                OutputWidth = _outputWidth,
                OutputHeight = _outputHeight,
                OutputFormat = ObsNative.VideoFormat.Nv12,
                Adapter = _adapterIndex,
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
                if (diagnoseOnFailure)
                {
                    DiagnoseEffects(
                        baseDirectory,
                        Path.Combine(baseDirectory, "data"));
                }
                throw new InvalidOperationException(
                    Localization.Format("L.Engine.VideoFailed", result));
            }
            ObsNative.obs_set_video_levels(
                ObsSdrWhiteLevel,
                ObsHdrNominalPeakLevel);
            _videoFrameRate = frameRate;
            if (_videoEncoder != 0)
            {
                ObsNative.obs_encoder_set_video(
                    _videoEncoder,
                    ObsNative.obs_get_video());
            }
            Log.Write($"OBS video clock: {frameRate} FPS.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(graphicsModule);
        }
    }

    private EncoderCapabilities DetectCapabilities()
    {
        string adapterName = Localization.Text("L.Gpu.Generic");
        ObsNative.AdapterCallback callback = (_, name, id) =>
        {
            if (id == _adapterIndex)
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
        IReadOnlyList<CaptureInterop.MonitorInfo> monitors = _enumerateMonitors();
        CaptureInterop.MonitorInfo monitor =
            _config.MonitorIndex >= 0 && _config.MonitorIndex < monitors.Count
                ? monitors[_config.MonitorIndex]
                : monitors.First();

        _captureMonitor = _preferredMonitor = monitor;

        if (IsAutomaticCapture)
        {
            _desktopVideoSource = CreateMonitorSource(monitor);
            _desktopVideoOutputSource = CreateStretchedOutput(
                _desktopVideoSource,
                "Captail Desktop Canvas",
                out _desktopVideoScene,
                out _desktopVideoSceneItem);
            _gameVideoSource = CreateGameSource(desktopFallback: true);
            _gameVideoOutputSource = CreateStretchedGameOutput(
                _gameVideoSource);
            _videoSource = _desktopVideoOutputSource;

            // Game Capture stops looking for a target when it is not visible.
            // Keep only its lightweight hook detector visible while Desktop is
            // rendered. Output still contains one video source at a time.
            ObsNative.obs_source_inc_showing(_gameVideoSource);
            _automaticGameSourceShowing = true;
        }
        else
        {
            _gameVideoSource = CreateGameSource(desktopFallback: false);
            _gameVideoOutputSource = CreateStretchedGameOutput(
                _gameVideoSource);
            _videoSource = _gameVideoOutputSource;
            // Detector stays dormant until a plausible foreground game appears.
            // This prevents hooks into unrelated fullscreen applications and
            // leaves GPU nearly idle while no game is running.
        }

        if (_videoSource == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VideoSourceFailed"));

        // Both sources fill the fixed recording canvas. A display mode switch
        // can change desktop dimensions while an output is still recording.
        ObsNative.obs_set_output_source(0, _videoSource);

        bool usesDetectedGameAudio = UsesDetectedGameAudio(
            _config,
            ProcessAudioAvailability);
        if (UsesSeparateGameAudio(_config) &&
            ProcessAudioAvailability != AdvancedProcessAudioAvailability.Available)
            throw new AdvancedProcessAudioUnavailableException(ProcessAudioAvailability);
        if (IsAdvancedAudioRouting || usesDetectedGameAudio)
        {
            // Internal mixers must not be retained by the main canvas past shutdown.
            _processAudioScene = ObsNative.obs_scene_create_private(
                "Captail Process Audio Mixer");
            if (_processAudioScene == 0)
            {
                throw new InvalidOperationException(
                    "Could not create the process audio mixer.");
            }
            nint sceneSource = ObsNative.obs_scene_get_source(_processAudioScene);
            ObsNative.obs_source_set_audio_mixers(sceneSource, 0x3Fu);
            ObsNative.obs_set_output_source(1, sceneSource);
            if (usesDetectedGameAudio)
                Log.Write("Detected game audio routing enabled.");
            if (UsesSeparateGameAudio(_config))
            {
                ReconcileSeparateSystemAudio(null);
            }
        }

        if (!IsAdvancedAudioRouting &&
            !usesDetectedGameAudio &&
            _config.CaptureSystemAudio)
        {
            nint system = CreateAudioSource(
                "wasapi_output_capture",
                IsGameCapture ? "Captail Game Audio" : "Captail System Audio",
                _config.SystemAudioDeviceId,
                _config.SeparateAudioTracks ? 1u : 1u,
                NormalizeVolume(_config.SystemAudioVolume));
            _audioSources.Add(system);
            ObsNative.obs_set_output_source(1, system);
        }

        if (_config.CaptureMicrophone)
        {
            uint micMix = IsAdvancedAudioRouting
                ? MixerBit(_config.AdvancedMicrophoneTrack)
                : _config.SeparateAudioTracks && _config.CaptureSystemAudio
                    ? MixerBit(3)
                    : 1u;
            nint microphone = CreateAudioSource(
                "wasapi_input_capture",
                "Captail Microphone",
                _config.MicrophoneDeviceId,
                micMix,
                NormalizeVolume(_config.MicrophoneVolume) *
                DecibelsToLinear(_config.MicrophoneBoostDb));
            _audioSources.Add(microphone);
            _microphoneSource = microphone;
            ObsNative.obs_set_output_source(2, microphone);
        }

    }

    private nint CreateGameSource(bool desktopFallback)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            DisplayColorState.HdrState? color = _readMonitorHdr(_captureMonitor?.Handle ?? 0);
            _windowsHdrUserEnabled = color?.UserEnabled;
            _windowsHdrRevision = color?.Revision ?? 0;
            _pendingWindowsHdrEnable = false;
            _gameHdr = color is { UserEnabled: true, Active: true };
            ObsNative.obs_data_set_string(settings, "rgb10a2_space", _gameHdr ? "2100pq" : "srgb");
            Log.Event(DiagnosticLevel.Info, "Capture", "capture.color-space.selected", new
            {
                monitor = $"0x{_captureMonitor?.Handle ?? 0:X}",
                hdrEnabled = _gameHdr,
                windowsHdrUserEnabled = _windowsHdrUserEnabled,
                gameRgb10a2Space = _gameHdr ? "2100pq" : "srgb",
                desktop = "native HDR/SDR surface detection",
                output = "NV12/BT.709/Partial",
            });
            ObsNative.obs_data_set_string(
                settings,
                "capture_mode",
                "window");
            ObsNative.obs_data_set_string(settings, "window", "");
            // Executable matching must never fall back to a different application.
            ObsNative.obs_data_set_int(settings, "priority", 2);
            ObsNative.obs_data_set_bool(settings, "capture_cursor", true);
            ObsNative.obs_data_set_bool(settings, "limit_framerate", false);
            ObsNative.obs_data_set_bool(settings, "anti_cheat_hook", true);
            ObsNative.obs_data_set_int(settings, "hook_rate", 1);
            // Automatic mode keeps WASAPI active across source changes. This
            // prevents an audio gap and duplicate system/game audio at switch.
            ObsNative.obs_data_set_bool(
                settings,
                "capture_audio",
                false);
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

    private nint CreateMonitorSource(CaptureInterop.MonitorInfo monitor)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            // WGC excludes protected video from captured frames instead of
            // competing with DRM presentation through Desktop Duplication.
            // Windows 11 can grant borderless capture. Windows 10 must force
            // DXGI because OBS Auto can select WGC and show a yellow border.
            _preferredMonitorCaptureMethod =
                RecommendedMonitorCaptureMethod(
                    Environment.OSVersion.Version,
                    _windowsGraphicsCaptureReady);
            long captureMethod = _preferredMonitorCaptureMethod ==
                                 MonitorCaptureMethodWgc
                ? MonitorCaptureMethodAuto
                : _preferredMonitorCaptureMethod;
            ObsNative.obs_data_set_int(
                settings,
                "method",
                captureMethod);
            Log.Write(
                $"Desktop capture startup backend: {(captureMethod == MonitorCaptureMethodDxgi ? "DXGI" : "Auto")}; " +
                $"preferred=" +
                $"{(_preferredMonitorCaptureMethod == MonitorCaptureMethodWgc ? "WGC" : captureMethod == MonitorCaptureMethodDxgi ? "DXGI" : "Auto")}; " +
                $"Windows {Environment.OSVersion.Version}");
            ObsNative.obs_data_set_string(settings, "monitor_id", monitor.DeviceId);
            ObsNative.obs_data_set_bool(settings, "capture_cursor", true);
            // Preserve HDR pixels until OBS tone maps to the SDR recording
            // canvas. WGC/DXGI select the surface from the current display state.
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

    private string ReadHookedGameExecutable()
    {
        string executable = ReadStringProcedure(
            ObsNative.obs_source_get_proc_handler(_gameVideoSource),
            "get_hooked",
            "executable");
        // OBS 32.2.2 clears the reported name on any settings update, including
        // a live color-space change that leaves the existing hook active.
        return string.IsNullOrWhiteSpace(executable)
            ? _colorUpdateHookedExecutable
            : executable;
    }

    internal void RefreshDisplayColorState()
    {
        if (_gameVideoSource == 0) return;
        bool hooked = IsRawGameHooked;
        RefreshGameColorState(hooked, hooked ? ReadHookedGameExecutable() : "");
    }

    private void RefreshGameColorState(bool hooked, string executable)
    {
        if (!hooked)
            _colorUpdateHookedExecutable = "";

        // A transient topology-query failure must not replace a known mode or
        // erase the Windows setting edge that can release the SDR lock.
        nint monitor = _captureMonitor?.Handle ?? 0;
        DisplayColorState.HdrState? current = _readMonitorHdr(monitor);
        if (current is not { } state)
            return;
        bool firstKnownState = _windowsHdrUserEnabled is null;
        bool retainHdr = _gameHdr;
        if (state.Revision > 0)
        {
            // The independent observer can see several transitions while OBS
            // is busy. Consume their ordering even if the final flags match.
            if (state.LastSdrRevision > _windowsHdrRevision)
            {
                retainHdr = false;
                _pendingWindowsHdrEnable = false;
            }
            if (state.LastEnableRevision > _windowsHdrRevision &&
                state.LastEnableRevision >= state.LastSdrRevision)
                _pendingWindowsHdrEnable = true;
            _windowsHdrRevision = state.Revision;
        }
        else if (_windowsHdrUserEnabled == false && state.UserEnabled)
            _pendingWindowsHdrEnable = true;
        _windowsHdrUserEnabled = state.UserEnabled;
        if (!state.UserEnabled)
            _pendingWindowsHdrEnable = false;

        // Once HDR becomes inactive, keep SDR for this capture session. A
        // rehook, source switch, or active-mode recovery cannot enable PQ.
        // Only Windows "Use HDR" off/on releases the lock. Its active output
        // may arrive later than the setting, so retain that enable request.
        bool hdr = state.UserEnabled && state.Active &&
            (retainHdr || _pendingWindowsHdrEnable || firstKnownState);
        if (hdr)
            _pendingWindowsHdrEnable = false;
        if (hdr == _gameHdr)
            return;

        nint settings = ObsNative.obs_data_create();
        try
        {
            // This OBS setting only affects 10-bit RGB input. Native SDR8 and
            // float scRGB handling stays independent of the display heuristic.
            ObsNative.obs_data_set_string(settings, "rgb10a2_space", hdr ? "2100pq" : "srgb");
            if (hooked && !string.IsNullOrWhiteSpace(executable))
                _colorUpdateHookedExecutable = executable;
            ObsNative.obs_source_update(_gameVideoSource, settings);
            _gameHdr = hdr;
            Log.Event(DiagnosticLevel.Info, "Capture", "capture.color-space.changed", new
            {
                monitor = $"0x{monitor:X}",
                hdrEnabled = hdr,
                windowsHdrUserEnabled = state.UserEnabled,
                windowsHdrActive = state.Active,
                windowsHdrRevision = state.Revision,
                gameRgb10a2Space = hdr ? "2100pq" : "srgb",
                output = "NV12/BT.709/Partial",
            });
        }
        finally { ObsNative.obs_data_release(settings); }
    }

    private bool RefreshCaptureMonitor()
    {
        if (_preferredMonitor is null)
            return false;

        IReadOnlyList<CaptureInterop.MonitorInfo> monitors = _enumerateMonitors();
        CaptureInterop.MonitorInfo? target = ResolveCaptureMonitor(
            monitors, _preferredMonitor, _captureMonitor, _config.MonitorIndex);
        if (target is null)
        {
            _monitorDisconnected = true;
            _pendingMonitor = null;
            return false;
        }
        if (IsGameCapture)
        {
            bool changed = target != _captureMonitor;
            _captureMonitor = target;
            return changed;
        }
        if (target == _captureMonitor && !_monitorDisconnected)
        {
            _pendingMonitor = null;
            return false;
        }
        // Display drivers often publish several intermediate layouts while
        // switching modes. Wait for one layout to remain stable before rebinding.
        if (target != _pendingMonitor)
        {
            _pendingMonitor = target;
            _pendingMonitorSinceUtc = DateTime.UtcNow;
            return false;
        }
        if (DateTime.UtcNow - _pendingMonitorSinceUtc < TimeSpan.FromMilliseconds(750))
            return false;

        if (!_monitorDisconnected && _captureMonitor is not null &&
            target with { Index = _captureMonitor.Index } == _captureMonitor)
        {
            // An enumeration-order change only changes the UI index.
            _captureMonitor = target;
            _pendingMonitor = null;
            return true;
        }

        nint replacement = CreateMonitorSource(target);
        nint item = ObsNative.obs_scene_add(_desktopVideoScene, replacement);
        if (item == 0)
        {
            ObsNative.obs_source_release(replacement);
            throw new InvalidOperationException(Localization.Text("L.Engine.VideoSourceFailed"));
        }
        SetCanvasBounds(item);
        ObsNative.obs_sceneitem_remove(_desktopVideoSceneItem);
        ObsNative.obs_source_remove(_desktopVideoSource);
        ObsNative.obs_source_release(_desktopVideoSource);
        _desktopVideoSource = replacement;
        _desktopVideoSceneItem = item;
        _captureMonitor = target;
        _pendingMonitor = null;
        _monitorDisconnected = false;
        ApplyPreferredMonitorCaptureMethod();
        Log.Write($"Desktop monitor rebound: index={target.Index}, id={target.DeviceId}, " +
                  $"size={target.Width}x{target.Height}; recording canvas remains {_baseWidth}x{_baseHeight}.");
        return true;
    }

    internal static CaptureInterop.MonitorInfo? ResolveCaptureMonitor(
        IReadOnlyList<CaptureInterop.MonitorInfo> monitors,
        CaptureInterop.MonitorInfo preferred,
        CaptureInterop.MonitorInfo? current,
        int configuredIndex)
    {
        // Preserve identity across reordering, then match the old desktop
        // position for dual-mode displays which expose a new device identity.
        return monitors.FirstOrDefault(m => !string.IsNullOrEmpty(preferred.DeviceId) &&
                   string.Equals(m.DeviceId, preferred.DeviceId, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m => string.IsNullOrEmpty(preferred.DeviceId) &&
                   m.Handle == preferred.Handle)
               ?? monitors.FirstOrDefault(m => m.Left == preferred.Left && m.Top == preferred.Top)
               ?? monitors.OrderByDescending(OverlapArea).FirstOrDefault(m => OverlapArea(m) > 0)
               ?? monitors.FirstOrDefault(m => current is not null && !string.IsNullOrEmpty(current.DeviceId) &&
                   string.Equals(m.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m => m.Index == configuredIndex)
               ?? monitors.FirstOrDefault();

        long OverlapArea(CaptureInterop.MonitorInfo monitor)
        {
            long width = Math.Max(0, Math.Min((long)monitor.Left + monitor.Width,
                (long)preferred.Left + preferred.Width) - Math.Max(monitor.Left, preferred.Left));
            long height = Math.Max(0, Math.Min((long)monitor.Top + monitor.Height,
                (long)preferred.Top + preferred.Height) - Math.Max(monitor.Top, preferred.Top));
            return width * height;
        }
    }

    private void ApplyPreferredMonitorCaptureMethod()
    {
        if (_desktopVideoSource == 0 ||
            _preferredMonitorCaptureMethod != MonitorCaptureMethodWgc)
        {
            return;
        }

        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_int(
                settings,
                "method",
                MonitorCaptureMethodWgc);
            ObsNative.obs_source_update(_desktopVideoSource, settings);
            Log.Write("Desktop capture backend switch requested: WGC.");
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

    /// <summary>Updates source gain on Captail's serialized OBS thread.</summary>
    public float UpdateMicrophoneGain()
    {
        ObjectDisposedException.ThrowIf(_disposing, this);
        if (_microphoneSource == 0)
            return 0;
        float volume = NormalizeVolume(_config.MicrophoneVolume) *
                       DecibelsToLinear(_config.MicrophoneBoostDb);
        ObsNative.obs_source_set_volume(_microphoneSource, volume);
        Log.Write($"Microphone gain updated: {_config.MicrophoneBoostDb} dB.");
        return ObsNative.obs_source_get_volume(_microphoneSource);
    }

    internal ProcessAudioReconcileResult ReconcileProcessAudio(
        ProcessSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposing, this);
        if (!_obsStarted)
            throw new InvalidOperationException("The OBS pipeline is not initialized.");

        _processAudioReconciler ??= new ProcessAudioReconciler(
            CreateProcessAudioSource,
            DestroyProcessAudioSource,
            Log.Write,
            ReadProcessAudioStatus);
        IReadOnlyList<ProcessAudioTarget> targets = BuildProcessAudioTargets(
            _config, _activeGameExecutable, ProcessAudioAvailability);
        if (UsesSeparateGameAudio(_config))
        {
            // Use the same independent root for inclusion and exclusion. Multiple
            // same-name instances must never duplicate audio between tracks.
            ProcessIdentity? identity = snapshot.SelectIndependentRoots(
                    targets.Select(target => target.Executable))
                .OrderBy(node => node.Identity.CreationTime)
                .ThenBy(node => node.Identity.ProcessId)
                .Select(node => (ProcessIdentity?)node.Identity)
                .FirstOrDefault();
            IReadOnlyList<ProcessAudioTarget>? originalStreams = _separateAudioRouting.Build(
                snapshot, identity, checked((uint)Environment.ProcessId));
            if (originalStreams is not null)
            {
                if (_separateSystemSource != 0)
                {
                    DestroyProcessAudioSource(_separateSystemSource);
                    _separateSystemSource = 0;
                    _separateSystemIdentity = null;
                }
                targets = originalStreams;
                if (!_separateMixerProtection)
                {
                    _separateMixerProtection = true;
                    Log.Write("Separate audio mixer protection enabled: original application streams; Voicemeeter re-rendering excluded.");
                }
                if (!_separateMixerSystemSoundsLogged && snapshot.AudioEnvironment?.Sessions.Any(
                        session => session.IsSystemSounds) == true)
                {
                    _separateMixerSystemSoundsLogged = true;
                    Log.Write("Separate audio mixer protection: ownerless Windows System Sounds sessions cannot be discovered as application routes; their capture is not guaranteed.");
                }
            }
            else
            {
                ReconcileSeparateSystemAudio(identity);
                targets = identity is null ? [] : targets.Select(target => target with { Identity = identity }).ToArray();
            }
        }
        ProcessAudioReconcileResult result = _processAudioReconciler.Reconcile(snapshot, targets);
        if (_separateSystemSource != 0)
        {
            ProcessAudioSourceStatus status = ReadProcessAudioStatus(_separateSystemSource);
            bool failed = status.State is ProcessAudioSourceState.ActivationFailed or ProcessAudioSourceState.CaptureFailed;
            result = result with
            {
                DesiredSources = result.DesiredSources + 1,
                ActiveSources = result.ActiveSources + 1,
                RuntimeFailedSources = result.RuntimeFailedSources + (failed ? 1 : 0),
                LastErrorCode = failed ? status.ErrorCode : result.LastErrorCode,
            };
        }
        return result;
    }

    private void ReconcileSeparateSystemAudio(ProcessIdentity? gameIdentity)
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        ProcessIdentity identity = gameIdentity ?? new ProcessIdentity(
            checked((uint)current.Id), current.StartTime.ToFileTimeUtc());
        if (_separateSystemSource != 0 && _separateSystemIdentity == identity &&
            ReadProcessAudioStatus(_separateSystemSource).State is not
                (ProcessAudioSourceState.TargetExited or ProcessAudioSourceState.Stopped))
            return;
        if (_separateSystemSource != 0)
        {
            DestroyProcessAudioSource(_separateSystemSource);
            _separateSystemSource = 0;
        }
        _separateSystemIdentity = null;
        _separateSystemSource = CreateProcessAudioSource(identity, 1, excludeTarget: true);
        if (_separateSystemSource == 0)
            throw new InvalidOperationException("Could not create separate system audio capture.");
        _separateSystemIdentity = identity;
    }

    private nint CreateProcessAudioSource(ProcessIdentity identity, int track) =>
        CreateProcessAudioSource(identity, track, excludeTarget: false);

    private nint CreateProcessAudioSource(ProcessIdentity identity, int track, bool excludeTarget)
    {
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_int(settings, "target_pid", identity.ProcessId);
            ObsNative.obs_data_set_bool(settings, "exclude_target", excludeTarget);
            ObsNative.obs_data_set_int(
                settings,
                "target_creation_time",
                identity.CreationTime);
            nint source = ObsNative.obs_source_create(
                "captail_process_audio_capture",
                $"Captail Process Audio {++_processAudioSourceNumber}",
                settings,
                0);
            if (source == 0)
                return 0;
            if (!TryReadProcessAudioStatus(source, out _))
            {
                ObsNative.obs_source_remove(source);
                ObsNative.obs_source_release(source);
                return 0;
            }

            ObsNative.obs_source_set_audio_mixers(source, MixerBit(track));
            if (UsesDetectedGameAudio(_config, ProcessAudioAvailability))
            {
                ObsNative.obs_source_set_volume(
                    source,
                    NormalizeVolume(_config.SystemAudioVolume));
            }
            nint sceneItem = ObsNative.obs_scene_add(_processAudioScene, source);
            if (sceneItem == 0)
            {
                ObsNative.obs_source_remove(source);
                ObsNative.obs_source_release(source);
                return 0;
            }
            _processAudioSceneItems.Add(source, sceneItem);
            return source;
        }
        finally
        {
            ObsNative.obs_data_release(settings);
        }
    }

    private void DestroyProcessAudioSource(nint source)
    {
        if (_processAudioSceneItems.Remove(source, out nint sceneItem))
            ObsNative.obs_sceneitem_remove(sceneItem);
        ObsNative.obs_source_remove(source);
        ObsNative.obs_source_release(source);
    }

    private static ProcessAudioSourceStatus ReadProcessAudioStatus(nint source)
    {
        if (!TryReadProcessAudioStatus(source, out ProcessAudioSourceStatus status))
        {
            throw new InvalidOperationException(
                "Could not read process audio source status.");
        }
        return status;
    }

    private nint CreateStretchedGameOutput(nint gameSource)
    {
        return CreateStretchedOutput(gameSource, "Captail Stretched Game Capture",
            out _gameVideoScene, out _);
    }

    private nint CreateStretchedOutput(nint source, string name, out nint scene, out nint sceneItem)
    {
        // Public scenes remain owned by the main canvas until after module unload.
        scene = ObsNative.obs_scene_create_private(name);
        if (scene == 0)
        {
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VideoSourceFailed"));
        }

        sceneItem = ObsNative.obs_scene_add(scene, source);
        if (sceneItem == 0)
        {
            throw new InvalidOperationException(
                Localization.Text("L.Engine.VideoSourceFailed"));
        }

        SetCanvasBounds(sceneItem);
        return ObsNative.obs_scene_get_source(scene);
    }

    private void SetCanvasBounds(nint sceneItem)
    {
        ObsNative.obs_sceneitem_set_bounds_type(
            sceneItem,
            ObsNative.BoundsType.Stretch);
        var bounds = new ObsNative.Vec2
        {
            X = _baseWidth,
            Y = _baseHeight,
        };
        ObsNative.obs_sceneitem_set_bounds(sceneItem, ref bounds);
    }

    private static bool TryReadProcessAudioStatus(
        nint source,
        out ProcessAudioSourceStatus status)
    {
        status = default;
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
            if (!ObsNative.proc_handler_call(handler, "get_status", ref callData) ||
                !ObsNative.calldata_get_data(
                    ref callData,
                    "state",
                    out long state,
                    sizeof(long)) ||
                !ObsNative.calldata_get_data(
                    ref callData,
                    "error_code",
                    out long errorCode,
                    sizeof(long)) ||
                !Enum.IsDefined((ProcessAudioSourceState)state))
            {
                return false;
            }
            status = new ProcessAudioSourceStatus(
                (ProcessAudioSourceState)state,
                errorCode);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(stack);
        }
    }

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
                string encoderName = BuildAudioTrackName(_config, index + 1);
                nint encoder = ObsNative.obs_audio_encoder_create(
                    audioEncoderId,
                    encoderName,
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
            _config.EncoderTuning.EffectiveKeyframeInterval(_config.MaxReplaySizeMb,
                _config.DiskReplayBufferEnabled && !IsContinuousRecording));
        bool constantQuality = _config.EncoderTuning.RateControl == "cqp";
        ObsNative.obs_data_set_string(settings, "rate_control", constantQuality ? "CQP" : "CBR");
        if (constantQuality)
        {
            int quality = _config.EncoderTuning.ConstantQuality;
            ObsNative.obs_data_set_int(settings, "cqp", quality);
            if (encoder.Family == "qsv")
            {
                ObsNative.obs_data_set_int(settings, "qpi", quality);
                ObsNative.obs_data_set_int(settings, "qpp", quality);
                ObsNative.obs_data_set_int(settings, "qpb", quality);
            }
        }

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
        ApplyEncoderOverrides(settings, encoder);
    }

    private void ApplyEncoderOverrides(nint settings, CodecCapability encoder)
    {
        EncoderTuningOptions tuning = _config.EncoderTuning;
        string family = encoder.Family;
        if (family == "nvenc")
        {
            if (tuning.NvencPreset != "auto")
                ObsNative.obs_data_set_string(settings, "preset", tuning.NvencPreset);
            ObsNative.obs_data_set_string(settings, "multipass", tuning.NvencMultipass);
            ObsNative.obs_data_set_bool(settings, "lookahead", tuning.NvencLookAhead);
            if (tuning.NvencAdaptiveQuantization != "auto")
                ObsNative.obs_data_set_bool(settings, "adaptive_quantization", tuning.NvencAdaptiveQuantization == "on");
        }
        else if (family == "amf" && tuning.AmfPreset != "auto")
        {
            ObsNative.obs_data_set_string(settings, "preset", tuning.AmfPreset);
        }
        else if (family == "qsv" && tuning.QsvTargetUsage != "auto")
        {
            ObsNative.obs_data_set_string(settings, "target_usage", tuning.QsvTargetUsage.ToUpperInvariant());
        }

        if (tuning.BFrames >= 0)
            ObsNative.obs_data_set_int(settings, family == "qsv" ? "bframes" : "bf",
                tuning.EffectiveBFrames(family, encoder.Codec, 0));
        Log.Write($"Encoder tuning: family={family}, rate={tuning.RateControl}, " +
            $"quality={tuning.ConstantQuality}, keyint={tuning.EffectiveKeyframeInterval(_config.MaxReplaySizeMb, _config.DiskReplayBufferEnabled && !IsContinuousRecording)}, " +
            $"preset={tuning.NvencPreset}/{tuning.AmfPreset}/{tuning.QsvTargetUsage}, bframes={tuning.BFrames}.");
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

    private void ResumeOrStartGameReplayBuffer()
    {
        if (IsContinuousRecording)
            return;
        if (_output == 0)
            throw new InvalidOperationException(
                Localization.Text("L.Engine.BufferUnavailable"));

        if (ObsNative.obs_output_active(_output))
        {
            if (_gameOutputPaused)
            {
                if (!ObsNative.obs_output_pause(_output, false))
                    throw new InvalidOperationException(
                        Localization.Text("L.Engine.BufferStartFailed"));
                _gameOutputPaused = false;
                _previousFrameCheckUtc = default;
                Log.Write("Game Capture found; Replay Buffer resumed.");
            }
            return;
        }

        ResetObsVideo(_config.FrameRate);
        PrepareDiskReplayWindow();
        if (!ObsNative.obs_output_start(_output))
        {
            string error = PtrToString(ObsNative.obs_output_get_last_error(_output));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? Localization.Text("L.Engine.BufferStartFailed")
                    : error);
        }

        _gameOutputPaused = false;
        _replayWindowStartedUtc = DateTime.UtcNow;
        _previousFrameCheckUtc = default;
        Log.Write("Game Capture found; Replay Buffer started.");
    }

    private void SuspendOrReleaseGameReplayBuffer()
    {
        if (IsContinuousRecording)
            return;
        if (_output == 0 || !ObsNative.obs_output_active(_output))
        {
            _gameHookLostUtc = default;
            _gameOutputPaused = false;
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_gameHookLostUtc == default)
            _gameHookLostUtc = now;

        if (!_gameOutputPaused)
        {
            if (ObsNative.obs_output_can_pause(_output) &&
                ObsNative.obs_output_pause(_output, true))
            {
                _gameOutputPaused = true;
                Log.Write(
                    "Game Capture lost; Replay Buffer paused for recovery grace.");
            }
            else
            {
                Log.Write(
                    "Game Capture lost; Replay Buffer pause unavailable.");
            }
        }

        if (now - _gameHookLostUtc <
            TimeSpan.FromSeconds(GameCaptureIdleReleaseSeconds))
        {
            return;
        }

        StopGameReplayBufferForIdle();
    }

    private void StopGameReplayBufferForIdle()
    {
        if (_output == 0 || !ObsNative.obs_output_active(_output))
            return;

        _idleOutputTransition = true;
        try
        {
            ObsNative.obs_output_stop(_output);
            for (int attempt = 0;
                 attempt < 20 && ObsNative.obs_output_active(_output);
                 attempt++)
            {
                Thread.Sleep(25);
            }
            if (ObsNative.obs_output_active(_output))
            {
                ObsNative.obs_output_force_stop(_output);
                for (int attempt = 0;
                     attempt < 20 && ObsNative.obs_output_active(_output);
                     attempt++)
                {
                    Thread.Sleep(25);
                }
            }
        }
        finally
        {
            _idleOutputTransition = false;
            _gameOutputPaused = false;
            _gameHookLostUtc = default;
            _replayWindowStartedUtc = default;
        }

        if (ObsNative.obs_output_active(_output))
            throw new InvalidOperationException(
                Localization.Text("L.Engine.BufferUnexpectedStop"));

        _diskReplayBuffer?.EndWindow();
        ResetObsVideo(GameCaptureIdleFrameRate);
        if (!ShouldWakeGameCapture(_readForeground()))
            SetGameCaptureDetectorShowing(false);
        Log.Write("Game Capture idle; Replay Buffer released.");
    }

    private void SetGameCaptureDetectorShowing(bool showing)
    {
        if (_gameVideoSource == 0 || _gameCaptureDetectorShowing == showing)
            return;

        if (showing)
        {
            ObsNative.obs_source_inc_showing(_gameVideoSource);
            _gameCaptureDetectorActivatedUtc = DateTime.UtcNow;
        }
        else
        {
            ObsNative.obs_source_dec_showing(_gameVideoSource);
            _gameCaptureDetectorActivatedUtc = default;
        }

        _gameCaptureDetectorShowing = showing;
    }

    private void CreateOutput()
    {
        if (IsContinuousRecording)
            CreateRecordingOutput();
        else if (_config.DiskReplayBufferEnabled)
            CreateDiskReplayBuffer();
        else
            CreateReplayBuffer();
    }

    private void CreateDiskReplayBuffer()
    {
        _diskReplayBuffer = new DiskReplayBuffer(_config);
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(settings, "path", _diskReplayBuffer.BeginWindow());
            ObsNative.obs_data_set_string(settings, "directory", _diskReplayBuffer.DirectoryPath);
            ObsNative.obs_data_set_string(settings, "format", _diskReplayBuffer.FileFormat);
            ObsNative.obs_data_set_string(settings, "extension", _diskReplayBuffer.Extension);
            ObsNative.obs_data_set_bool(settings, "split_file", true);
            ObsNative.obs_data_set_bool(settings, "allow_overwrite", false);
            ObsNative.obs_data_set_bool(settings, "allow_spaces", false);
            ObsNative.obs_data_set_int(settings, "max_time_sec", DiskReplayBuffer.SegmentSeconds);
            ObsNative.obs_data_set_int(settings, "max_size_mb", 0);
            _output = ObsNative.obs_output_create("ffmpeg_muxer", "Captail Disk Replay Buffer", settings, 0);
        }
        finally { ObsNative.obs_data_release(settings); }
        if (_output == 0)
            throw new InvalidOperationException(Localization.Text("L.Engine.BufferUnavailable"));
        ConfigureOutputEncodersAndSignals(connectSavedSignal: false);
        ObsNative.signal_handler_connect(_outputSignals, "file_changed", _fileChangedCallback, 0);
        if (IsGameCapture)
        {
            _replayWindowStartedUtc = default;
            Log.Write("Game Capture idle; disk replay buffer sleeping.");
            return;
        }
        if (!ObsNative.obs_output_start(_output))
        {
            string error = PtrToString(ObsNative.obs_output_get_last_error(_output));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? Localization.Text("L.Engine.BufferStartFailed") : error);
        }
        _replayWindowStartedUtc = DateTime.UtcNow;
        Log.Write($"Disk replay buffer started: {_diskReplayBuffer.DirectoryPath}");
    }

    private void PrepareDiskReplayWindow()
    {
        if (_diskReplayBuffer is null)
            return;
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(settings, "path", _diskReplayBuffer.BeginWindow());
            ObsNative.obs_output_update(_output, settings);
        }
        finally { ObsNative.obs_data_release(settings); }
    }

    private void OnDiskReplayFileChanged(nint data, nint callData)
    {
        try
        {
            ObsNative.CallData values = Marshal.PtrToStructure<ObsNative.CallData>(callData);
            if (ObsNative.calldata_get_string(ref values, "next_file", out nint path))
                _diskReplayBuffer?.FileChanged(PtrToString(path),
                    (double)EncodedFrameCount / _config.FrameRate);
        }
        catch (Exception exception)
        {
            Log.Write($"Disk replay segment callback failed: {exception.Message}");
            _diskReplayBuffer?.OutputStopped();
        }
    }

    private void CreateRecordingOutput()
    {
        string captureDirectory = _config.OutputDirectory;
        Directory.CreateDirectory(captureDirectory);
        bool opus = string.Equals(
            _config.AudioCodec,
            "opus",
            StringComparison.OrdinalIgnoreCase);
        string extension = opus ? "mkv" : "mp4";
        RecordingPath = Path.Combine(
            captureDirectory,
            $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{extension}");

        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(settings, "path", RecordingPath);
            if (!opus)
            {
                ObsNative.obs_data_set_string(
                    settings,
                    "muxer_settings",
                    "movflags=frag_keyframe+empty_moov+delay_moov");
            }
            _output = ObsNative.obs_output_create(
                "ffmpeg_muxer",
                "Captail Recording",
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

        ConfigureOutputEncodersAndSignals(connectSavedSignal: false);
        if (!ObsNative.obs_output_start(_output))
        {
            string error = PtrToString(
                ObsNative.obs_output_get_last_error(_output));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? Localization.Text("L.Engine.BufferStartFailed")
                    : error);
        }
        Log.Write($"Continuous recording started: {RecordingPath}");
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

        ConfigureOutputEncodersAndSignals(connectSavedSignal: true);

        if (IsGameCapture)
        {
            _replayWindowStartedUtc = default;
            Log.Write(
                "Game Capture idle; hook detector active, Replay Buffer sleeping.");
            return;
        }

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

    private void ConfigureOutputEncodersAndSignals(bool connectSavedSignal)
    {
        ObsNative.obs_output_set_video_encoder(_output, _videoEncoder);
        for (int index = 0; index < _audioEncoders.Count; index++)
        {
            ObsNative.obs_output_set_audio_encoder(
                _output,
                _audioEncoders[index],
                (nuint)index);
        }

        _outputSignals = ObsNative.obs_output_get_signal_handler(_output);
        if (connectSavedSignal)
        {
            ObsNative.signal_handler_connect(
                _outputSignals,
                "saved",
                _savedCallback,
                0);
        }
        ObsNative.signal_handler_connect(
            _outputSignals,
            "stop",
            _stoppedCallback,
            0);

    }

    private int AudioTrackCount()
        => AudioTrackCount(_config);

    internal static uint MixerBit(int track)
    {
        if (track is not (>= 1 and <= 6))
            throw new ArgumentOutOfRangeException(nameof(track));
        return 1u << (track - 1);
    }

    internal static int AudioTrackCount(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.Equals(
                config.AudioRoutingMode,
                "advanced",
                StringComparison.OrdinalIgnoreCase))
        {
            int highest = config.ProcessAudioRoutes.Count == 0
                ? 1
                : config.ProcessAudioRoutes.Max(route => route.Track);
            if (config.CaptureMicrophone)
                highest = Math.Max(highest, config.AdvancedMicrophoneTrack);
            return highest;
        }

        int enabled = (config.CaptureSystemAudio ? 1 : 0) +
                      (config.CaptureMicrophone ? 1 : 0);
        if (enabled == 0)
            return 1;
        if (UsesSeparateGameAudio(config))
            return config.CaptureMicrophone ? 3 : 2;
        return 1;
    }

    internal static bool UsesSeparateGameAudio(Config config) =>
        config.SeparateAudioTracks && config.CaptureSystemAudio &&
        !string.Equals(config.AudioRoutingMode, "advanced", StringComparison.OrdinalIgnoreCase);

    internal static bool UsesDetectedGameAudio(
        Config config,
        AdvancedProcessAudioAvailability processAudioAvailability)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.CaptureSystemAudio &&
               (UsesSeparateGameAudio(config) || string.Equals(
                   config.CaptureSource,
                   "game",
                   StringComparison.OrdinalIgnoreCase)) &&
               !string.Equals(
                   config.AudioRoutingMode,
                   "advanced",
                   StringComparison.OrdinalIgnoreCase) &&
               processAudioAvailability ==
                   AdvancedProcessAudioAvailability.Available;
    }

    internal static bool ShouldMonitorProcessAudio(
        Config config,
        AdvancedProcessAudioAvailability processAudioAvailability)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (string.Equals(
                    config.AudioRoutingMode,
                    "advanced",
                    StringComparison.OrdinalIgnoreCase) &&
                config.ProcessAudioRoutes.Any(route => route.Enabled)) ||
               UsesDetectedGameAudio(config, processAudioAvailability);
    }

    internal static IReadOnlyList<ProcessAudioTarget> BuildProcessAudioTargets(
        Config config,
        string activeGameExecutable,
        AdvancedProcessAudioAvailability processAudioAvailability)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.Equals(
                config.AudioRoutingMode,
                "advanced",
                StringComparison.OrdinalIgnoreCase))
        {
            return config.ProcessAudioRoutes
                .Where(route => route.Enabled)
                .Select(route => new ProcessAudioTarget(
                    route.Executable,
                    route.Track))
                .ToArray();
        }

        if (!UsesDetectedGameAudio(config, processAudioAvailability))
            return [];

        string executable = Config.NormalizeExecutableName(activeGameExecutable);
        return executable.Length == 0
            ? []
            : [new ProcessAudioTarget(executable, UsesSeparateGameAudio(config) ? 2 : 1)];
    }

    internal static string BuildAudioTrackName(Config config, int track)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (track is not (>= 1 and <= 6))
            throw new ArgumentOutOfRangeException(nameof(track));

        if (string.Equals(
                config.AudioRoutingMode,
                "advanced",
                StringComparison.OrdinalIgnoreCase))
        {
            string[] applications = config.ProcessAudioRoutes
                .Where(route => route.Enabled && route.Track == track)
                .Select(route => Path.GetFileNameWithoutExtension(route.Executable))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sources = new List<string>(applications);
            if (config.CaptureMicrophone &&
                config.AdvancedMicrophoneTrack == track)
            {
                sources.Add("Microphone");
            }

            string suffix = sources.Count == 0
                ? ""
                : $" - {string.Join(" + ", sources)}";
            string name = $"Track {track}{suffix}";
            return name.Length <= 96 ? name : name[..96];
        }

        if (UsesSeparateGameAudio(config))
        {
            return track switch { 1 => "System", 2 => "Game", _ => "Microphone" };
        }
        if (config.CaptureSystemAudio && !config.CaptureMicrophone)
            return "System / Game";
        if (config.CaptureMicrophone && !config.CaptureSystemAudio)
            return "Microphone";
        return "Mixed audio";
    }

    internal static AdvancedProcessAudioAvailability DetectProcessAudioAvailability(
        Version windowsVersion,
        bool sourceRegistered)
    {
        ArgumentNullException.ThrowIfNull(windowsVersion);
        if (windowsVersion.Major < 10 ||
            windowsVersion.Major == 10 && windowsVersion.Build < ProcessLoopbackMinimumBuild)
        {
            return AdvancedProcessAudioAvailability.UnsupportedWindowsVersion;
        }
        return sourceRegistered
            ? AdvancedProcessAudioAvailability.Available
            : AdvancedProcessAudioAvailability.SourceUnavailable;
    }

    internal static bool IsAudioRoutingAvailable(
        Config config,
        AdvancedProcessAudioAvailability processAudioAvailability)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (!UsesSeparateGameAudio(config) && !string.Equals(
                   config.AudioRoutingMode,
                   "advanced",
                   StringComparison.OrdinalIgnoreCase)) ||
               processAudioAvailability == AdvancedProcessAudioAvailability.Available;
    }

    private bool IsAdvancedAudioRouting => string.Equals(
        _config.AudioRoutingMode,
        "advanced",
        StringComparison.OrdinalIgnoreCase);

    private static bool HasInputType(string expected)
    {
        for (nuint index = 0;
             ObsNative.obs_enum_input_types(index, out nint id);
             index++)
        {
            if (string.Equals(PtrToString(id), expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void OnReplaySaved(nint _, nint __)
    {
        string path = ReadStringProcedure(
            ObsNative.obs_output_get_proc_handler(_output),
            "get_last_replay",
            "path");
        lock (_saveGate)
        {
            Log.Event(DiagnosticLevel.Info, "Replay", "replay.saved-callback",
                new { path, pending = _pendingSave is not null }, _saveOperationId);
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
        if (_disposing || _resettingReplayWindow || _idleOutputTransition)
        {
            Log.Event(DiagnosticLevel.Info, "Capture", "capture.output-stopped", new
            {
                expected = true, _disposing, _resettingReplayWindow, _idleOutputTransition,
            });
            return;
        }
        _diskReplayBuffer?.OutputStopped();
        string error = PtrToString(ObsNative.obs_output_get_last_error(_output));
        Log.Event(DiagnosticLevel.Error, "Capture", "capture.output-stopped", new
        {
            expected = false, nativeError = error, state = CachedDiagnosticState,
        }, _saveOperationId);
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
            ObsNative.signal_handler_disconnect(_outputSignals, "file_changed", _fileChangedCallback, 0);
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
        _diskReplayBuffer?.Dispose();
        _diskReplayBuffer = null;
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
        if (_gameCaptureDetectorShowing && _gameVideoSource != 0)
        {
            ObsNative.obs_source_dec_showing(_gameVideoSource);
            _gameCaptureDetectorShowing = false;
        }

        _processAudioReconciler?.Dispose();
        _processAudioReconciler = null;
        if (_separateSystemSource != 0)
            DestroyProcessAudioSource(_separateSystemSource);
        _separateSystemSource = 0;
        _separateSystemIdentity = null;
        _processAudioSceneItems.Clear();

        if (_desktopVideoScene != 0)
        {
            ObsNative.obs_scene_release(_desktopVideoScene);
            _desktopVideoScene = 0;
            _desktopVideoSceneItem = 0;
            _desktopVideoOutputSource = 0;
        }
        if (_gameVideoScene != 0)
        {
            ObsNative.obs_scene_release(_gameVideoScene);
            _gameVideoScene = 0;
            _gameVideoOutputSource = 0;
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
        _microphoneSource = 0;
        if (_processAudioScene != 0)
        {
            ObsNative.obs_scene_release(_processAudioScene);
            _processAudioScene = 0;
        }

        nint windowsCaptureModule = ObsNative.obs_get_module("win-capture");
        nint windowsCaptureLibrary = windowsCaptureModule == 0
            ? 0 : ObsNative.obs_get_module_lib(windowsCaptureModule);
        ObsNative.obs_wait_for_destroy_queue();
        ObsNative.obs_shutdown();
        _obsStarted = false;
        // libobs intentionally retains module DLLs. win-capture's one-shot hook
        // initialization join guard otherwise survives the next module load,
        // allowing its new worker to outlive the OBS context. All sources and
        // the first worker are drained before releasing libobs's DLL reference.
        if (windowsCaptureLibrary != 0)
            NativeLibrary.Free(windowsCaptureLibrary);
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
    string? GameExecutable)
{
    internal Task? SnapshotReady { get; init; }
}
