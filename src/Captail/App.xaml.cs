using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using H.NotifyIcon;

namespace Captail;

public partial class App : Application
{
    private Config? _config;
    private ObsReplayEngine? _obs;
    private HotkeyManager? _hotkeys;
    private string _boundHotkey = "";
    private string _boundToggleHotkey = "";
    private TaskbarIcon? _tray;
    private MenuItem? _saveMenuItem;
    private MenuItem? _toggleMenuItem;
    private MenuItem? _openFolderMenuItem;
    private MenuItem? _settingsMenuItem;
    private MenuItem? _exitMenuItem;
    private SettingsWindow? _settingsWindow;
    private bool _uiOnly;
    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _activationServerCts;
    private string _activationPipeName = "";
    private string? _pendingUiError;
    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _captureStateTimer;
    private DateTime _pipelineStartedUtc;
    private DateTime _nextRecoveryUtc;
    private int _recoveryFailures;
    private int _recoveryInProgress;
    private int _captureStateRefreshInProgress;
    private OverlayNotificationWindow? _overlayNotification;
    private readonly UpdateService _updateService = new();
    private DispatcherTimer? _updateShutdownTimer;
    private int _saving;
    private EncoderCapabilities? _capabilities;
    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private readonly SingleThreadTaskScheduler _obsTaskScheduler =
        new("Captail OBS");
    private volatile bool _replayRunning;
    private string? _captureDescription;
    private int _exiting;
    private bool _shutdownExistingSucceeded = true;
#if DEBUG
    private bool _qaUpdateAvailable;
#endif

    private bool IsReplayRunning => _replayRunning;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _uiOnly = e.Args.Contains("--ui-only", StringComparer.OrdinalIgnoreCase);
#if DEBUG
            bool faultTest = e.Args.Contains(
                "--qa-fault-recovery",
                StringComparer.OrdinalIgnoreCase);
            bool codecTest = e.Args.Contains(
                "--qa-codecs",
                StringComparer.OrdinalIgnoreCase);
            bool capabilityModelTest = e.Args.Contains(
                "--qa-capability-model",
                StringComparer.OrdinalIgnoreCase);
            bool gameCaptureTest = e.Args.Any(
                argument => argument.StartsWith(
                    "--qa-game-capture=",
                    StringComparison.OrdinalIgnoreCase));
            bool replaySegmentsTest = e.Args.Contains(
                "--qa-replay-segments",
                StringComparer.OrdinalIgnoreCase);
            bool fileRetryTest = e.Args.Contains(
                "--qa-file-retry",
                StringComparer.OrdinalIgnoreCase);
            bool automaticCapturePolicyTest = e.Args.Contains(
                "--qa-auto-capture-policy",
                StringComparer.OrdinalIgnoreCase);
            bool updateCheckTest = e.Args.Contains(
                "--qa-update-check",
                StringComparer.OrdinalIgnoreCase);
            string? clipEditorTestPath = e.Args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-clip-editor=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-clip-editor=".Length..];
            bool clipEditorTest = !string.IsNullOrWhiteSpace(clipEditorTestPath);
            string? audioMixTestPath = e.Args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-audio-mix=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-audio-mix=".Length..];
            bool audioMixTest = !string.IsNullOrWhiteSpace(audioMixTestPath);
            string? previewGeometryTestPath = e.Args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-preview-geometry=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-preview-geometry=".Length..];
            bool previewGeometryTest =
                !string.IsNullOrWhiteSpace(previewGeometryTestPath);
            string? trimOverwriteTestPath = e.Args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-trim-overwrite=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-trim-overwrite=".Length..];
            bool trimOverwriteTest =
                !string.IsNullOrWhiteSpace(trimOverwriteTestPath);
            _qaUpdateAvailable = e.Args.Contains(
                "--qa-update-available",
                StringComparer.OrdinalIgnoreCase);
#else
            const bool faultTest = false;
            const bool codecTest = false;
            const bool capabilityModelTest = false;
            const bool gameCaptureTest = false;
            const bool replaySegmentsTest = false;
            const bool fileRetryTest = false;
            const bool automaticCapturePolicyTest = false;
            const bool updateCheckTest = false;
            const bool clipEditorTest = false;
            const bool audioMixTest = false;
            const bool previewGeometryTest = false;
            const bool trimOverwriteTest = false;
#endif
            bool backgroundLaunch = e.Args.Contains(
                    "--background",
                    StringComparer.OrdinalIgnoreCase) ||
                AppDistribution.IsStartupTaskActivation();
            bool shutdownExisting = e.Args.Contains(
                "--shutdown-existing",
                StringComparer.OrdinalIgnoreCase);
            if (!AcquireSingleInstance(
                    backgroundLaunch,
                    shutdownExisting,
                    _uiOnly || faultTest || codecTest || capabilityModelTest ||
                    gameCaptureTest || replaySegmentsTest || updateCheckTest ||
                    clipEditorTest || audioMixTest || previewGeometryTest ||
                    fileRetryTest || trimOverwriteTest ||
                    automaticCapturePolicyTest))
            {
                Shutdown();
                return;
            }
            if (shutdownExisting)
            {
                Shutdown(_shutdownExistingSucceeded ? 0 : 12);
                return;
            }

            _config = Config.Load();
            Localization.SetLanguage(_config.Language);
            Localization.Changed += OnLanguageChanged;
#if !DEBUG
            if (!_uiOnly && Autostart.HasEntry())
            {
                try
                {
                    await Autostart.SetEnabledAsync(true);
                }
                catch (Exception exception)
                {
                    Log.Write(
                        $"Autostart migration failed: {exception.Message}");
                }
            }
#endif
#if DEBUG
            if (faultTest)
            {
                RunFaultRecoveryTest();
                return;
            }
            if (codecTest)
            {
                RunCodecTest(e.Args);
                return;
            }
            if (capabilityModelTest)
            {
                RunCapabilityModelTest();
                return;
            }
            if (gameCaptureTest)
            {
                RunGameCaptureTest(e.Args);
                return;
            }
            if (replaySegmentsTest)
            {
                RunReplaySegmentsTest();
                return;
            }
            if (fileRetryTest)
            {
                await RunFileRetryTestAsync();
                return;
            }
            if (automaticCapturePolicyTest)
            {
                RunAutomaticCapturePolicyTest();
                return;
            }
            if (updateCheckTest)
            {
                await _updateService.CheckAsync(
                    force: true,
                    CancellationToken.None);
                Shutdown(0);
                return;
            }
            if (clipEditorTest)
            {
                await RunClipEditorTestAsync(clipEditorTestPath!);
                return;
            }
            if (audioMixTest)
            {
                await RunAudioMixTestAsync(audioMixTestPath!);
                return;
            }
            if (previewGeometryTest)
            {
                await RunPreviewGeometryTestAsync(previewGeometryTestPath!);
                return;
            }
            if (trimOverwriteTest)
            {
                await RunTrimOverwriteTestAsync(trimOverwriteTestPath!);
                return;
            }
#endif
            if (_uiOnly)
            {
                StartActivationServer();
                OpenSettings();
#if DEBUG
                if (e.Args.Contains("--qa-recovery", StringComparer.OrdinalIgnoreCase))
                {
                    _config.ReplayEnabled = true;
                    _settingsWindow?.UpdateRecoveryState(
                        Localization.Format("L.Notify.RetryIn", 5));
                    _settingsWindow?.ShowError(
                        Localization.Text("L.Notify.RecoveryFailedTitle"),
                        Localization.Format("L.Notify.DriverUnavailable", 5));
                }
                if (e.Args.Contains("--qa-overlay", StringComparer.OrdinalIgnoreCase))
                {
                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        () => ShowOverlayNotification(
                            "✓",
                            Localization.Text("L.Notify.RecoveredTitle"),
                            Localization.Text("L.Notify.RecoveredDetail"),
                            OverlayTone.Success,
                            60_000));
                }
#endif
                return;
            }

            CreateTrayIcon();
            BindHotkeyAtStartup();
            StartHealthMonitor();
            StartCaptureStateMonitor();
            StartActivationServer();
            if (!backgroundLaunch)
                OpenSettings();

            if (_config.ReplayEnabled &&
                await TryStartPipelineAsync(showError: true))
            {
                ShowOverlayNotification(
                    "●",
                    Localization.Text("L.Notify.ReadyTitle"),
                    Localization.Format(
                        "L.Status.BufferLast",
                        FormatDuration(_config.BufferSeconds)),
                    OverlayTone.Success);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Localization.Format("L.App.StartError", exception.Message),
                Localization.Text("L.Brand"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

#if DEBUG
    private void RunAutomaticCapturePolicyTest()
    {
        string[] rejected =
        [
            "Telegram.exe", @"C:\Apps\Telegram.exe", "Discord.exe", "chrome.exe", "msedge.exe",
            "firefox.exe", "explorer.exe", "dwm.exe", "Spotify.exe",
            "vlc.exe", "mpv.exe", "obs64.exe", "Captail.exe",
            "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "SearchHost.exe",
        ];
        string[] accepted =
        [
            "cs2.exe", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
            "GTA5.exe", "hl2.exe",
            "ExampleGame-Win64-Shipping.exe", "Minecraft.Windows.exe",
        ];
        string[] falsePositives = rejected
            .Where(ObsReplayEngine.IsAutomaticCaptureCandidate)
            .ToArray();
        string[] falseNegatives = accepted
            .Where(executable =>
                !ObsReplayEngine.IsAutomaticCaptureCandidate(executable))
            .ToArray();
        bool altTabRejected = !ObsReplayEngine.ShouldUseAutomaticGameCapture(
            "cs2.exe",
            "explorer.exe",
            hasVideo: true);
        bool focusedGameAccepted = ObsReplayEngine.ShouldUseAutomaticGameCapture(
            "cs2.exe",
            "CS2.exe",
            hasVideo: true);
        bool missingVideoRejected = !ObsReplayEngine.ShouldUseAutomaticGameCapture(
            "cs2.exe",
            "cs2.exe",
            hasVideo: false);
        bool passed = falsePositives.Length == 0 &&
                      falseNegatives.Length == 0 &&
                      !ObsReplayEngine.IsAutomaticCaptureCandidate("") &&
                      altTabRejected && focusedGameAccepted &&
                      missingVideoRejected;
        Log.Write(
            $"AUTO_CAPTURE_POLICY_TEST {(passed ? "PASS" : "FAIL")}: " +
            $"falsePositives={string.Join(',', falsePositives)}, " +
            $"falseNegatives={string.Join(',', falseNegatives)}, " +
            $"altTabRejected={altTabRejected}, " +
            $"focusedGameAccepted={focusedGameAccepted}, " +
            $"missingVideoRejected={missingVideoRejected}");
        Shutdown(passed ? 0 : 22);
    }

    private async Task RunTrimOverwriteTestAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("QA replay does not exist.", fullPath);

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"captail_trim_overwrite_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string workingPath = Path.Combine(
            directory,
            "overwrite-source" + Path.GetExtension(fullPath));
        try
        {
            File.Copy(fullPath, workingPath);
            var ffmpeg = new FfmpegAdapter();
            TimeSpan originalDuration = await ffmpeg.ReadDurationAsync(workingPath);
            IReadOnlyList<AudioTrackInfo> audioTracks =
                await ffmpeg.ReadAudioTracksAsync(workingPath);
            var file = new FileInfo(workingPath);
            var clip = new ReplayClip(
                workingPath,
                file.Name,
                null,
                file.LastWriteTime,
                file.Length,
                originalDuration,
                null);
            TimeSpan start = TimeSpan.FromMilliseconds(250);
            TimeSpan end = originalDuration - TimeSpan.FromMilliseconds(500);
            if (end <= start)
                throw new InvalidOperationException("QA replay must be longer than one second.");

            var library = new ReplayLibrary(ffmpeg);
            await library.TrimOverwriteAsync(
                directory,
                clip,
                start,
                end,
                audioTracks.Select(track => track.StreamIndex).ToArray());
            TimeSpan trimmedDuration = await ffmpeg.ReadDurationAsync(workingPath);
            string[] internalFiles = Directory.EnumerateFiles(directory)
                .Where(ReplayLibrary.IsInternalWorkingFile)
                .ToArray();
            bool passed = File.Exists(workingPath) &&
                          new FileInfo(workingPath).Length > 0 &&
                          trimmedDuration < originalDuration &&
                          internalFiles.Length == 0;
            Log.Write(
                $"TRIM_OVERWRITE_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"duration={originalDuration.TotalSeconds:0.000}->" +
                $"{trimmedDuration.TotalSeconds:0.000}, " +
                $"audioTracks={audioTracks.Count}, leftovers={internalFiles.Length}");
            Shutdown(passed ? 0 : 21);
        }
        catch (Exception exception)
        {
            Log.Write($"TRIM_OVERWRITE_TEST FAIL: {exception}");
            Shutdown(21);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                Log.Write($"Trim overwrite QA cleanup failed: {exception.Message}");
            }
        }
    }

    private async Task RunFileRetryTestAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"captail_file_retry_{Guid.NewGuid():N}");
        string source = Path.Combine(directory, "clip.tmp");
        string destination = Path.Combine(directory, "clip.mkv");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(source, "captail");
            using var locked = new ManualResetEventSlim();
            Task locker = Task.Run(() =>
            {
                using FileStream stream = File.Open(
                    source,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                locked.Set();
                Thread.Sleep(650);
            });
            locked.Wait();

            var watch = Stopwatch.StartNew();
            await FfmpegAdapter.MoveFileWithRetryAsync(source, destination);
            watch.Stop();
            await locker;

            const string legacyWorkingName =
                ".Replay.test.replacement.mkv.deadbeef.tmp.mkv";
            bool filtered = ReplayLibrary.IsInternalWorkingFile(legacyWorkingName);
            bool passed = File.Exists(destination) &&
                          watch.ElapsedMilliseconds >= 500 &&
                          filtered;
            Log.Write(
                $"FILE_RETRY_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"moved={File.Exists(destination)}, " +
                $"elapsed={watch.ElapsedMilliseconds}ms, filtered={filtered}");
            Shutdown(passed ? 0 : 20);
        }
        catch (Exception exception)
        {
            Log.Write($"FILE_RETRY_TEST FAIL: {exception}");
            Shutdown(20);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                Log.Write($"File retry QA cleanup failed: {exception.Message}");
            }
        }
    }

    private async Task RunAudioMixTestAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("QA replay does not exist.", fullPath);

        string destination = Path.Combine(
            Path.GetTempPath(),
            $"captail_audio_mix_{Guid.NewGuid():N}{Path.GetExtension(fullPath)}");
        try
        {
            var ffmpeg = new FfmpegAdapter();
            TimeSpan duration = await ffmpeg.ReadDurationAsync(fullPath);
            IReadOnlyList<AudioTrackInfo> sourceTracks =
                await ffmpeg.ReadAudioTracksAsync(fullPath);
            VideoStreamInfo? sourceVideo = await ffmpeg.ReadVideoInfoAsync(fullPath);
            if (sourceTracks.Count < 2 || sourceVideo is null)
                throw new InvalidOperationException(
                    "QA replay requires video and at least two audio tracks.");

            await ffmpeg.TrimCopyAsync(
                fullPath,
                destination,
                TimeSpan.Zero,
                duration,
                sourceTracks.Select(track => track.StreamIndex).ToArray(),
                mergeAudioTracks: true);

            IReadOnlyList<AudioTrackInfo> mixedTracks =
                await ffmpeg.ReadAudioTracksAsync(destination);
            VideoStreamInfo? mixedVideo = await ffmpeg.ReadVideoInfoAsync(destination);
            bool passed = mixedTracks.Count == 1 &&
                mixedVideo is not null &&
                mixedVideo.Codec.Equals(
                    sourceVideo.Codec,
                    StringComparison.OrdinalIgnoreCase) &&
                mixedVideo.Width == sourceVideo.Width &&
                mixedVideo.Height == sourceVideo.Height;
            Log.Write(
                $"AUDIO_MIX_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"sourceTracks={sourceTracks.Count}, mixedTracks={mixedTracks.Count}, " +
                $"video={sourceVideo.Codec}/{mixedVideo?.Codec} " +
                $"{sourceVideo.Width}x{sourceVideo.Height}/" +
                $"{mixedVideo?.Width}x{mixedVideo?.Height}");
            Shutdown(passed ? 0 : 16);
        }
        catch (Exception exception)
        {
            Log.Write($"AUDIO_MIX_TEST FAIL: {exception}");
            Shutdown(16);
        }
        finally
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
            catch (Exception exception)
            {
                Log.Write($"Audio mix QA cleanup failed: {exception.Message}");
            }
        }
    }

    private async Task RunClipEditorTestAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("QA replay does not exist.", fullPath);

        var ffmpeg = new FfmpegAdapter();
        TimeSpan duration = await ffmpeg.ReadDurationAsync(fullPath);
        var file = new FileInfo(fullPath);
        var clip = new ReplayClip(
            fullPath,
            file.Name,
            null,
            file.LastWriteTime,
            file.Length,
            duration,
            null);
        var window = new ClipEditorWindow(
            new ReplayLibrary(ffmpeg),
            file.DirectoryName!,
            clip,
            saved => Log.Write($"CLIP_EDITOR_QA saved={saved}"));
        MainWindow = window;
        window.ShowDialog();
        Shutdown(0);
    }

    private async Task RunPreviewGeometryTestAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("QA replay does not exist.", fullPath);

        var ffmpeg = new FfmpegAdapter();
        TimeSpan duration = await ffmpeg.ReadDurationAsync(fullPath);
        var file = new FileInfo(fullPath);
        var clip = new ReplayClip(
            fullPath,
            file.Name,
            null,
            file.LastWriteTime,
            file.Length,
            duration,
            null);
        var window = new ClipEditorWindow(
            new ReplayLibrary(ffmpeg),
            file.DirectoryName!,
            clip,
            _ => { });
        MainWindow = window;
        window.Show();
        try
        {
            (bool passed, string details) =
                await window.RunPreviewGeometryQaAsync();
            Log.Write(
                $"PREVIEW_GEOMETRY_TEST {(passed ? "PASS" : "FAIL")}: {details}");
            window.Close();
            Shutdown(passed ? 0 : 15);
        }
        catch (Exception exception)
        {
            Log.Write($"PREVIEW_GEOMETRY_TEST FAIL: {exception}");
            window.Close();
            Shutdown(15);
        }
    }

    private void RunCapabilityModelTest()
    {
        try
        {
            var oldNvidiaIds = new HashSet<string>(
                ["obs_nvenc_h264_tex", "obs_nvenc_hevc_tex"],
                StringComparer.OrdinalIgnoreCase);
            var oldNvidia = new EncoderCapabilities(
                "NVIDIA GeForce GTX 970",
                EncoderCatalog.Available(oldNvidiaIds, "NVIDIA GeForce GTX 970"));

            var amdIds = new HashSet<string>(
                ["h264_texture_amf", "h265_texture_amf", "av1_texture_amf",
                 "obs_qsv11_v2"],
                StringComparer.OrdinalIgnoreCase);
            var amd = new EncoderCapabilities(
                "AMD Radeon RX 7900 XTX",
                EncoderCatalog.Available(amdIds, "AMD Radeon RX 7900 XTX"));

            var intelIds = new HashSet<string>(
                ["obs_qsv11_v2", "obs_qsv11_hevc", "obs_qsv11_av1",
                 "h264_texture_amf"],
                StringComparer.OrdinalIgnoreCase);
            var intel = new EncoderCapabilities(
                "Intel Arc A770",
                EncoderCatalog.Available(intelIds, "Intel Arc A770"));
            var invalidConfig = new Config
            {
                BufferSeconds = -1,
                FrameRate = 999,
                Codec = "unknown",
                SystemAudioVolume = 500,
                Hotkey = "Ctrl+A+B",
            };
            invalidConfig.Normalize();
            Config hotkeyOnlyChange = invalidConfig.Clone();
            hotkeyOnlyChange.Hotkey = "Ctrl+Alt+F8";
            Config pipelineChange = invalidConfig.Clone();
            pipelineChange.FrameRate = 30;

            bool passed =
                oldNvidia.Supports("h264") &&
                oldNvidia.Supports("hevc") &&
                !oldNvidia.Supports("av1") &&
                oldNvidia.FallbackCodec() == "h264" &&
                amd.Preferred("av1")?.Family == "amf" &&
                amd.Preferred("h264")?.Family == "amf" &&
                intel.Preferred("av1")?.Family == "qsv" &&
                intel.Preferred("h264")?.Family == "qsv" &&
                invalidConfig.BufferSeconds == 300 &&
                invalidConfig.FrameRate == 60 &&
                invalidConfig.Codec == "h264" &&
                invalidConfig.SystemAudioVolume == 100 &&
                invalidConfig.Hotkey == "Ctrl+Shift+F10" &&
                ObsReplayEngine.RecommendedNvencBFrames("hevc", true) == 0 &&
                ObsReplayEngine.RecommendedNvencBFrames("h264", true) == 2 &&
                ObsReplayEngine.RecommendedNvencBFrames("h264", false) == 0 &&
                invalidConfig.PipelineEquals(hotkeyOnlyChange) &&
                !invalidConfig.PipelineEquals(pipelineChange);
            Log.Write(
                $"GPU_CAPABILITY_MODEL_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"oldNvidiaAv1={oldNvidia.Supports("av1")}, " +
                $"amd={amd.Preferred("av1")?.Family}, " +
                $"intel={intel.Preferred("av1")?.Family}");
            Shutdown(passed ? 0 : 11);
        }
        catch (Exception exception)
        {
            Log.Write($"GPU_CAPABILITY_MODEL_TEST FAIL: {exception}");
            Shutdown(11);
        }
    }

    private async void RunCodecTest(string[] args)
    {
        try
        {
            int frameRate = ParseQaFrameRate(args, "--qa-fps=", 30);
            string resolution = ParseQaResolution(args, "--qa-resolution=");
            int maxSizeMb = ParseQaInt(
                args,
                "--qa-max-size-mb=",
                0,
                0,
                10_000);
            bool audioTracks = args.Contains(
                "--qa-audio-tracks",
                StringComparer.OrdinalIgnoreCase);
            string audioCodec = args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-audio-codec=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-audio-codec=".Length..]
                .ToLowerInvariant() == "opus"
                ? "opus"
                : "aac";
            string? requested = args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-codec=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-codec=".Length..]
                .ToLowerInvariant();
            string[] codecs = requested is "av1" or "hevc" or "h264"
                ? [requested]
                : ["av1", "hevc", "h264"];
            bool allPassed = true;

            foreach (string codec in codecs)
            {
                string root = Path.Combine(
                    Path.GetTempPath(),
                    "Captail",
                    $"obs_{codec}_{Environment.ProcessId}");
                _config = new Config
                {
                    ReplayEnabled = true,
                    BufferSeconds = 5,
                    MaxReplaySizeMb = maxSizeMb,
                    FrameRate = frameRate,
                    RecordingResolution = resolution,
                    BitrateMbps = 0,
                    Codec = codec,
                    AudioCodec = audioCodec,
                    CaptureSource = "desktop",
                    CaptureSystemAudio = audioTracks,
                    SystemAudioVolume = audioTracks ? 37 : 100,
                    CaptureMicrophone = audioTracks,
                    MicrophoneVolume = audioTracks ? 63 : 100,
                    MicrophoneBoostDb = audioTracks ? 12 : 0,
                    SeparateAudioTracks = audioTracks,
                    OutputDirectory = root,
                };
                bool started = TryStartPipeline(showError: false);
                if (!started ||
                    !string.Equals(_obs?.ActiveCodec, codec, StringComparison.OrdinalIgnoreCase))
                {
                    allPassed = false;
                    Log.Write($"OBS_CODEC_TEST {codec}: start failed");
                    StopPipeline();
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
                bool sourceChanged = _obs!.RefreshCaptureState();
                Log.Write(
                    $"OBS_CODEC_TEST {codec}: source={_obs.Description}, " +
                    $"changed={sourceChanged}, game={_obs.ActiveGameExecutable}");
                await Task.Delay(TimeSpan.FromSeconds(4));
                string path = await _obs!.SaveReplayAsync();
                bool saved = File.Exists(path) && new FileInfo(path).Length > 0;
                allPassed &= saved;
                Log.Write(
                    $"OBS_CODEC_TEST {codec}: saved={saved}, " +
                    $"frames={_obs.EncodedFrameCount}, path={path}");
                StopPipeline();
            }

            Log.Write($"OBS_CODEC_TEST {(allPassed ? "PASS" : "FAIL")}");
            Shutdown(allPassed ? 0 : 6);
        }
        catch (Exception exception)
        {
            Log.Write($"OBS_CODEC_TEST FAIL: {exception}");
            Shutdown(6);
        }
    }

    private async void RunGameCaptureTest(string[] args)
    {
        try
        {
            string codec = args
                .FirstOrDefault(argument => argument.StartsWith(
                    "--qa-game-codec=",
                    StringComparison.OrdinalIgnoreCase))
                ?["--qa-game-codec=".Length..]
                .ToLowerInvariant() ?? "av1";
            int frameRate = ParseQaFrameRate(args, "--qa-game-fps=", 240);
            string resolution = ParseQaResolution(args, "--qa-game-resolution=");
            string root = Path.Combine(
                Path.GetTempPath(),
                "Captail",
                $"obs_game_{Environment.ProcessId}");
            _config = new Config
            {
                ReplayEnabled = true,
                BufferSeconds = 6,
                FrameRate = frameRate,
                RecordingResolution = resolution,
                BitrateMbps = 50,
                Codec = codec,
                CaptureSource = "game",
                CaptureSystemAudio = false,
                CaptureMicrophone = false,
                OutputDirectory = root,
            };
            if (!TryStartPipeline(showError: false))
                throw new InvalidOperationException("OBS Game Capture did not start.");

            DateTime hookDeadline = DateTime.UtcNow.AddSeconds(8);
            while (!_obs!.IsGameHooked && DateTime.UtcNow < hookDeadline)
                await Task.Delay(100);
            if (_obs.IsGameHooked)
                await Task.Delay(TimeSpan.FromSeconds(2));
            uint totalBefore = _obs.TotalRenderedFrames;
            uint laggedBefore = _obs.LaggedRenderedFrames;
            await Task.Delay(TimeSpan.FromSeconds(6));
            uint totalAfter = _obs.TotalRenderedFrames;
            uint laggedAfter = _obs.LaggedRenderedFrames;
            uint totalDelta = totalAfter - totalBefore;
            uint laggedDelta = laggedAfter - laggedBefore;
            double steadyLagPercent = totalDelta == 0
                ? 100
                : laggedDelta * 100d / totalDelta;
            string path = await _obs!.SaveReplayAsync();
            bool passed = File.Exists(path) &&
                          new FileInfo(path).Length > 0 &&
                          _obs.IsGameHooked &&
                          _obs.EncodedFrameCount > 0 &&
                          steadyLagPercent < 10;
            Log.Write(
                $"OBS_GAME_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"hooked={_obs.IsGameHooked}, frames={_obs.EncodedFrameCount}, " +
                $"steadyLag={laggedDelta}/{totalDelta} ({steadyLagPercent:0.0}%), " +
                $"path={path}");
            Shutdown(passed ? 0 : 8);
        }
        catch (Exception exception)
        {
            Log.Write($"OBS_GAME_TEST FAIL: {exception}");
            Shutdown(9);
        }
    }

    private async void RunFaultRecoveryTest()
    {
        try
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Captail",
                $"obs_fault_{Environment.ProcessId}");
            _config = new Config
            {
                ReplayEnabled = true,
                BufferSeconds = 5,
                FrameRate = 30,
                BitrateMbps = 8,
                Codec = "h264",
                CaptureSource = "desktop",
                CaptureSystemAudio = false,
                CaptureMicrophone = false,
                OutputDirectory = root,
            };
            StartHealthMonitor();
            if (!await TryStartPipelineAsync(showError: false))
                throw new InvalidOperationException("The initial OBS pipeline did not start.");

            bool restarted = true;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                DateTime originalStart = _pipelineStartedUtc;
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 1));
                await RecoverPipelineAsync($"QA: simulated OBS restart {attempt}.");
                restarted &= IsReplayRunning && _pipelineStartedUtc > originalStart;
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
            string path = "";
            if (restarted)
            {
                Task<string> saveOperation = await RunOnObsThreadAsync(
                    () => _obs!.SaveReplayAsync());
                path = await saveOperation;
            }
            bool passed = restarted && File.Exists(path);
            Log.Write(
                $"OBS_FAULT_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"restarted={restarted}, path={path}");
            Shutdown(passed ? 0 : 4);
        }
        catch (Exception exception)
        {
            Log.Write($"OBS_FAULT_TEST FAIL: {exception}");
            Shutdown(4);
        }
    }

    private static int ParseQaFrameRate(
        IEnumerable<string> args,
        string prefix,
        int fallback)
    {
        string? value = args.FirstOrDefault(argument => argument.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase));
        return value is not null &&
               int.TryParse(value[prefix.Length..], out int parsed) &&
               parsed is 30 or 60 or 120 or 144 or 240
            ? parsed
            : fallback;
    }

    private static string ParseQaResolution(
        IEnumerable<string> args,
        string prefix)
    {
        string? value = args.FirstOrDefault(argument => argument.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase));
        string parsed = value?[prefix.Length..].ToLowerInvariant() ?? "source";
        return parsed is "720p" or "1080p" or "1440p" or "2160p"
            ? parsed
            : "source";
    }

    private static int ParseQaInt(
        IEnumerable<string> args,
        string prefix,
        int fallback,
        int minimum,
        int maximum)
    {
        string? value = args.FirstOrDefault(argument => argument.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase));
        return value is not null &&
               int.TryParse(value[prefix.Length..], out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
#endif

    private bool AcquireSingleInstance(
        bool backgroundLaunch,
        bool shutdownExisting,
        bool isolatedUiTest)
    {
        string userId = WindowsIdentity.GetCurrent().User?.Value ??
                        Environment.UserName;
        string suffix = userId.Replace('\\', '.') +
                        (isolatedUiTest ? ".UiOnly" : "");
        string mutexName = $@"Local\Captail.SingleInstance.{suffix}";
        _activationPipeName = $"Captail.Activate.{suffix}";
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out bool createdNew);
        if (createdNew)
            return true;

        SendActivationCommand(
            shutdownExisting
                ? "EXIT"
                : backgroundLaunch ? "PING" : "SHOW");
        if (shutdownExisting)
        {
            bool acquired = false;
            try
            {
                acquired = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(50));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (acquired)
                _singleInstanceMutex.ReleaseMutex();
            _shutdownExistingSucceeded = acquired;
        }
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private void SendActivationCommand(string command)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _activationPipeName,
                    PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(command);
                return;
            }
            catch (TimeoutException)
            {
                // The first instance may still be starting.
            }
            catch (IOException)
            {
                // The pipe is recreated between attempts.
            }
        }
    }

    private void StartActivationServer()
    {
        _activationServerCts = new CancellationTokenSource();
        _ = Task.Run(() => ActivationServerLoopAsync(_activationServerCts.Token));
    }

    private async Task ActivationServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _activationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                string command = await ReadActivationCommandAsync(
                    reader,
                    cancellationToken);
                if (string.Equals(
                        command,
                        "SHOW",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(OpenSettings);
                }
                else if (string.Equals(
                             command,
                             "EXIT",
                             StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(
                        () => _ = RequestShutdownAsync());
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Write($"Single-instance pipe: {exception.Message}");
            }
        }
    }

    private static async Task<string> ReadActivationCommandAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[5];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(count, 1),
                cancellationToken).ConfigureAwait(false);
            if (read == 0 || buffer[count] is '\r' or '\n')
                break;
            count += read;
        }

        return new string(buffer, 0, count);
    }

    private void BindHotkeyAtStartup()
    {
        _boundHotkey = _config!.Hotkey;
        _boundToggleHotkey = _config.ToggleReplayHotkey;
        try
        {
            _hotkeys = new HotkeyManager(
                _config.Hotkey,
                _config.ToggleReplayHotkey);
            SubscribeHotkeys();
        }
        catch (Exception exception)
        {
            Log.Write($"Global hotkey unavailable: {exception.Message}");
            _pendingUiError = exception.Message;
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Notify.HotkeyUnavailableTitle"),
                exception.Message,
                OverlayTone.Warning);
        }
    }

    private void SubscribeHotkeys()
    {
        _hotkeys!.SaveRequested += SaveReplay;
        _hotkeys.ToggleRequested += ToggleReplayFromHotkey;
    }

    private void ToggleReplayFromHotkey() => _ = ToggleReplayAsync();

    private async Task ToggleReplayAsync()
    {
        try
        {
            await SetReplayEnabledGuardedAsync(null);
        }
        catch (Exception exception)
        {
            Log.Write($"Replay hotkey toggle failed: {exception}");
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Error.Attention"),
                exception.Message,
                OverlayTone.Error);
        }
    }

    private bool TryStartPipeline(bool showError)
    {
        if (IsReplayRunning)
            return true;

        ObsReplayEngine? engine = null;
        try
        {
            string requestedCodec = _config!.Codec;
            engine = new ObsReplayEngine(_config);
            engine.Faulted += reason => OnPipelineFault(engine, reason);
            engine.Start();
            _obs = engine;
            _replayRunning = true;
            _capabilities = engine.Capabilities;
            if (!string.Equals(
                    requestedCodec,
                    _config.Codec,
                    StringComparison.OrdinalIgnoreCase))
            {
                _config.Save();
            }
            _pipelineStartedUtc = DateTime.UtcNow;
            _nextRecoveryUtc = DateTime.MinValue;
            _recoveryFailures = 0;
            UpdateUiState();
            return true;
        }
        catch (Exception exception)
        {
            if (engine is not null)
                _capabilities = engine.Capabilities;
            StopPipeline();
            Log.Write($"OBS pipeline startup failed: {exception}");
            if (showError)
            {
                ShowOverlayNotification(
                    "!",
                    Localization.Text("L.Notify.CaptureFailed"),
                    exception.Message,
                    OverlayTone.Error);
                _pendingUiError = exception.Message;
                _settingsWindow?.ShowError(
                    Localization.Text("L.Notify.CaptureFailed"),
                    exception.Message);
            }
            UpdateUiState();
            return false;
        }
    }

    private void StopPipeline()
    {
        ObsReplayEngine? engine = _obs;
        _obs = null;
        _replayRunning = false;
        _captureDescription = null;
        try
        {
            engine?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Write($"OBS pipeline shutdown failed: {exception}");
        }
    }

    private async Task<bool> TryStartPipelineAsync(bool showError)
    {
        await _pipelineGate.WaitAsync();
        try
        {
            return await TryStartPipelineCoreAsync(showError);
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task<bool> TryStartPipelineCoreAsync(bool showError)
    {
        if (IsReplayRunning)
            return true;

        ObsReplayEngine? engine = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string requestedCodec = _config!.Codec;
            engine = new ObsReplayEngine(_config);
            engine.Faulted += reason => OnPipelineFault(engine, reason);
            string description = await RunOnObsThreadAsync(() =>
            {
                engine.Start();
                return engine.Description;
            });
            _obs = engine;
            _replayRunning = true;
            _captureDescription = description;
            _capabilities = engine.Capabilities;
            if (!string.Equals(
                    requestedCodec,
                    _config.Codec,
                    StringComparison.OrdinalIgnoreCase))
            {
                _config.Save();
            }
            _pipelineStartedUtc = DateTime.UtcNow;
            _nextRecoveryUtc = DateTime.MinValue;
            _recoveryFailures = 0;
            Log.Write($"OBS pipeline started in {stopwatch.ElapsedMilliseconds} ms.");
            UpdateUiState();
            return true;
        }
        catch (Exception exception)
        {
            if (engine is not null)
                _capabilities = engine.Capabilities;
            _obs = null;
            _replayRunning = false;
            _captureDescription = null;
            if (engine is not null)
            {
                try
                {
                    await RunOnObsThreadAsync(engine.Dispose);
                }
                catch (Exception disposeException)
                {
                    Log.Write($"OBS pipeline cleanup failed: {disposeException}");
                }
            }
            Log.Write(
                $"OBS pipeline startup failed after {stopwatch.ElapsedMilliseconds} ms: " +
                exception);
            if (showError)
            {
                ShowOverlayNotification(
                    "!",
                    Localization.Text("L.Notify.CaptureFailed"),
                    exception.Message,
                    OverlayTone.Error);
                _pendingUiError = exception.Message;
                _settingsWindow?.ShowError(
                    Localization.Text("L.Notify.CaptureFailed"),
                    exception.Message);
            }
            UpdateUiState();
            return false;
        }
    }

    private async Task StopPipelineCoreAsync()
    {
        ObsReplayEngine? engine = _obs;
        _obs = null;
        _replayRunning = false;
        _captureDescription = null;
        if (engine is null)
            return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await RunOnObsThreadAsync(engine.Dispose);
            Log.Write($"OBS pipeline stopped in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception exception)
        {
            Log.Write($"OBS pipeline shutdown failed: {exception}");
        }
    }

    private void StartHealthMonitor()
    {
        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _healthTimer.Tick += async (_, _) => await MonitorPipelineSafeAsync();
        _healthTimer.Start();
    }

    private void StartCaptureStateMonitor()
    {
        _captureStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _captureStateTimer.Tick += async (_, _) =>
            await RefreshAutomaticCaptureStateSafeAsync();
        _captureStateTimer.Start();
    }

    private async Task RefreshAutomaticCaptureStateSafeAsync()
    {
        if (_uiOnly || !IsReplayRunning ||
            Volatile.Read(ref _exiting) != 0 ||
            Interlocked.Exchange(ref _captureStateRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (!await _pipelineGate.WaitAsync(0))
                return;
            try
            {
                ObsReplayEngine? engine = _obs;
                if (engine is null || !engine.IsAutomaticCapture)
                    return;
                (bool changed, string description) =
                    await RunOnObsThreadAsync(() =>
                    {
                        bool sourceChanged = engine.RefreshCaptureState();
                        return (sourceChanged, engine.Description);
                    });
                if (changed && ReferenceEquals(engine, _obs))
                {
                    _captureDescription = description;
                    UpdateUiState();
                }
            }
            finally
            {
                _pipelineGate.Release();
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Capture source monitor failed: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureStateRefreshInProgress, 0);
        }
    }

    private async Task MonitorPipelineSafeAsync()
    {
        try
        {
            await MonitorPipelineAsync();
        }
        catch (Exception exception)
        {
            Log.Write($"Health monitor failed: {exception}");
        }
    }

    private async Task MonitorPipelineAsync()
    {
        if (_uiOnly ||
            _config?.ReplayEnabled != true ||
            Interlocked.CompareExchange(ref _recoveryInProgress, 0, 0) != 0 ||
            DateTime.UtcNow < _nextRecoveryUtc)
        {
            return;
        }

        if (!await _pipelineGate.WaitAsync(0))
            return;

        string? recoveryReason = null;
        try
        {
            ObsReplayEngine? engine = _obs;
            if (engine is null)
            {
                recoveryReason = Localization.Text(
                    "L.Recovery.ModuleStopped");
            }
            else
            {
                bool checkHealth = DateTime.UtcNow - _pipelineStartedUtc >=
                                   TimeSpan.FromSeconds(8);
                (bool healthy, string? description) =
                    await RunOnObsThreadAsync(() =>
                    {
                        engine.RefreshCaptureState();
                        return (
                            checkHealth ? engine.IsHealthy : true,
                            engine.Description);
                    });
                if (healthy)
                    _captureDescription = description;
                else
                    recoveryReason = Localization.Text(
                        "L.Recovery.NoFrames");
            }
        }
        finally
        {
            _pipelineGate.Release();
        }

        if (recoveryReason is not null)
            await RecoverPipelineAsync(recoveryReason);
        else
            UpdateUiState();
    }

    private void OnPipelineFault(ObsReplayEngine source, string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(source, _obs))
                _ = RecoverPipelineSafeAsync(reason);
        });
    }

    private async Task RecoverPipelineSafeAsync(string reason)
    {
        try
        {
            await RecoverPipelineAsync(reason);
        }
        catch (Exception exception)
        {
            Log.Write($"Pipeline recovery failed unexpectedly: {exception}");
            _pendingUiError = exception.Message;
            _settingsWindow?.ShowError(
                Localization.Text("L.Notify.RecoveryFailedTitle"),
                exception.Message);
        }
    }

    private async Task RecoverPipelineAsync(string reason)
    {
        if (_config?.ReplayEnabled != true ||
            DateTime.UtcNow < _nextRecoveryUtc ||
            Interlocked.Exchange(ref _recoveryInProgress, 1) != 0)
        {
            return;
        }

        bool gateHeld = false;
        try
        {
            await _pipelineGate.WaitAsync();
            gateHeld = true;
            if (_config?.ReplayEnabled != true)
                return;

            Log.Write($"Watchdog: {reason}");
            ShowOverlayNotification(
                "↻",
                Localization.Text("L.Notify.RecoveryTitle"),
                reason,
                OverlayTone.Warning);
            await StopPipelineCoreAsync();

            if (await TryStartPipelineCoreAsync(showError: false))
            {
                _recoveryFailures = 0;
                _nextRecoveryUtc = DateTime.MinValue;
                ShowOverlayNotification(
                    "✓",
                    Localization.Text("L.Notify.RecoveredTitle"),
                    Localization.Text("L.Notify.RecoveredDetail"),
                    OverlayTone.Success);
                return;
            }

            _recoveryFailures++;
            int delaySeconds = _recoveryFailures switch
            {
                1 => 3,
                2 => 5,
                3 => 10,
                _ => 30,
            };
            _nextRecoveryUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            string message = Localization.Format(
                "L.Notify.ReasonRetry",
                reason,
                delaySeconds);
            _pendingUiError = message;
            _settingsWindow?.UpdateRecoveryState(
                Localization.Format("L.Notify.RetryIn", delaySeconds));
            _settingsWindow?.ShowError(
                Localization.Text("L.Notify.RecoveryFailedTitle"),
                message);
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Notify.RecoveryUnavailableTitle"),
                Localization.Format("L.Notify.RetryIn", delaySeconds),
                OverlayTone.Error);
        }
        finally
        {
            if (gateHeld)
                _pipelineGate.Release();
            Interlocked.Exchange(ref _recoveryInProgress, 0);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("TrayMenu"),
        };

        _saveMenuItem = CreateMenuItem(
            Localization.Text("L.Tray.Save"),
            _config!.Hotkey);
        _saveMenuItem.Click += (_, _) => SaveReplay();
        menu.Items.Add(_saveMenuItem);

        _toggleMenuItem = CreateMenuItem(
            Localization.Text("L.Tray.Toggle"),
            _config.ToggleReplayHotkey);
        _toggleMenuItem.Click += (_, _) => ToggleReplayFromHotkey();
        menu.Items.Add(_toggleMenuItem);

        _openFolderMenuItem = CreateMenuItem(
            Localization.Text("L.Tray.OpenFolder"));
        _openFolderMenuItem.Click += async (_, _) => await OpenOutputFolderAsync();
        menu.Items.Add(_openFolderMenuItem);

        _settingsMenuItem = CreateMenuItem(
            Localization.Text("L.Tray.OpenApp"));
        _settingsMenuItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(_settingsMenuItem);
        menu.Items.Add(new Separator
        {
            Style = (Style)FindResource("TrayMenuSeparator"),
        });

        _exitMenuItem = CreateMenuItem(Localization.Text("L.Tray.Exit"));
        _exitMenuItem.Click += (_, _) => _ = RequestShutdownAsync();
        menu.Items.Add(_exitMenuItem);

        _tray = new TaskbarIcon
        {
            Icon = CreateIcon(),
            ToolTipText = Localization.Text("L.Brand"),
            ContextMenu = menu,
            DoubleClickCommand = new ActionCommand(OpenSettings),
        };
        _tray.ForceCreate();
        UpdateUiState();
    }

    private MenuItem CreateMenuItem(string header, string gesture = "") =>
        new()
        {
            Header = header,
            InputGestureText = gesture,
            Style = (Style)FindResource("TrayMenuItem"),
        };

    private void ShowOverlayNotification(
        string glyph,
        string title,
        string detail,
        OverlayTone tone,
        int durationMilliseconds = 3200)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowOverlayNotification(
                glyph,
                title,
                detail,
                tone,
                durationMilliseconds));
            return;
        }

        _overlayNotification ??= new OverlayNotificationWindow();
        _overlayNotification.ShowNotification(
            glyph,
            title,
            detail,
            tone,
            durationMilliseconds);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        EncoderCapabilities capabilities = _capabilities ?? EncoderCapabilities.Preview();
        _settingsWindow = new SettingsWindow(
            _config!,
            IsReplayRunning,
            SaveReplay,
            SetReplayEnabledAsync,
            SetAudioSourcesAsync,
            ApplySettingsAsync,
            capabilities,
            CheckForUpdatesAsync,
            PrepareAndLaunchUpdateAsync);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_uiOnly)
                Shutdown();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (_config!.ReplayEnabled != true &&
            (_capabilities is null ||
             !string.IsNullOrWhiteSpace(_capabilities.ProbeError)))
        {
            _ = EnsureCapabilitiesSafeAsync();
        }
        if (!string.IsNullOrWhiteSpace(_pendingUiError))
        {
            _settingsWindow.ShowError(
                Localization.Text("L.Error.Attention"),
                _pendingUiError);
            _pendingUiError = null;
        }
    }

    private Task<UpdateRelease?> CheckForUpdatesAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (AppDistribution.IsMicrosoftStore)
            return Task.FromResult<UpdateRelease?>(null);

#if DEBUG
        if (_qaUpdateAvailable)
        {
            var asset = new UpdateAsset(
                "qa",
                new Uri("https://github.com/FaulMit/captail"),
                1,
                $"sha256:{new string('0', 64)}");
            return Task.FromResult<UpdateRelease?>(
                new UpdateRelease(
                    new Version(0, 2, 0),
                    "v0.2.0",
                    new Uri(
                        "https://github.com/FaulMit/captail/releases/tag/v0.2.0"),
                    true,
                    asset,
                    asset,
                    null));
        }
#endif
        return _updateService.CheckAsync(force, cancellationToken);
    }

    private async Task PrepareAndLaunchUpdateAsync(
        UpdateRelease release,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        PreparedUpdate update = await _updateService.PrepareAsync(
            release,
            progress,
            cancellationToken);
        UpdateService.Launch(update);

        _updateShutdownTimer?.Stop();
        _updateShutdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _updateShutdownTimer.Tick += (_, _) =>
        {
            _updateShutdownTimer?.Stop();
            _updateShutdownTimer = null;
            _ = RequestShutdownAsync();
        };
        _updateShutdownTimer.Start();
    }

    private async Task EnsureCapabilitiesAsync()
    {
        if (_capabilities is not null &&
            string.IsNullOrWhiteSpace(_capabilities.ProbeError))
        {
            return;
        }

        if (!await _pipelineGate.WaitAsync(0))
            return;

        try
        {
            _capabilities = await RunOnObsThreadAsync(
                () => ObsReplayEngine.ProbeCapabilities(_config!));
        }
        finally
        {
            _pipelineGate.Release();
        }
        if (_uiOnly && !string.IsNullOrWhiteSpace(_capabilities.ProbeError))
            _capabilities = EncoderCapabilities.Preview();

        if (!_capabilities.Supports(_config!.Codec) &&
            _capabilities.FallbackCodec() is string fallback)
        {
            _config.Codec = fallback;
            _config.Save();
        }
        UpdateUiState();
    }

    private async Task EnsureCapabilitiesSafeAsync()
    {
        try
        {
            await EnsureCapabilitiesAsync();
        }
        catch (Exception exception)
        {
            Log.Write($"GPU capability refresh failed: {exception}");
            _settingsWindow?.ShowError(
                Localization.Text("L.Error.Attention"),
                exception.Message);
        }
    }

    private async Task OpenOutputFolderAsync()
    {
        try
        {
            string outputDirectory = _config!.OutputDirectory;
            await Task.Run(() => Directory.CreateDirectory(outputDirectory));
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { outputDirectory },
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            Log.Write($"Open output folder failed: {exception}");
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Error.FolderTitle"),
                exception.Message,
                OverlayTone.Error);
        }
    }

    private Task<bool> SetReplayEnabledAsync(bool enabled) =>
        SetReplayEnabledGuardedAsync(enabled);

    private async Task<bool> SetReplayEnabledGuardedAsync(bool? requestedState)
    {
        await _pipelineGate.WaitAsync();
        bool enabled = requestedState ?? !_config!.ReplayEnabled;
        bool previousEnabled = _config!.ReplayEnabled;
        bool wasRunning = IsReplayRunning;
        try
        {
            return await SetReplayEnabledCoreAsync(enabled);
        }
        catch (Exception exception)
        {
            Log.Write($"Replay toggle failed; rolling back: {exception}");
            _config.ReplayEnabled = previousEnabled;
            SaveRollbackConfig("replay toggle");
            if (wasRunning && !IsReplayRunning)
                await TryStartPipelineCoreAsync(showError: false);
            else if (!wasRunning && IsReplayRunning)
                await StopPipelineCoreAsync();
            UpdateUiState();
            _settingsWindow?.ShowError(
                Localization.Text("L.Error.Attention"),
                exception.Message);
            return IsReplayRunning;
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task<bool> SetReplayEnabledCoreAsync(bool enabled)
    {
        if (enabled)
        {
            bool started = await TryStartPipelineCoreAsync(showError: true);
            if (started)
            {
                _config!.ReplayEnabled = true;
                _config.Save();
                ShowOverlayNotification(
                    "●",
                    Localization.Text("L.Notify.EnabledTitle"),
                    Localization.Format(
                        "L.Status.BufferLast",
                        FormatDuration(_config.BufferSeconds)),
                    OverlayTone.Success);
            }
            return started;
        }

        await StopPipelineCoreAsync();
        _config!.ReplayEnabled = false;
        _config.Save();
        _nextRecoveryUtc = DateTime.MinValue;
        _recoveryFailures = 0;
        UpdateUiState();
        ShowOverlayNotification(
            "■",
            Localization.Text("L.Notify.DisabledTitle"),
            Localization.Text("L.Notify.DisabledDetail"),
            OverlayTone.Neutral);
        return false;
    }

    private async Task<bool> SetAudioSourcesAsync(
        bool captureSystemAudio,
        bool captureMicrophone,
        string systemAudioDeviceId,
        string microphoneDeviceId)
    {
        await _pipelineGate.WaitAsync();
        Config previous = _config!.Clone();
        bool wasRunning = IsReplayRunning;
        try
        {
            if (previous.CaptureSystemAudio == captureSystemAudio &&
                previous.CaptureMicrophone == captureMicrophone &&
                previous.SystemAudioDeviceId == systemAudioDeviceId &&
                previous.MicrophoneDeviceId == microphoneDeviceId)
            {
                return true;
            }

            _config.CaptureSystemAudio = captureSystemAudio;
            _config.CaptureMicrophone = captureMicrophone;
            _config.SystemAudioDeviceId = systemAudioDeviceId;
            _config.MicrophoneDeviceId = microphoneDeviceId;
            _config.Normalize();

            if (!IsReplayRunning)
            {
                _config.Save();
                UpdateUiState();
                return true;
            }

            await StopPipelineCoreAsync();
            if (await TryStartPipelineCoreAsync(showError: true))
            {
                _config.Save();
                return true;
            }
            throw new InvalidOperationException(
                Localization.Text("L.Error.AudioSourceMessage"));
        }
        catch (Exception exception)
        {
            Log.Write($"Audio source change failed; rolling back: {exception}");
            if (IsReplayRunning)
                await StopPipelineCoreAsync();
            _config.CopyFrom(previous);
            SaveRollbackConfig("audio source change");
            if (wasRunning)
                await TryStartPipelineCoreAsync(showError: false);
            UpdateUiState();
            return false;
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task<bool> ApplySettingsAsync(
        Config candidate,
        bool autostartEnabled)
    {
        candidate.Normalize();
        if (_uiOnly)
        {
            _config!.CopyFrom(candidate);
            _config.Save();
            UpdateUiState();
            return true;
        }

        await _pipelineGate.WaitAsync();
        Config previous = _config!.Clone();
        bool previousAutostart = await Autostart.IsEnabledAsync();
        bool wasRunning = IsReplayRunning;
        bool pipelineChanged = !previous.PipelineEquals(candidate);
        bool pipelineTouched = false;
        try
        {
            ApplyHotkeys(candidate);

            bool mustStop = wasRunning &&
                (!candidate.ReplayEnabled || pipelineChanged);
            if (mustStop)
            {
                pipelineTouched = true;
                await StopPipelineCoreAsync();
            }

            _config.CopyFrom(candidate);
            bool mustStart = candidate.ReplayEnabled &&
                (!wasRunning || pipelineChanged);
            if (mustStart)
            {
                pipelineTouched = true;
                if (!await TryStartPipelineCoreAsync(showError: true))
                    throw new InvalidOperationException(
                        Localization.Text("L.Engine.BufferStartFailed"));
            }

            await Autostart.SetEnabledAsync(autostartEnabled);
            _config.Save();
            UpdateUiState();

            ShowOverlayNotification(
                "✓",
                Localization.Text("L.Notify.SettingsApplied"),
                _config.ReplayEnabled
                    ? $"{_obs!.ActiveCodec.ToUpperInvariant()} · " +
                      $"{_config.FrameRate} FPS · " +
                      $"{FormatDuration(_config.BufferSeconds)}"
                    : Localization.Text("L.Status.Disabled"),
                OverlayTone.Success);
            return true;
        }
        catch (Exception exception)
        {
            Log.Write($"Apply settings failed; rolling back: {exception}");
            if (pipelineTouched && IsReplayRunning)
                await StopPipelineCoreAsync();

            _config.CopyFrom(previous);
            SaveRollbackConfig("settings apply");
            try
            {
                ApplyHotkeys(previous);
            }
            catch (Exception rollbackException)
            {
                Log.Write($"Hotkey rollback failed: {rollbackException}");
            }
            try
            {
                await Autostart.SetEnabledAsync(previousAutostart);
            }
            catch (Exception rollbackException)
            {
                Log.Write($"Autostart rollback failed: {rollbackException}");
            }
            if (wasRunning && !IsReplayRunning)
                await TryStartPipelineCoreAsync(showError: false);

            UpdateUiState();
            _settingsWindow?.ShowError(
                Localization.Text("L.Error.Attention"),
                exception.Message);
            return false;
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private void ApplyHotkeys(Config config)
    {
        if (_hotkeys is null)
        {
            _hotkeys = new HotkeyManager(config.Hotkey, config.ToggleReplayHotkey);
            SubscribeHotkeys();
        }
        else
        {
            _hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey);
        }
        _boundHotkey = config.Hotkey;
        _boundToggleHotkey = config.ToggleReplayHotkey;
    }

    private void SaveRollbackConfig(string operation)
    {
        try
        {
            _config!.Save();
        }
        catch (Exception exception)
        {
            Log.Write($"Could not persist {operation} rollback: {exception}");
        }
    }

    private void UpdateUiState()
    {
        bool active = IsReplayRunning;
        string codec = _obs?.ActiveCodec ?? _config?.Codec ?? "h264";
        int availableReplaySeconds = active
            ? _obs?.AvailableReplaySeconds ?? _config!.BufferSeconds
            : 0;
        if (_capabilities is not null)
            _settingsWindow?.UpdateCapabilities(_capabilities);
        _settingsWindow?.UpdateRuntimeState(
            active,
            codec,
            _captureDescription,
            availableReplaySeconds);
        if (_tray is not null)
        {
            _tray.ToolTipText = active
                ? Localization.Format(
                    "L.Tray.Active",
                    FormatDuration(_config!.BufferSeconds))
                : Localization.Text("L.Tray.Disabled");
        }
        if (_saveMenuItem is not null)
            _saveMenuItem.InputGestureText = _config?.Hotkey ?? "";
        if (_toggleMenuItem is not null)
            _toggleMenuItem.InputGestureText =
                _config?.ToggleReplayHotkey ?? "";
    }

    private void SaveReplay()
    {
        ObsReplayEngine? engine = _obs;
        if (engine is null || !IsReplayRunning ||
            Volatile.Read(ref _exiting) != 0)
        {
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Notify.ReplayOff"),
                Localization.Text("L.Notify.EnableBeforeSave"),
                OverlayTone.Warning);
            return;
        }
        if (Interlocked.Exchange(ref _saving, 1) == 1)
            return;
        _ = SaveReplayCoreAsync(engine);
    }

    private async Task SaveReplayCoreAsync(ObsReplayEngine engine)
    {
        try
        {
            int availableReplaySeconds = Math.Max(
                1,
                Math.Min(
                    _config!.BufferSeconds,
                    engine.AvailableReplaySeconds));
            // Shown immediately so the user sees progress; replaced by the result
            // notification once the file is on disk. The long duration is a safety
            // net — the "saved"/"failed" notification supersedes it well before then.
            ShowOverlayNotification(
                "⟳",
                Localization.Text("L.Notify.Saving"),
                Localization.Format(
                    "L.Notify.SavingDetail",
                    FormatDuration(availableReplaySeconds)),
                OverlayTone.Neutral,
                30_000);
            string path = await SaveReplayGuardedAsync(engine);
            _settingsWindow?.NotifyReplaySaved(path);
            ShowOverlayNotification(
                "✓",
                Localization.Text("L.Notify.Saved"),
                Path.GetFileName(path),
                OverlayTone.Success);
        }
        catch (Exception exception)
        {
            Log.Write($"Replay save failed: {exception}");
            ShowOverlayNotification(
                "!",
                Localization.Text("L.Notify.SaveError"),
                exception.Message,
                OverlayTone.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _saving, 0);
        }
    }

    private async Task<string> SaveReplayGuardedAsync(ObsReplayEngine engine)
    {
        await _pipelineGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(engine, _obs) || !IsReplayRunning ||
                Volatile.Read(ref _exiting) != 0)
            {
                throw new InvalidOperationException(
                    Localization.Text("L.Notify.EnableBeforeSave"));
            }
            ReplaySaveOperation operation = await RunOnObsThreadAsync(
                    () => engine.BeginSaveReplay())
                .ConfigureAwait(false);
            bool snapshotStarted = await WaitForSaveSnapshotAsync(
                    engine,
                    operation)
                .ConfigureAwait(false);
            if (snapshotStarted)
            {
                await RunOnObsThreadAsync(engine.ResetReplayWindow)
                    .ConfigureAwait(false);
            }

            string path = await operation.Completion.ConfigureAwait(false);
            if (!snapshotStarted)
            {
                Log.Write(
                    "Replay snapshot marker was delayed; advancing window " +
                    "after mux completion.");
                await RunOnObsThreadAsync(engine.ResetReplayWindow)
                    .ConfigureAwait(false);
            }
            try
            {
                return ReplayPaths.RouteSavedReplay(
                    _config!,
                    path,
                    operation.GameExecutable);
            }
            catch (Exception exception)
            {
                Log.Write($"Could not route replay into game folder: {exception}");
                return path;
            }
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

#if DEBUG
    private async void RunReplaySegmentsTest()
    {
        try
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Captail",
                $"obs_segments_{Environment.ProcessId}");
            _config = new Config
            {
                ReplayEnabled = true,
                BufferSeconds = 15,
                FrameRate = 30,
                BitrateMbps = 8,
                Codec = "h264",
                CaptureSource = "desktop",
                CaptureSystemAudio = false,
                CaptureMicrophone = false,
                OutputDirectory = root,
            };
            if (!await TryStartPipelineAsync(showError: false))
            {
                throw new InvalidOperationException(
                    "The replay segment pipeline did not start.");
            }

            await Task.Delay(TimeSpan.FromSeconds(6));
            string first = await SaveReplayGuardedAsync(_obs!);
            int availableAfterFirst = _obs!.AvailableReplaySeconds;
            await Task.Delay(TimeSpan.FromSeconds(3));
            string second = await SaveReplayGuardedAsync(_obs);
            int availableAfterSecond = _obs.AvailableReplaySeconds;

            bool passed =
                File.Exists(first) &&
                File.Exists(second) &&
                new FileInfo(first).Length > 0 &&
                new FileInfo(second).Length > 0 &&
                availableAfterFirst <= 1 &&
                availableAfterSecond <= 1;
            Log.Write(
                $"OBS_SEGMENT_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"first={first}, second={second}, " +
                $"availableAfterFirst={availableAfterFirst}s, " +
                $"availableAfterSecond={availableAfterSecond}s");
            await StopPipelineCoreAsync();
            Shutdown(passed ? 0 : 13);
        }
        catch (Exception exception)
        {
            Log.Write($"OBS_SEGMENT_TEST FAIL: {exception}");
            Shutdown(13);
        }
    }
#endif

    private async Task<bool> WaitForSaveSnapshotAsync(
        ObsReplayEngine engine,
        ReplaySaveOperation operation)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!ReferenceEquals(engine, _obs) ||
                !IsReplayRunning ||
                Volatile.Read(ref _exiting) != 0)
            {
                throw new InvalidOperationException(
                    Localization.Text("L.Notify.EnableBeforeSave"));
            }

            bool started = await RunOnObsThreadAsync(
                    () => engine.HasSaveSnapshotStarted(operation))
                .ConfigureAwait(false);
            if (started)
                return true;
            if (operation.Completion.IsCompleted)
                break;
            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnLanguageChanged);
            return;
        }

        if (_saveMenuItem is not null)
            _saveMenuItem.Header = Localization.Text("L.Tray.Save");
        if (_toggleMenuItem is not null)
            _toggleMenuItem.Header = Localization.Text("L.Tray.Toggle");
        if (_openFolderMenuItem is not null)
            _openFolderMenuItem.Header = Localization.Text("L.Tray.OpenFolder");
        if (_settingsMenuItem is not null)
            _settingsMenuItem.Header = Localization.Text("L.Tray.OpenApp");
        if (_exitMenuItem is not null)
            _exitMenuItem.Header = Localization.Text("L.Tray.Exit");
        UpdateUiState();
    }

    private static string FormatDuration(int seconds) =>
        Localization.Format(
            seconds < 60 ? "L.Unit.Seconds" : "L.Unit.Minutes",
            seconds < 60 ? seconds : seconds / 60);

    private static Icon CreateIcon()
    {
        using Stream stream = GetResourceStream(
            new Uri("Assets/Captail.ico", UriKind.Relative)).Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private Task RunOnObsThreadAsync(Action action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            _obsTaskScheduler);

    private Task<T> RunOnObsThreadAsync<T>(Func<T> action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            _obsTaskScheduler);

    private async Task RequestShutdownAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
            return;

        _healthTimer?.Stop();
        _captureStateTimer?.Stop();
        await _pipelineGate.WaitAsync();
        try
        {
            await StopPipelineCoreAsync();
        }
        catch (Exception exception)
        {
            Log.Write($"Graceful shutdown failed: {exception}");
        }
        finally
        {
            _pipelineGate.Release();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        bool gracefulShutdownCompleted =
            Interlocked.Exchange(ref _exiting, 1) != 0 && _obs is null;
        Localization.Changed -= OnLanguageChanged;
        _healthTimer?.Stop();
        _captureStateTimer?.Stop();
        _updateShutdownTimer?.Stop();
        _activationServerCts?.Cancel();
        _activationServerCts?.Dispose();
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        bool gateHeld = false;
        try
        {
            if (!gracefulShutdownCompleted)
            {
                gateHeld = _pipelineGate.Wait(TimeSpan.FromSeconds(50));
                if (!gateHeld)
                    Log.Write("Timed out waiting for replay save during shutdown.");
                RunOnObsThreadAsync(StopPipeline).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            Log.Write($"OBS shutdown worker failed: {exception}");
        }
        finally
        {
            if (gateHeld)
                _pipelineGate.Release();
        }
        _obsTaskScheduler.Dispose();
        _overlayNotification?.ClosePermanently();
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Already released during emergency shutdown.
            }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
