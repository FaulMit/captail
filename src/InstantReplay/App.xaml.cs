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

namespace InstantReplay;

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
    private DateTime _pipelineStartedUtc;
    private DateTime _nextRecoveryUtc;
    private int _recoveryFailures;
    private int _recoveryInProgress;
    private OverlayNotificationWindow? _overlayNotification;
    private int _saving;
    private EncoderCapabilities? _capabilities;

    private bool IsReplayRunning => _obs?.IsActive == true;

    protected override void OnStartup(StartupEventArgs e)
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
#else
            const bool faultTest = false;
            const bool codecTest = false;
            const bool capabilityModelTest = false;
            const bool gameCaptureTest = false;
#endif
            bool backgroundLaunch = e.Args.Contains(
                "--background",
                StringComparer.OrdinalIgnoreCase);
            if (!AcquireSingleInstance(
                    backgroundLaunch,
                    _uiOnly || faultTest || codecTest || capabilityModelTest ||
                    gameCaptureTest))
            {
                Shutdown();
                return;
            }

            _config = Config.Load();
            Localization.SetLanguage(_config.Language);
            Localization.Changed += OnLanguageChanged;
#if !DEBUG
            if (!_uiOnly && Autostart.IsEnabled())
            {
                try
                {
                    Autostart.SetEnabled(true);
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
                    Dispatcher.BeginInvoke(
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

            TerminateLegacyInstances();
            CreateTrayIcon();
            BindHotkeyAtStartup();
            StartHealthMonitor();

            if (_config.ReplayEnabled && TryStartPipeline(showError: true))
            {
                ShowOverlayNotification(
                    "●",
                    Localization.Text("L.Notify.ReadyTitle"),
                    Localization.Format(
                        "L.Status.BufferLast",
                        FormatDuration(_config.BufferSeconds)),
                    OverlayTone.Success);
            }

            StartActivationServer();
            if (!backgroundLaunch)
                OpenSettings();
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

            bool passed =
                oldNvidia.Supports("h264") &&
                oldNvidia.Supports("hevc") &&
                !oldNvidia.Supports("av1") &&
                oldNvidia.FallbackCodec() == "h264" &&
                amd.Preferred("av1")?.Family == "amf" &&
                amd.Preferred("h264")?.Family == "amf" &&
                intel.Preferred("av1")?.Family == "qsv" &&
                intel.Preferred("h264")?.Family == "qsv";
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

                await Task.Delay(TimeSpan.FromSeconds(6));
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
            string gamePath = args
                .First(argument => argument.StartsWith(
                    "--qa-game-capture=",
                    StringComparison.OrdinalIgnoreCase))
                ["--qa-game-capture=".Length..];
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
                GameExecutablePath = gamePath,
                CaptureSystemAudio = false,
                CaptureMicrophone = false,
                OutputDirectory = root,
            };
            if (!TryStartPipeline(showError: false))
                throw new InvalidOperationException("OBS Game Capture не запустился.");

            await Task.Delay(TimeSpan.FromSeconds(8));
            string path = await _obs!.SaveReplayAsync();
            bool passed = File.Exists(path) &&
                          new FileInfo(path).Length > 0 &&
                          _obs.IsGameHooked &&
                          _obs.EncodedFrameCount > 0;
            Log.Write(
                $"OBS_GAME_TEST {(passed ? "PASS" : "FAIL")}: " +
                $"hooked={_obs.IsGameHooked}, frames={_obs.EncodedFrameCount}, path={path}");
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
            if (!TryStartPipeline(showError: false))
                throw new InvalidOperationException("Исходный OBS pipeline не запустился.");

            DateTime originalStart = _pipelineStartedUtc;
            await Task.Delay(TimeSpan.FromSeconds(3));
            RecoverPipeline("QA: искусственный перезапуск OBS.");
            await Task.Delay(TimeSpan.FromSeconds(6));
            bool restarted = IsReplayRunning && _pipelineStartedUtc > originalStart;
            string path = restarted
                ? await _obs!.SaveReplayAsync()
                : "";
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

    private bool AcquireSingleInstance(bool backgroundLaunch, bool isolatedUiTest)
    {
        string userId = WindowsIdentity.GetCurrent().User?.Value ??
                        Environment.UserName;
        string suffix = userId.Replace('\\', '.') +
                        (isolatedUiTest ? ".UiOnly" : "");
        string mutexName = $@"Local\Everloop.SingleInstance.{suffix}";
        _activationPipeName = $"Everloop.Activate.{suffix}";
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out bool createdNew);
        if (createdNew)
            return true;

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        SendActivationCommand(backgroundLaunch ? "PING" : "SHOW");
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
                // Первый экземпляр ещё запускается.
            }
            catch (IOException)
            {
                // Pipe между попытками пересоздаётся.
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
                string? command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                    await Dispatcher.InvokeAsync(OpenSettings);
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

    private static void TerminateLegacyInstances()
    {
        int currentId = Environment.ProcessId;
        foreach (Process process in Process.GetProcessesByName("InstantReplay"))
        {
            using (process)
            {
                if (process.Id == currentId)
                    continue;
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1200))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(1200);
                    }
                }
                catch (Exception exception)
                {
                    Log.Write(
                        $"Не удалось закрыть старый экземпляр PID {process.Id}: " +
                        exception.Message);
                }
            }
        }
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
            Log.Write($"Глобальный хоткей недоступен: {exception.Message}");
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

    private void ToggleReplayFromHotkey() =>
        SetReplayEnabled(!_config!.ReplayEnabled);

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
            Log.Write($"Запуск OBS pipeline не удался: {exception}");
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
        try
        {
            engine?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Write($"Остановка OBS pipeline: {exception}");
        }
    }

    private void StartHealthMonitor()
    {
        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _healthTimer.Tick += (_, _) => MonitorPipeline();
        _healthTimer.Start();
    }

    private void MonitorPipeline()
    {
        if (_uiOnly ||
            _config?.ReplayEnabled != true ||
            Interlocked.CompareExchange(ref _recoveryInProgress, 0, 0) != 0 ||
            DateTime.UtcNow < _nextRecoveryUtc)
        {
            return;
        }

        if (_obs is null)
        {
            RecoverPipeline(Localization.Text("L.Recovery.ModuleStopped"));
            return;
        }
        if (DateTime.UtcNow - _pipelineStartedUtc < TimeSpan.FromSeconds(8))
            return;
        if (!_obs.IsHealthy)
            RecoverPipeline(Localization.Text("L.Recovery.NoFrames"));
        else
            UpdateUiState();
    }

    private void OnPipelineFault(ObsReplayEngine source, string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(source, _obs))
                RecoverPipeline(reason);
        });
    }

    private void RecoverPipeline(string reason)
    {
        if (_config?.ReplayEnabled != true ||
            DateTime.UtcNow < _nextRecoveryUtc ||
            Interlocked.Exchange(ref _recoveryInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            Log.Write($"Watchdog: {reason}");
            ShowOverlayNotification(
                "↻",
                Localization.Text("L.Notify.RecoveryTitle"),
                reason,
                OverlayTone.Warning);
            StopPipeline();

            if (TryStartPipeline(showError: false))
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
        _openFolderMenuItem.Click += (_, _) =>
        {
            Directory.CreateDirectory(_config.OutputDirectory);
            Process.Start("explorer.exe", _config.OutputDirectory);
        };
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
        _exitMenuItem.Click += (_, _) => Shutdown();
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
            Dispatcher.Invoke(() => ShowOverlayNotification(
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

        EncoderCapabilities capabilities = EnsureCapabilities();
        _settingsWindow = new SettingsWindow(
            _config!,
            IsReplayRunning,
            SaveReplay,
            SetReplayEnabled,
            SetAudioSources,
            ApplySettings,
            capabilities);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_uiOnly)
                Shutdown();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (!string.IsNullOrWhiteSpace(_pendingUiError))
        {
            _settingsWindow.ShowError(
                Localization.Text("L.Error.Attention"),
                _pendingUiError);
            _pendingUiError = null;
        }
    }

    private EncoderCapabilities EnsureCapabilities()
    {
        if (_capabilities is not null &&
            string.IsNullOrWhiteSpace(_capabilities.ProbeError))
        {
            return _capabilities;
        }

        _capabilities = ObsReplayEngine.ProbeCapabilities(_config!);
        if (_uiOnly && !string.IsNullOrWhiteSpace(_capabilities.ProbeError))
            _capabilities = EncoderCapabilities.Preview();

        if (!_capabilities.Supports(_config!.Codec) &&
            _capabilities.FallbackCodec() is string fallback)
        {
            _config.Codec = fallback;
            _config.Save();
        }
        return _capabilities;
    }

    private bool SetReplayEnabled(bool enabled)
    {
        if (enabled)
        {
            bool started = TryStartPipeline(showError: true);
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

        StopPipeline();
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

    private bool SetAudioSources(
        bool captureSystemAudio,
        bool captureMicrophone,
        string systemAudioDeviceId,
        string microphoneDeviceId)
    {
        bool oldSystemAudio = _config!.CaptureSystemAudio;
        bool oldMicrophone = _config.CaptureMicrophone;
        string oldSystemDevice = _config.SystemAudioDeviceId;
        string oldMicrophoneDevice = _config.MicrophoneDeviceId;
        if (oldSystemAudio == captureSystemAudio &&
            oldMicrophone == captureMicrophone &&
            oldSystemDevice == systemAudioDeviceId &&
            oldMicrophoneDevice == microphoneDeviceId)
        {
            return true;
        }

        _config.CaptureSystemAudio = captureSystemAudio;
        _config.CaptureMicrophone = captureMicrophone;
        _config.SystemAudioDeviceId = systemAudioDeviceId;
        _config.MicrophoneDeviceId = microphoneDeviceId;
        _config.Save();

        if (!IsReplayRunning)
        {
            UpdateUiState();
            return true;
        }

        StopPipeline();
        if (TryStartPipeline(showError: true))
            return true;

        _config.CaptureSystemAudio = oldSystemAudio;
        _config.CaptureMicrophone = oldMicrophone;
        _config.SystemAudioDeviceId = oldSystemDevice;
        _config.MicrophoneDeviceId = oldMicrophoneDevice;
        _config.Save();
        TryStartPipeline(showError: false);
        UpdateUiState();
        _settingsWindow?.ShowError(
            Localization.Text("L.Error.AudioSourceTitle"),
            Localization.Text("L.Error.AudioSourceMessage"));
        return false;
    }

    private bool ApplySettings()
    {
        if (_uiOnly)
        {
            UpdateUiState();
            return true;
        }

        bool hotkeysApplied = true;
        try
        {
            if (_hotkeys is null)
            {
                _hotkeys = new HotkeyManager(
                    _config!.Hotkey,
                    _config.ToggleReplayHotkey);
                SubscribeHotkeys();
            }
            else
            {
                _hotkeys.Rebind(
                    _config!.Hotkey,
                    _config.ToggleReplayHotkey);
            }
            _boundHotkey = _config!.Hotkey;
            _boundToggleHotkey = _config.ToggleReplayHotkey;
        }
        catch (Exception exception)
        {
            _config!.Hotkey = _boundHotkey;
            _config.ToggleReplayHotkey = _boundToggleHotkey;
            _config.Save();
            hotkeysApplied = false;
            _settingsWindow?.ShowError(
                Localization.Text("L.Error.BindTitle"),
                exception.Message);
        }

        StopPipeline();
        bool running = !_config!.ReplayEnabled ||
                       TryStartPipeline(showError: true);
        UpdateUiState();

        if (running)
        {
            ShowOverlayNotification(
                "✓",
                Localization.Text("L.Notify.SettingsApplied"),
                _config.ReplayEnabled
                    ? $"{_obs!.ActiveCodec.ToUpperInvariant()} · " +
                      $"{_config.FrameRate} FPS · " +
                      $"{FormatDuration(_config.BufferSeconds)}"
                    : Localization.Text("L.Status.Disabled"),
                OverlayTone.Success);
        }
        if (!hotkeysApplied)
        {
            _settingsWindow?.UpdateRuntimeState(
                IsReplayRunning,
                _obs?.ActiveCodec);
        }
        return running;
    }

    private void UpdateUiState()
    {
        bool active = IsReplayRunning;
        string codec = _obs?.ActiveCodec ?? _config?.Codec ?? "h264";
        if (_capabilities is not null)
            _settingsWindow?.UpdateCapabilities(_capabilities);
        _settingsWindow?.UpdateRuntimeState(
            active,
            codec,
            _obs?.Description);
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
        if (engine?.IsActive != true)
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
            string path = await engine.SaveReplayAsync();
            ShowOverlayNotification(
                "✓",
                Localization.Text("L.Notify.Saved"),
                Path.GetFileName(path),
                OverlayTone.Success);
        }
        catch (Exception exception)
        {
            Log.Write($"Сохранение повтора упало: {exception}");
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
            new Uri("Assets/Everloop.ico", UriKind.Relative)).Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Localization.Changed -= OnLanguageChanged;
        _healthTimer?.Stop();
        _activationServerCts?.Cancel();
        _activationServerCts?.Dispose();
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        StopPipeline();
        _overlayNotification?.ClosePermanently();
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Уже освобождён во время аварийного завершения.
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
