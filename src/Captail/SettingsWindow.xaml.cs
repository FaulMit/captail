using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Captail.Interop;

namespace Captail;

public partial class SettingsWindow : Window
{
    private const double DashboardHeight = 650;

    private readonly Config _config;
    private readonly Action _saveReplay;
    private readonly Func<bool, Task<bool>> _setReplayEnabled;
    private readonly Func<bool, bool, string, string, Task<bool>> _setAudioSources;
    private readonly Func<Config, bool, Task<bool>> _applySettings;
    private readonly Func<bool, CancellationToken, Task<UpdateRelease?>>
        _checkForUpdates;
    private readonly Func<
        UpdateRelease,
        IProgress<int>,
        CancellationToken,
        Task> _installUpdate;
    private EncoderCapabilities _capabilities;
    private readonly DispatcherTimer _diskTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ReplayLibrary _replayLibrary;
    private string _outputDirectory;
    private string _pendingSaveHotkey;
    private string _pendingToggleHotkey;
    private Button? _capturingHotkeyButton;
    private bool _updatingUi;
    private bool _runtimeActive;
    private int _availableReplaySeconds;
    private bool? _animatedRecordingState;
    private int _deviceRefreshVersion;
    private int _diskRefreshInProgress;
    private int _actionInProgress;
    private UpdateRelease? _availableUpdate;
    private UpdateDisplayState _updateDisplayState = UpdateDisplayState.Current;
    private int _updateProgress;
    private bool _updateCheckInProgress;
    private bool _updateInstallInProgress;
    private int _libraryRefreshInProgress;
    private ReplayClip? _pendingDeleteClip;
    private IReadOnlyList<CaptureInterop.MonitorInfo> _monitors = [];
    private readonly List<DisplayIdentifierWindow> _displayIdentifierWindows = [];

    public bool Applied { get; private set; }

    internal SettingsWindow(
        Config config,
        bool runtimeActive,
        Action saveReplay,
        Func<bool, Task<bool>> setReplayEnabled,
        Func<bool, bool, string, string, Task<bool>> setAudioSources,
        Func<Config, bool, Task<bool>> applySettings,
        EncoderCapabilities capabilities,
        Func<bool, CancellationToken, Task<UpdateRelease?>> checkForUpdates,
        Func<
            UpdateRelease,
            IProgress<int>,
            CancellationToken,
            Task> installUpdate)
    {
        _config = config;
        _saveReplay = saveReplay;
        _setReplayEnabled = setReplayEnabled;
        _setAudioSources = setAudioSources;
        _applySettings = applySettings;
        _capabilities = capabilities;
        _checkForUpdates = checkForUpdates;
        _installUpdate = installUpdate;
        _outputDirectory = config.OutputDirectory;
        _pendingSaveHotkey = config.Hotkey;
        _pendingToggleHotkey = config.ToggleReplayHotkey;
        _runtimeActive = runtimeActive;

        InitializeComponent();
        LanguageList.ItemsSource = Localization.SupportedLanguages;
        UpdateLanguageMenuSelection();
        _replayLibrary = new ReplayLibrary(new FfmpegAdapter());
        ApplyHardwareCapabilities();
        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _diskTimer.Tick += async (_, _) => await RefreshDiskAsync();
        Localization.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            Localization.Changed -= OnLanguageChanged;
            _diskTimer.Stop();
            _lifetimeCts.Cancel();
            CloseDisplayIdentifiers();
            _lifetimeCts.Dispose();
        };

        ResetDeviceLists();
        LoadSettingsControls();
        UpdateRuntimeState(runtimeActive);
        RenderUpdateStatus();
        Loaded += async (_, _) =>
        {
            await RunUiActionAsync(async () =>
            {
                await Task.WhenAll(
                    LoadDeviceListsAsync(),
                    LoadAutostartStateAsync(),
                    RefreshDiskAsync(),
                    RefreshReplayLibraryAsync());
            });
            if (!AppDistribution.IsMicrosoftStore)
                _ = CheckForUpdatesAsync(force: false);
        };
        _diskTimer.Start();
    }

    private void ApplyHardwareCapabilities()
    {
        SetCodecAvailability(H264CodecItem, "h264", "H.264 (AVC)");
        SetCodecAvailability(HevcCodecItem, "hevc", "H.265 (HEVC)");
        SetCodecAvailability(Av1CodecItem, "av1", "AV1");

        if (!_capabilities.Supports(_config.Codec) &&
            _capabilities.FallbackCodec() is string fallback)
        {
            _config.Codec = fallback;
        }
        UpdateHardwareEncoderText();
    }

    public void UpdateCapabilities(EncoderCapabilities capabilities)
    {
        if (ReferenceEquals(_capabilities, capabilities))
            return;
        _capabilities = capabilities;
        ApplyHardwareCapabilities();
        SelectByTag(CodecBox, _config.Codec);
        UpdateHardwareEncoderText();
    }

    private void SetCodecAvailability(
        ComboBoxItem item,
        string codec,
        string label)
    {
        bool available = _capabilities.Supports(codec);
        item.IsEnabled = available;
        item.Content = available
            ? label
            : Localization.Format("L.Codec.UnavailableSuffix", label);
        item.ToolTip = available
            ? _capabilities.Preferred(codec)?.FamilyDisplayName
            : Localization.Text("L.Codec.Unsupported");
    }

    private void CodecBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsInitialized)
            UpdateHardwareEncoderText();
    }

    private void UpdateHardwareEncoderText()
    {
        string codec = GetSelectedTag(CodecBox, _config.Codec);
        CodecCapability? capability = _capabilities.Preferred(codec);
        HardwareEncoderText.Text = capability is null
            ? _capabilities.ProbeError ??
              Localization.Text("L.Codec.HardwareUnavailable")
            : $"{ShortAdapterName(_capabilities.AdapterName)} · " +
              $"{ShortEncoderName(capability.Family)}";
        HardwareEncoderText.ToolTip = capability is null
            ? HardwareEncoderText.Text
            : $"{_capabilities.AdapterName} · {capability.FamilyDisplayName}";
    }

    private static string ShortAdapterName(string adapterName) =>
        adapterName
            .Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel ", "", StringComparison.OrdinalIgnoreCase);

    private static string ShortEncoderName(string family) =>
        family.ToLowerInvariant() switch
        {
            "nvenc" => "NVENC",
            "amf" => "AMF",
            "qsv" => "Quick Sync",
            _ => "HW",
        };

    private void ResetDeviceLists()
    {
        AudioDeviceBox.Items.Clear();
        MicDeviceBox.Items.Clear();
        MonitorBox.Items.Clear();
        _monitors = [];
        IdentifyDisplaysButton.IsEnabled = false;
        AudioDeviceBox.Items.Add(new ComboBoxItem
        {
            Tag = "",
            Content = Localization.Text("L.Audio.DefaultWindows"),
        });
        MicDeviceBox.Items.Add(new ComboBoxItem
        {
            Tag = "",
            Content = Localization.Text("L.Audio.DefaultWindows"),
        });
        MonitorBox.Items.Add(new ComboBoxItem
        {
            Tag = "0",
            Content = Localization.Text("L.Video.PrimaryMonitor"),
        });
    }

    private async Task LoadDeviceListsAsync()
    {
        int version = Interlocked.Increment(ref _deviceRefreshVersion);
        string systemDevice = GetSelectedTag(
            AudioDeviceBox,
            _config.SystemAudioDeviceId);
        string microphoneDevice = GetSelectedTag(
            MicDeviceBox,
            _config.MicrophoneDeviceId);
        string monitorId = GetSelectedTag(
            MonitorBox,
            _config.MonitorIndex.ToString());

        DeviceListsSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(
                CollectDeviceLists,
                _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _deviceRefreshVersion || _lifetimeCts.IsCancellationRequested)
            return;

        ResetDeviceLists();
        foreach (var (id, name) in snapshot.RenderDevices)
            AudioDeviceBox.Items.Add(new ComboBoxItem { Tag = id, Content = name });
        foreach (var (id, name) in snapshot.CaptureDevices)
            MicDeviceBox.Items.Add(new ComboBoxItem { Tag = id, Content = name });
        if (snapshot.Monitors.Count > 0)
        {
            _monitors = snapshot.Monitors;
            MonitorBox.Items.Clear();
            foreach (var monitor in snapshot.Monitors)
            {
                MonitorBox.Items.Add(new ComboBoxItem
                {
                    Tag = monitor.Index.ToString(),
                    Content = Localization.Format(
                        "L.Video.MonitorFormat",
                        monitor.Index + 1,
                        monitor.Width,
                        monitor.Height),
                });
            }
        }
        IdentifyDisplaysButton.IsEnabled = _monitors.Count > 0;

        SelectByTag(AudioDeviceBox, systemDevice);
        SelectByTag(MicDeviceBox, microphoneDevice);
        SelectByTag(MonitorBox, monitorId);
        UpdateAudioDeviceState();
    }

    private void IdentifyDisplays_Click(object sender, RoutedEventArgs e)
    {
        CloseDisplayIdentifiers();
        foreach (CaptureInterop.MonitorInfo monitor in _monitors)
        {
            var identifier = new DisplayIdentifierWindow(
                monitor,
                monitor.Index + 1);
            identifier.Owner = this;
            identifier.Closed += (_, _) =>
                _displayIdentifierWindows.Remove(identifier);
            _displayIdentifierWindows.Add(identifier);
            identifier.Show();
        }
    }

    private void CloseDisplayIdentifiers()
    {
        DisplayIdentifierWindow[] identifiers =
            _displayIdentifierWindows.ToArray();
        _displayIdentifierWindows.Clear();
        foreach (DisplayIdentifierWindow identifier in identifiers)
        {
            if (identifier.IsLoaded)
                identifier.Close();
        }
    }

    private static DeviceListsSnapshot CollectDeviceLists()
    {
        IReadOnlyList<(string Id, string Name)> renderDevices = [];
        IReadOnlyList<(string Id, string Name)> captureDevices = [];
        IReadOnlyList<CaptureInterop.MonitorInfo> monitors = [];
        try
        {
            renderDevices = AudioDevices.ListRenderDevices();
        }
        catch (Exception ex)
        {
            Log.Write($"Output-device list unavailable: {ex.Message}");
        }
        try
        {
            captureDevices = AudioDevices.ListCaptureDevices();
        }
        catch (Exception ex)
        {
            Log.Write($"Microphone list unavailable: {ex.Message}");
        }
        try
        {
            monitors = CaptureInterop.EnumerateMonitors();
        }
        catch (Exception ex)
        {
            Log.Write($"Monitor list unavailable: {ex.Message}");
        }
        return new DeviceListsSnapshot(renderDevices, captureDevices, monitors);
    }

    private void CaptureSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
            UpdateCaptureSourceState();
    }

    private void UpdateCaptureSourceState()
    {
        bool game = GetSelectedTag(CaptureSourceBox, "desktop") == "game";
        MonitorBox.IsEnabled = true;
        SystemAudioLabel.Text = Localization.Text(
            game ? "L.Audio.GameAudio" : "L.Audio.SystemAudio");
        AudioTrackHintText.Text = Localization.Text(
            game ? "L.Audio.GameAndMic" : "L.Audio.SystemAndMic");
        UpdateAudioDeviceState();
    }

    private void LoadSettingsControls()
    {
        _updatingUi = true;
        try
        {
            SelectRadioByTag(BufferOptions, _config.BufferSeconds.ToString());
            SelectByTag(ReplaySizeLimitBox, _config.MaxReplaySizeMb.ToString());
            SelectRadioByTag(FpsOptions, _config.FrameRate.ToString());
            SelectByTag(CaptureSourceBox, _config.CaptureSource);
            SelectByTag(CodecBox, _config.Codec);
            SelectByTag(BitrateBox, _config.BitrateMbps.ToString());
            SelectByTag(MonitorBox, _config.MonitorIndex.ToString());
            SelectByTag(ResolutionBox, _config.RecordingResolution);
            SelectByTag(AudioDeviceBox, _config.SystemAudioDeviceId);
            SelectByTag(MicDeviceBox, _config.MicrophoneDeviceId);
            SelectByTag(AudioCodecBox, _config.AudioCodec);
            SelectByTag(AudioTrackModeBox, _config.SeparateAudioTracks ? "separate" : "mixed");

            SettingsReplayToggle.IsChecked = _config.ReplayEnabled;
            RecordingIndicatorBox.IsChecked = _config.ShowRecordingIndicator;
            SelectRadioByTag(
                RecordingIndicatorPositionOptions,
                _config.RecordingIndicatorPosition);
            SystemAudioBox.IsChecked = _config.CaptureSystemAudio;
            MicBox.IsChecked = _config.CaptureMicrophone;
            SystemVolumeSlider.Value = Math.Clamp(_config.SystemAudioVolume, 0, 100);
            MicVolumeSlider.Value = Math.Clamp(_config.MicrophoneVolume, 0, 100);
            MicBoostSlider.Value = Math.Clamp(_config.MicrophoneBoostDb, 0, 20);
            OrganizeByGameBox.IsChecked = _config.OrganizeReplaysByGame;

            _pendingSaveHotkey = _config.Hotkey;
            _pendingToggleHotkey = _config.ToggleReplayHotkey;
            SaveHotkeyButton.Content = _pendingSaveHotkey;
            ToggleHotkeyButton.Content = _pendingToggleHotkey;

            _outputDirectory = _config.OutputDirectory;
            OutputDirText.Text = _outputDirectory;
            UpdateAudioDeviceState();
            UpdateCaptureSourceState();
            UpdateHardwareEncoderText();
        }
        finally
        {
            _updatingUi = false;
        }
    }

    public void UpdateRuntimeState(
        bool active,
        string? activeCodec = null,
        string? activeCaptureSource = null,
        int? availableReplaySeconds = null)
    {
        _runtimeActive = active;
        if (availableReplaySeconds is not null)
            _availableReplaySeconds = availableReplaySeconds.Value;
        else if (!active)
            _availableReplaySeconds = 0;
        _updatingUi = true;
        ReplayToggle.IsChecked = active;
        SettingsReplayToggle.IsChecked = active;
        _updatingUi = false;

        string primaryAudio = Localization.Text(
            _config.CaptureSource == "game"
                ? "L.Audio.GameSound"
                : "L.Audio.SystemSound");
        string audio = (_config.CaptureSystemAudio, _config.CaptureMicrophone) switch
        {
            (true, true) when _config.SeparateAudioTracks =>
                Localization.Format("L.Audio.SeparateSuffix", primaryAudio),
            (true, true) =>
                Localization.Format("L.Audio.MixedWithMic", primaryAudio),
            (true, false) => primaryAudio,
            (false, true) => Localization.Text("L.Audio.MicrophoneLower"),
            _ => Localization.Text("L.Audio.VideoOnly"),
        };

        StatusTitleText.Text = Localization.Text(
            active ? "L.Status.Enabled" : "L.Status.Disabled");
        StatusDetailText.Text = active
            ? Localization.Format(
                "L.Status.Detail",
                FormatDuration(_config.BufferSeconds),
                LocalizedCaptureSource(activeCaptureSource),
                audio)
            : Localization.Text("L.Status.Idle");
        StatusRing.Stroke = FindBrush(active ? "AccentBrush" : "RingIdleBrush");
        StatusDot.Fill = FindBrush(active ? "AccentBrush" : "RingIdleBrush");
        SaveReplayButton.IsEnabled = active;
        AnimateRecordingState(active);

        SystemSourceChip.IsChecked = _config.CaptureSystemAudio;
        MicSourceChip.IsChecked = _config.CaptureMicrophone;
        PrimaryAudioChipText.Text = Localization.Text(
            _config.CaptureSource == "game"
                ? "L.Audio.Game"
                : "L.Audio.System");
        SystemSourceChip.ToolTip = _config.CaptureSource == "game"
            ? Localization.Text("L.Audio.GameToggleTip")
            : Localization.Text("L.Audio.ToggleTip");
        SystemSourceDot.Fill = FindBrush(_config.CaptureSystemAudio ? "AccentBrush" : "TextMutedBrush");
        MicSourceDot.Fill = FindBrush(_config.CaptureMicrophone ? "AccentBrush" : "TextMutedBrush");

        string codec = FormatCodec(activeCodec ?? _config.Codec);
        CodecSummaryText.Text = $"{codec} · {FormatResolution(_config.RecordingResolution)}";
        FpsSummaryText.Text = $"{_config.FrameRate} FPS";
        SaveButtonText.Text = Localization.Format(
            "L.Save.Duration",
            FormatDuration(
                active
                    ? Math.Max(1, _availableReplaySeconds)
                    : _config.BufferSeconds));
        HotkeySummaryText.Text = _config.Hotkey;
        OutputFolderSummaryText.Text = _config.OutputDirectory;
    }

    public void UpdateRecoveryState(string detail)
    {
        _runtimeActive = false;
        _updatingUi = true;
        ReplayToggle.IsChecked = true;
        SettingsReplayToggle.IsChecked = true;
        _updatingUi = false;

        StatusTitleText.Text = Localization.Text("L.Status.Recovering");
        StatusDetailText.Text = detail;
        StatusRing.Stroke = FindBrush("ErrorBrush");
        StatusDot.Fill = FindBrush("ErrorBrush");
        SaveReplayButton.IsEnabled = false;
        AnimateRecordingState(false);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings();
        SettingsScrollViewer.ScrollToTop();
    }

    private void LanguagePopup_Opened(object? sender, EventArgs e)
    {
        UpdateLanguageMenuSelection();
        LanguagePopupPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        if (LanguagePopupPanel.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseOut,
                    },
                });
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            LanguageList.Focus();
            if (LanguageList.ItemContainerGenerator.ContainerFromItem(
                    LanguageList.SelectedItem) is ListBoxItem selected)
            {
                selected.Focus();
            }
        });
    }

    private void LanguagePopup_Closed(object? sender, EventArgs e)
    {
        LanguagePopupPanel.BeginAnimation(OpacityProperty, null);
        LanguagePopupPanel.Opacity = 0;
        if (LanguagePopupPanel.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = -5;
        }
        UpdateLanguageMenuSelection();
    }

    private void LanguageList_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(
                LanguageList,
                e.OriginalSource as DependencyObject) is not ListBoxItem item)
        {
            return;
        }

        LanguageList.SelectedItem = item.DataContext;
        ApplySelectedLanguage();
    }

    private void LanguageList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ApplySelectedLanguage();
        e.Handled = true;
    }

    private void ApplySelectedLanguage()
    {
        if (LanguageList.SelectedItem is not LanguageDefinition selected)
            return;

        string previousLanguage = _config.Language;
        try
        {
            if (string.Equals(
                    selected.Code,
                    previousLanguage,
                    StringComparison.OrdinalIgnoreCase))
            {
                LanguagePopup.IsOpen = false;
                return;
            }

            _config.Language = selected.Code;
            _config.Save();
            Localization.SetLanguage(_config.Language);
            UpdateLanguageMenuSelection();
            LanguagePopup.IsOpen = false;
            AnimatePress(LanguageButton);
        }
        catch (Exception exception)
        {
            _config.Language = previousLanguage;
            try
            {
                _config.Save();
                Localization.SetLanguage(previousLanguage);
            }
            catch (Exception rollbackException)
            {
                Log.Write($"Language rollback failed: {rollbackException}");
            }
            HandleUiActionError("Language change", exception);
        }
    }

    private void UpdateLanguageMenuSelection()
    {
        LanguageList.SelectedItem = Localization.SupportedLanguages.FirstOrDefault(
            language => string.Equals(
                language.Code,
                _config.Language,
                StringComparison.OrdinalIgnoreCase));
    }

    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnLanguageChanged);
            return;
        }

        UpdateLanguageMenuSelection();
        _ = RunUiActionAsync(LoadDeviceListsAsync);
        ApplyHardwareCapabilities();
        UpdateCaptureSourceState();
        UpdateRuntimeState(_runtimeActive);
        RenderUpdateStatus();
        _ = RefreshDiskAsync();
        _ = RefreshReplayLibraryAsync();
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdateService.RepositoryUrl,
                UseShellExecute = true,
            });
            AnimatePress(GitHubButton);
        }
        catch (Exception exception)
        {
            HandleUiActionError("Open GitHub repository", exception);
        }
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BugReportInfo.BuildUrl(_config, _capabilities),
                UseShellExecute = true,
            });
            AnimatePress(ReportBugButton);
        }
        catch (Exception exception)
        {
            HandleUiActionError("Open GitHub bug report form", exception);
        }
    }

    private void FeatureRequest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdateService.FeatureRequestUrl,
                UseShellExecute = true,
            });
            AnimatePress(FeatureRequestButton);
        }
        catch (Exception exception)
        {
            HandleUiActionError("Open GitHub feature request form", exception);
        }
    }

    private async void UpdateVersion_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (AppDistribution.IsMicrosoftStore)
            return;

        if (_updateCheckInProgress || _updateInstallInProgress)
            return;

        AnimatePress(UpdateVersionButton);
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(force: true);
            return;
        }

        _updateInstallInProgress = true;
        _updateDisplayState = UpdateDisplayState.Downloading;
        _updateProgress = 0;
        RenderUpdateStatus();

        try
        {
            var progress = new Progress<int>(value =>
            {
                _updateProgress = Math.Clamp(value, 0, 100);
                RenderUpdateStatus();
            });
            await _installUpdate(
                _availableUpdate,
                progress,
                _lifetimeCts.Token);
            _updateDisplayState = UpdateDisplayState.Installing;
            RenderUpdateStatus();
        }
        catch (OperationCanceledException)
            when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Update installation failed: {exception}");
            _updateInstallInProgress = false;
            _updateDisplayState = UpdateDisplayState.Available;
            RenderUpdateStatus();
            ShowError(
                Localization.Text("L.Update.InstallFailedTitle"),
                exception.Message);
        }
    }

    private async Task CheckForUpdatesAsync(bool force)
    {
        if (AppDistribution.IsMicrosoftStore)
            return;

        if (_updateCheckInProgress || _updateInstallInProgress)
            return;

        _updateCheckInProgress = true;
        _updateDisplayState = UpdateDisplayState.Checking;
        RenderUpdateStatus();
        try
        {
            _availableUpdate = await _checkForUpdates(
                force,
                _lifetimeCts.Token);
            _updateDisplayState = _availableUpdate is null
                ? UpdateDisplayState.Current
                : UpdateDisplayState.Available;
            RenderUpdateStatus();
            if (_availableUpdate is not null)
            {
                AnimatePress(UpdateVersionButton);
                var pulse = new DoubleAnimation(
                    0.28,
                    1,
                    TimeSpan.FromMilliseconds(420))
                {
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(2),
                };
                UpdateStatusDot.BeginAnimation(
                    OpacityProperty,
                    pulse);
            }
        }
        catch (OperationCanceledException)
            when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Update check failed: {exception}");
            _availableUpdate = null;
            _updateDisplayState = UpdateDisplayState.CheckFailed;
            RenderUpdateStatus();
            if (force)
            {
                ShowError(
                    Localization.Text("L.Update.CheckFailedTitle"),
                    exception.Message);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!_lifetimeCts.IsCancellationRequested)
                RenderUpdateStatus();
        }
    }

    private void RenderUpdateStatus()
    {
        string current = UpdateService.CurrentVersionText;
        if (AppDistribution.IsMicrosoftStore)
        {
            UpdateVersionButton.IsEnabled = false;
            UpdateVersionButton.Tag = "current";
            UpdateStatusDot.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = Localization.Format(
                "L.Update.StoreManaged",
                current);
            UpdateVersionButton.ToolTip =
                Localization.Text("L.Update.StoreManagedTip");
            return;
        }

        UpdateVersionButton.IsEnabled = true;
        string? available = _availableUpdate is null
            ? null
            : UpdateService.FormatVersion(_availableUpdate.Version);

        UpdateVersionButton.Tag = _updateDisplayState switch
        {
            UpdateDisplayState.Available => "available",
            UpdateDisplayState.Checking or
            UpdateDisplayState.Downloading or
            UpdateDisplayState.Installing => "busy",
            _ => "current",
        };
        UpdateStatusDot.Visibility = _updateDisplayState is
            UpdateDisplayState.Available or
            UpdateDisplayState.Downloading or
            UpdateDisplayState.Installing
                ? Visibility.Visible
                : Visibility.Collapsed;

        switch (_updateDisplayState)
        {
            case UpdateDisplayState.Checking:
                UpdateStatusText.Text =
                    $"v{current} · {Localization.Text("L.Update.Checking")}";
                UpdateVersionButton.ToolTip =
                    Localization.Text("L.Update.CheckingTip");
                break;
            case UpdateDisplayState.Available:
                UpdateStatusText.Text = Localization.Format(
                    "L.Update.Available",
                    available ?? current);
                UpdateVersionButton.ToolTip = Localization.Format(
                    "L.Update.AvailableTip",
                    available ?? current);
                break;
            case UpdateDisplayState.Downloading:
                UpdateStatusText.Text = Localization.Format(
                    "L.Update.Downloading",
                    _updateProgress);
                UpdateVersionButton.ToolTip = Localization.Format(
                    "L.Update.DownloadingTip",
                    available ?? current);
                break;
            case UpdateDisplayState.Installing:
                UpdateStatusText.Text =
                    Localization.Text("L.Update.Installing");
                UpdateVersionButton.ToolTip =
                    Localization.Text("L.Update.InstallingTip");
                break;
            case UpdateDisplayState.CheckFailed:
                UpdateStatusText.Text = $"v{current}";
                UpdateVersionButton.ToolTip =
                    Localization.Text("L.Update.CheckFailedTip");
                break;
            default:
                UpdateStatusText.Text = $"v{current}";
                UpdateVersionButton.ToolTip = _updateCheckInProgress
                    ? Localization.Text("L.Update.CheckingTip")
                    : Localization.Format(
                        "L.Update.UpToDateTip",
                        current);
                break;
        }
    }

    private async Task LoadAutostartStateAsync()
    {
        bool enabled = await Autostart.IsEnabledAsync();
        _updatingUi = true;
        try
        {
            AutostartBox.IsChecked = enabled;
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void ShowSettings()
    {
        LoadSettingsControls();
        DashboardPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Collapsed;
        DoneButton.Visibility = Visibility.Visible;
        SetWindowHeight(Math.Min(720, SystemParameters.WorkArea.Height - 64));
        AnimateView(SettingsPanel);
    }

    private void CodecSummary_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsAt(CodecSettingsRow, CodecBox);

    private void FpsSummary_Click(object sender, RoutedEventArgs e)
    {
        RadioButton? selected = FpsOptions.Children.OfType<RadioButton>()
            .FirstOrDefault(button => button.IsChecked == true);
        OpenSettingsAt(FpsSettingsRow, selected);
    }

    private void OutputFolderSummary_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsAt(OutputFolderSettingsRow, BrowseOutputButton);

    private void OpenSettingsAt(FrameworkElement target, Control? focusTarget)
    {
        ShowSettings();
        SettingsScrollViewer.ScrollToTop();
        Dispatcher.BeginInvoke(() =>
        {
            target.UpdateLayout();
            Point position = target.TranslatePoint(new Point(0, 0), SettingsScrollViewer);
            SettingsScrollViewer.ScrollToVerticalOffset(
                Math.Max(0, SettingsScrollViewer.VerticalOffset + position.Y - 18));
            focusTarget?.Focus();
            AnimatePress(target);
        }, DispatcherPriority.Loaded);
    }

    private void ShowDashboard()
    {
        CancelHotkeyCapture();
        SettingsPanel.Visibility = Visibility.Collapsed;
        DashboardPanel.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        DoneButton.Visibility = Visibility.Collapsed;
        SetWindowHeight(DashboardHeight);
        UpdateRuntimeState(_runtimeActive);
        _ = RefreshReplayLibraryAsync();
        AnimateView(DashboardPanel);
    }

    private void SetWindowHeight(double height)
    {
        height = Math.Min(height, Math.Max(430, SystemParameters.WorkArea.Height - 32));
        double centerY = Top + ActualHeight / 2;
        Height = height;
        Top = Math.Clamp(centerY - height / 2, SystemParameters.WorkArea.Top + 16,
            SystemParameters.WorkArea.Bottom - height - 16);
    }

    private async void ReplayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        if (!TryBeginAction())
        {
            UpdateRuntimeState(_runtimeActive);
            return;
        }

        try
        {
            bool active = await _setReplayEnabled(ReplayToggle.IsChecked == true);
            UpdateRuntimeState(active);
            AnimatePress(ReplayToggle);
        }
        catch (Exception exception)
        {
            HandleUiActionError("Replay toggle", exception);
            UpdateRuntimeState(_runtimeActive);
        }
        finally
        {
            EndAction();
        }
    }

    private void SaveReplay_Click(object sender, RoutedEventArgs e)
    {
        AnimatePress(SaveReplayButton);
        _saveReplay();
    }

    private async void SourceChip_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        if (!TryBeginAction())
        {
            UpdateRuntimeState(_runtimeActive);
            return;
        }

        try
        {
            bool applied = await _setAudioSources(
                SystemSourceChip.IsChecked == true,
                MicSourceChip.IsChecked == true,
                _config.SystemAudioDeviceId,
                _config.MicrophoneDeviceId);
            UpdateRuntimeState(_runtimeActive);
            AnimatePress((FrameworkElement)sender);
            if (!applied)
                ShowError(
                    Localization.Text("L.Error.SourceTitle"),
                    Localization.Text("L.Error.AudioSourceMessage"));
        }
        catch (Exception exception)
        {
            HandleUiActionError("Audio source toggle", exception);
            UpdateRuntimeState(_runtimeActive);
        }
        finally
        {
            EndAction();
        }
    }

    private void AudioDeviceMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        bool isSystem = string.Equals(menu.Tag?.ToString(), "system", StringComparison.Ordinal);
        ComboBox deviceBox = isSystem ? AudioDeviceBox : MicDeviceBox;
        string selectedId = isSystem ? _config.SystemAudioDeviceId : _config.MicrophoneDeviceId;

        menu.Items.Clear();
        if (isSystem && _config.CaptureSource == "game")
        {
            menu.Items.Add(new MenuItem
            {
                Header = Localization.Text("L.Audio.GameCaptured"),
                IsEnabled = false,
                Style = (Style)FindResource("TrayMenuItem"),
            });
            return;
        }
        menu.Items.Add(new MenuItem
        {
            Header = Localization.Text(
                isSystem
                    ? "L.Audio.SystemDeviceHeader"
                    : "L.Audio.MicDeviceHeader"),
            IsEnabled = false,
            FontSize = 10,
            Foreground = FindBrush("TextMutedBrush"),
            Style = (Style)FindResource("TrayMenuItem"),
        });
        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });

        foreach (ComboBoxItem device in deviceBox.Items.OfType<ComboBoxItem>())
        {
            string id = device.Tag?.ToString() ?? "";
            bool selected = string.Equals(id, selectedId, StringComparison.Ordinal);
            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Text = device.Content?.ToString() ??
                           Localization.Text("L.Audio.UnknownDevice"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 290,
                },
                Icon = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = selected ? FindBrush("AccentBrush") : Brushes.Transparent,
                },
                Tag = new AudioDeviceSelection(isSystem, id),
                Style = (Style)FindResource("TrayMenuItem"),
            };
            item.Click += AudioDeviceMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private async void AudioDeviceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Tag is not AudioDeviceSelection selection)
            return;
        if (!TryBeginAction())
            return;

        try
        {
            string systemDeviceId = selection.IsSystem
                ? selection.Id
                : _config.SystemAudioDeviceId;
            string microphoneDeviceId = selection.IsSystem
                ? _config.MicrophoneDeviceId
                : selection.Id;
            bool applied = await _setAudioSources(
                SystemSourceChip.IsChecked == true,
                MicSourceChip.IsChecked == true,
                systemDeviceId,
                microphoneDeviceId);

            SelectByTag(
                selection.IsSystem ? AudioDeviceBox : MicDeviceBox,
                selection.IsSystem
                    ? _config.SystemAudioDeviceId
                    : _config.MicrophoneDeviceId);
            UpdateRuntimeState(_runtimeActive);
            if (!applied)
                ShowError(
                    Localization.Text("L.Error.DeviceTitle"),
                    Localization.Text("L.Error.AudioSourceMessage"));
        }
        catch (Exception exception)
        {
            HandleUiActionError("Audio device selection", exception);
            UpdateRuntimeState(_runtimeActive);
        }
        finally
        {
            EndAction();
        }
    }

    private void AudioToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingUi)
            UpdateAudioDeviceState();
    }

    private void UpdateAudioDeviceState()
    {
        bool game = GetSelectedTag(CaptureSourceBox, "desktop") == "game";
        AudioDeviceBox.IsEnabled = !game && SystemAudioBox.IsChecked == true;
        SystemVolumeRow.IsEnabled = SystemAudioBox.IsChecked == true;
        MicDeviceBox.IsEnabled = MicBox.IsChecked == true;
        MicVolumeRow.IsEnabled = MicBox.IsChecked == true;
        MicBoostRow.IsEnabled = MicBox.IsChecked == true;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _outputDirectory };
        if (dialog.ShowDialog() != true)
            return;

        _outputDirectory = dialog.FolderName;
        OutputDirText.Text = _outputDirectory;
        _ = RefreshDiskAsync();
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyCapture();
        _capturingHotkeyButton = (Button)sender;
        _capturingHotkeyButton.Content = Localization.Text("L.Hotkey.Press");
        _capturingHotkeyButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null)
        {
            if (e.Key == Key.Escape && LanguagePopup.IsOpen)
            {
                LanguagePopup.IsOpen = false;
                LanguageButton.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && DeleteConfirmOverlay.Visibility == Visibility.Visible)
            {
                CancelDeleteReplay();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible)
            {
                LoadSettingsControls();
                ShowDashboard();
                e.Handled = true;
            }
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }
        if (IsModifierKey(key))
            return;

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is < Key.F1 or > Key.F24)
        {
            _capturingHotkeyButton.Content =
                Localization.Text("L.Hotkey.AddModifier");
            return;
        }

        string hotkey = FormatHotkey(modifiers, key);
        bool captureSave = Equals(_capturingHotkeyButton.Tag, "save");
        string other = captureSave ? _pendingToggleHotkey : _pendingSaveHotkey;
        if (!HotkeyManager.AreDistinct(hotkey, other))
        {
            _capturingHotkeyButton.Content =
                Localization.Text("L.Hotkey.InUse");
            return;
        }

        if (captureSave)
            _pendingSaveHotkey = hotkey;
        else
            _pendingToggleHotkey = hotkey;
        _capturingHotkeyButton.Content = hotkey;
        _capturingHotkeyButton = null;
    }

    private void CancelHotkeyCapture()
    {
        if (_capturingHotkeyButton is null)
            return;

        bool captureSave = Equals(_capturingHotkeyButton.Tag, "save");
        _capturingHotkeyButton.Content = captureSave ? _pendingSaveHotkey : _pendingToggleHotkey;
        _capturingHotkeyButton = null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyCapture();
        if (!HotkeyManager.IsValid(_pendingSaveHotkey) ||
            !HotkeyManager.IsValid(_pendingToggleHotkey) ||
            !HotkeyManager.AreDistinct(_pendingSaveHotkey, _pendingToggleHotkey))
        {
            ShowError(
                Localization.Text("L.Error.HotkeysTitle"),
                Localization.Text("L.Error.HotkeysMessage"));
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputDirectory))
        {
            ShowError(
                Localization.Text("L.Error.FolderTitle"),
                Localization.Text("L.Error.FolderMessage"));
            return;
        }

        try
        {
            _ = Path.GetFullPath(_outputDirectory);
        }
        catch
        {
            ShowError(
                Localization.Text("L.Error.PathTitle"),
                Localization.Text("L.Error.PathMessage"));
            return;
        }

        bool separateAudioTracks =
            GetSelectedTag(AudioTrackModeBox, "mixed") == "separate";
        Config candidate = _config.Clone();
        candidate.ReplayEnabled = SettingsReplayToggle.IsChecked == true;
        candidate.ShowRecordingIndicator =
            RecordingIndicatorBox.IsChecked == true;
        candidate.RecordingIndicatorPosition = GetSelectedRadioTag(
            RecordingIndicatorPositionOptions,
            _config.RecordingIndicatorPosition);
        candidate.BufferSeconds =
            GetSelectedRadioInt(BufferOptions, _config.BufferSeconds);
        candidate.MaxReplaySizeMb = GetSelectedInt(ReplaySizeLimitBox, 0);
        candidate.CaptureSource = GetSelectedTag(CaptureSourceBox, "desktop");
        string selectedCodec = GetSelectedTag(CodecBox, _config.Codec);
        if (!_capabilities.Supports(selectedCodec))
        {
            ShowError(
                Localization.Text("L.Error.CodecTitle"),
                Localization.Text("L.Error.CodecMessage"));
            return;
        }
        if (!TryBeginAction())
            return;

        try
        {
            candidate.Codec = selectedCodec;
            candidate.BitrateMbps = GetSelectedInt(BitrateBox, _config.BitrateMbps);
            candidate.FrameRate = GetSelectedRadioInt(FpsOptions, _config.FrameRate);
            candidate.MonitorIndex = GetSelectedInt(MonitorBox, _config.MonitorIndex);
            candidate.RecordingResolution = GetSelectedTag(ResolutionBox, "source");
            candidate.CaptureSystemAudio = SystemAudioBox.IsChecked == true;
            candidate.SystemAudioVolume = (int)Math.Round(SystemVolumeSlider.Value);
            candidate.SystemAudioDeviceId = GetSelectedTag(AudioDeviceBox, "");
            candidate.CaptureMicrophone = MicBox.IsChecked == true;
            candidate.MicrophoneVolume = (int)Math.Round(MicVolumeSlider.Value);
            candidate.MicrophoneBoostDb = (int)Math.Round(MicBoostSlider.Value);
            candidate.MicrophoneDeviceId = GetSelectedTag(MicDeviceBox, "");
            candidate.AudioCodec = GetSelectedTag(AudioCodecBox, "aac");
            candidate.SeparateAudioTracks = separateAudioTracks;
            candidate.OutputDirectory = _outputDirectory;
            candidate.OrganizeReplaysByGame = OrganizeByGameBox.IsChecked == true;
            candidate.Hotkey = _pendingSaveHotkey;
            candidate.ToggleReplayHotkey = _pendingToggleHotkey;
            candidate.Normalize();

            if (!await _applySettings(
                    candidate,
                    AutostartBox.IsChecked == true))
                // Keep pending choices visible so the failing setting can be corrected.
                return;

            Applied = true;
            _ = RefreshDiskAsync();
            ShowDashboard();
        }
        catch (Exception exception)
        {
            Log.Write($"Apply settings UI failed: {exception}");
            ShowError(
                Localization.Text("L.Error.Attention"),
                exception.Message);
        }
        finally
        {
            EndAction();
        }
    }

    private bool TryBeginAction()
    {
        if (Interlocked.Exchange(ref _actionInProgress, 1) != 0)
            return false;

        ReplayToggle.IsEnabled = false;
        SystemSourceChip.IsEnabled = false;
        MicSourceChip.IsEnabled = false;
        SettingsReplayToggle.IsEnabled = false;
        DoneButton.IsEnabled = false;
        return true;
    }

    private void EndAction()
    {
        Interlocked.Exchange(ref _actionInProgress, 0);
        ReplayToggle.IsEnabled = true;
        SystemSourceChip.IsEnabled = true;
        MicSourceChip.IsEnabled = true;
        SettingsReplayToggle.IsEnabled = true;
        DoneButton.IsEnabled = true;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            HandleUiActionError("Background UI action", exception);
        }
    }

    private void HandleUiActionError(string operation, Exception exception)
    {
        Log.Write($"{operation} failed: {exception}");
        ShowError(
            Localization.Text("L.Error.Attention"),
            exception.Message);
    }

    private async Task RefreshDiskAsync()
    {
        if (Interlocked.Exchange(ref _diskRefreshInProgress, 1) != 0)
            return;

        string outputDirectory = _outputDirectory;
        try
        {
            DiskSnapshot snapshot = await Task.Run(
                () => ReadDiskSnapshot(outputDirectory),
                _lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested ||
                !string.Equals(outputDirectory, _outputDirectory, StringComparison.Ordinal))
            {
                return;
            }

            DiskSummaryText.Text = Localization.Format(
                "L.Storage.FreeOn",
                FormatBytes(snapshot.FreeBytes),
                snapshot.DriveName);
            DiskSummaryProgress.Value = snapshot.UsedPercent;
            DiskUsedText.Text = Localization.Format(
                "L.Storage.Used",
                FormatBytes(snapshot.UsedBytes));
            DiskFreeText.Text = Localization.Format(
                "L.Storage.Free",
                FormatBytes(snapshot.FreeBytes));
            DiskProgress.Value = snapshot.UsedPercent;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Write($"Disk-space query unavailable: {exception.Message}");
            DiskSummaryText.Text = Localization.Text("L.Storage.Unavailable");
            DiskSummaryProgress.Value = 0;
            DiskUsedText.Text = Localization.Text("L.Storage.NoData");
            DiskFreeText.Text = "";
            DiskProgress.Value = 0;
        }
        finally
        {
            Interlocked.Exchange(ref _diskRefreshInProgress, 0);
        }
    }

    public void NotifyReplaySaved(string path)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => NotifyReplaySaved(path));
            return;
        }

        _ = RefreshReplayLibraryAsync();
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(RefreshReplayLibraryAsync);

    private async Task RefreshReplayLibraryAsync()
    {
        if (Interlocked.Exchange(ref _libraryRefreshInProgress, 1) != 0)
            return;
        SetReplayLibraryState(loading: true);
        try
        {
            IReadOnlyList<ReplayClip> clips = await _replayLibrary.GetRecentAsync(
                _outputDirectory,
                int.MaxValue,
                _lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested)
                return;

            RecentReplaysList.ItemsSource = clips.Select(CreateReplayClipItem).ToArray();
            SetReplayLibraryState(empty: clips.Count == 0);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Replay library refresh failed: {exception}");
            RecentReplaysList.ItemsSource = null;
            SetReplayLibraryState(error: true);
        }
        finally
        {
            Interlocked.Exchange(ref _libraryRefreshInProgress, 0);
        }
    }

    private void SetReplayLibraryState(
        bool loading = false,
        bool empty = false,
        bool error = false)
    {
        ReplayLibraryLoading.Visibility = loading
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReplayLibraryEmptyText.Visibility = empty
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReplayLibraryErrorText.Visibility = error
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecentReplaysScrollViewer.Visibility = !loading && !empty && !error
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private ReplayClipItem CreateReplayClipItem(ReplayClip clip)
    {
        BitmapImage? thumbnail = null;
        if (clip.ThumbnailPath is not null && File.Exists(clip.ThumbnailPath))
        {
            try
            {
                thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.UriSource = new Uri(clip.ThumbnailPath);
                thumbnail.EndInit();
                thumbnail.Freeze();
            }
            catch (Exception exception)
            {
                Log.Write($"Replay thumbnail load failed: {exception.Message}");
            }
        }

        string saved = clip.SavedAt.ToString("g");
        string metadata = Localization.Format(
            "L.Library.Metadata",
            saved,
            FormatFileSize(clip.SizeBytes));
        if (!string.IsNullOrWhiteSpace(clip.Collection))
            metadata = $"{clip.Collection} · {metadata}";
        return new ReplayClipItem(
            clip,
            Path.GetFileNameWithoutExtension(clip.Name),
            metadata,
            FormatTimelineDuration(clip.Duration),
            thumbnail,
            clip.Duration > TimeSpan.FromMilliseconds(200));
    }

    private void RevealReplay_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReplayClipItem item)
            return;
        try
        {
            _replayLibrary.Reveal(_outputDirectory, item.Clip);
        }
        catch (Exception exception)
        {
            HandleUiActionError("Reveal replay", exception);
        }
    }

    private void TrimReplay_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReplayClipItem item || !item.CanTrim)
            return;
        var editor = new ClipEditorWindow(
            _replayLibrary,
            _outputDirectory,
            item.Clip,
            savedPath =>
            {
                Log.Write($"Trimmed replay saved: {savedPath}");
                _ = RefreshReplayLibraryAsync();
            })
        {
            Owner = this,
        };
        editor.ShowDialog();
    }

    private void RequestDeleteReplay_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReplayClipItem item)
            return;
        _pendingDeleteClip = item.Clip;
        DeleteConfirmFileText.Text = item.Clip.Name;
        DeleteConfirmOverlay.Visibility = Visibility.Visible;
        AnimateView(DeleteConfirmOverlay);
    }

    private void CancelDeleteReplay_Click(object sender, RoutedEventArgs e) =>
        CancelDeleteReplay();

    private void CancelDeleteReplay()
    {
        _pendingDeleteClip = null;
        DeleteConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmDeleteReplay_Click(object sender, RoutedEventArgs e)
    {
        ReplayClip? clip = _pendingDeleteClip;
        CancelDeleteReplay();
        if (clip is null)
            return;
        try
        {
            await Task.Run(
                () => _replayLibrary.DeleteToRecycleBin(_outputDirectory, clip),
                _lifetimeCts.Token);
            await RefreshReplayLibraryAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            HandleUiActionError("Delete replay", exception);
        }
    }

    private static DiskSnapshot ReadDiskSnapshot(string outputDirectory)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
        if (string.IsNullOrEmpty(root))
            throw new IOException("Could not resolve the target drive.");

        var drive = new DriveInfo(root);
        long total = drive.TotalSize;
        long free = drive.AvailableFreeSpace;
        long used = total - free;
        return new DiskSnapshot(
            drive.Name.TrimEnd('\\'),
            used,
            free,
            total == 0 ? 0 : used * 100d / total);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice();

    public void ShowError(string title, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowError(title, message));
            return;
        }

        NoticeTitleText.Text = title;
        NoticeMessageText.Text = message;
        NoticeBanner.Visibility = Visibility.Visible;
        NoticeBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        NoticeTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void HideNotice()
    {
        var fade = new DoubleAnimation(NoticeBanner.Opacity, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            NoticeBanner.Opacity = 0;
        };
        NoticeBanner.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateRecordingState(bool active)
    {
        if (_animatedRecordingState == active)
            return;

        _animatedRecordingState = active;
        StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusRingRotation.Angle = 0;
        StatusDot.Opacity = 1;
        if (!active)
            return;

        StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(5))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        StatusDot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.42, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    private static void AnimateView(FrameworkElement view)
    {
        var translate = new TranslateTransform(0, 8);
        view.RenderTransform = translate;
        view.Opacity = 0;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void AnimatePress(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = element.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        var animation = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt;

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");

        string keyName = key is >= Key.D0 and <= Key.D9
            ? ((int)key - (int)Key.D0).ToString()
            : key.ToString();
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static string FormatCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "H.265",
        "av1" => "AV1",
        _ => codec.ToUpperInvariant(),
    };

    private static string FormatResolution(string resolution) =>
        resolution.ToLowerInvariant() switch
        {
            "720p" => "720p",
            "1080p" => "1080p",
            "1440p" => "1440p",
            "2160p" => "4K",
            _ => Localization.Text("L.Video.SourceShort"),
        };

    private string LocalizedCaptureSource(string? activeCaptureSource)
    {
        if (!string.IsNullOrWhiteSpace(activeCaptureSource))
        {
            return activeCaptureSource;
        }

        return Localization.Text(
            _config.CaptureSource == "game"
                ? "L.Video.GameLower"
                : "L.Video.DesktopLower");
    }

    private enum UpdateDisplayState
    {
        Current,
        Checking,
        Available,
        Downloading,
        Installing,
        CheckFailed,
    }

    private sealed record AudioDeviceSelection(bool IsSystem, string Id);

    private sealed record DeviceListsSnapshot(
        IReadOnlyList<(string Id, string Name)> RenderDevices,
        IReadOnlyList<(string Id, string Name)> CaptureDevices,
        IReadOnlyList<CaptureInterop.MonitorInfo> Monitors);

    private sealed record DiskSnapshot(
        string DriveName,
        long UsedBytes,
        long FreeBytes,
        double UsedPercent);

    private static void SelectRadioByTag(Panel panel, string tag)
    {
        RadioButton? fallback = null;
        foreach (RadioButton button in panel.Children.OfType<RadioButton>())
        {
            fallback ??= button;
            if (button.Tag?.ToString() == tag)
            {
                button.IsChecked = true;
                return;
            }
        }
        if (fallback is not null)
            fallback.IsChecked = true;
    }

    private static int GetSelectedRadioInt(Panel panel, int fallback)
    {
        RadioButton? selected = panel.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true);
        return int.TryParse(selected?.Tag?.ToString(), out int value) ? value : fallback;
    }

    private static string GetSelectedRadioTag(Panel panel, string fallback)
    {
        RadioButton? selected = panel.Children
            .OfType<RadioButton>()
            .FirstOrDefault(button => button.IsChecked == true);
        return selected?.Tag?.ToString() ?? fallback;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string GetSelectedTag(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int GetSelectedInt(ComboBox box, int fallback) =>
        int.TryParse(GetSelectedTag(box, ""), out int value) ? value : fallback;

    private static string FormatDuration(int seconds) =>
        Localization.Format(
            seconds < 60 ? "L.Unit.Seconds" : "L.Unit.Minutes",
            seconds < 60 ? seconds : seconds / 60);

    private static string FormatBytes(long bytes)
    {
        double gigabytes = bytes / 1024d / 1024d / 1024d;
        string value = gigabytes >= 100
            ? gigabytes.ToString("0")
            : gigabytes.ToString("0.0");
        return Localization.Format("L.Unit.GB", value);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatTimelineDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

    private sealed record ReplayClipItem(
        ReplayClip Clip,
        string Title,
        string Details,
        string Duration,
        BitmapImage? Thumbnail,
        bool CanTrim);

    private Brush FindBrush(string key) => (Brush)FindResource(key);
}
