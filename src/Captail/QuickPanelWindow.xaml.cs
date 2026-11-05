using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Captail.Interop;

namespace Captail;

public partial class QuickPanelWindow : Window
{
    private readonly Config _config;
    private readonly Func<bool> _isRunning;
    private readonly Func<string, bool> _supportsCodec;
    private readonly Func<Config, Task<bool>> _applyConfig;
    private readonly Func<int, Task<bool>> _setMicBoost;
    private readonly Func<bool, Task<bool>> _setEnabled;
    private readonly Func<bool, bool, string, string, Task<bool>> _setAudio;
    private readonly Action _saveReplay;
    private readonly Action _openSettings;
    private readonly Func<Task> _openFolder;
    private readonly ReplayLibrary _library = new(new FfmpegAdapter());
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _boostApplyTimer;
    private IReadOnlyList<CaptureInterop.MonitorInfo> _monitorChoices = [];
    private readonly TranslateTransform _slide = new();
    private AudioPeakReader? _peakReader;
    private float _displayedSystemPeak;
    private float _displayedMicPeak;
    private ReplayClip? _lastClip;
    private bool _busy;
    private bool _closing;
    private bool _syncingControls;
    private bool _syncingBoost;
    private bool _boostApplying;
    private bool _meterFailureLogged;
    private bool _dialogOpen;
    private nint _foregroundWindow;
    private bool _escapeWasDown;
    private bool _mouseWasDown;
    private long _animationSequence;

    public QuickPanelWindow(
        Config config,
        Func<bool> isRunning,
        Func<string, bool> supportsCodec,
        Func<Config, Task<bool>> applyConfig,
        Func<int, Task<bool>> setMicBoost,
        Func<bool, Task<bool>> setEnabled,
        Func<bool, bool, string, string, Task<bool>> setAudio,
        Action saveReplay,
        Action openSettings,
        Func<Task> openFolder)
    {
        _config = config;
        _isRunning = isRunning;
        _supportsCodec = supportsCodec;
        _applyConfig = applyConfig;
        _setMicBoost = setMicBoost;
        _setEnabled = setEnabled;
        _setAudio = setAudio;
        _saveReplay = saveReplay;
        _openSettings = openSettings;
        _openFolder = openFolder;
        InitializeComponent();
        // Save must remain clickable without moving keyboard focus out of a game.
        SaveReplayButton.Focusable = false;
        SourceInitialized += (_, _) => ConfigureNativeActivation();
        foreach (ComboBox box in new[] { SystemAudioBox, MicrophoneBox, FrameRateBox,
                     CodecBox, ResolutionBox, MonitorBox, SourceBox })
            box.AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(Selector_PreviewKeyDown), handledEventsToo: true);
        PanelFrame.RenderTransform = _slide;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            Refresh();
            EnsureTopmost();
        };
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += (_, _) =>
        {
            UpdateAudioMeters();
            CheckForegroundInput();
        };
        _boostApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _boostApplyTimer.Tick += async (_, _) => await ApplyMicBoostAsync();
        Localization.Changed += Refresh;
        Deactivated += (_, _) =>
        {
            if (_dialogOpen || AnySelectorOpen())
                return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (IsVisible && !IsActive && !_dialogOpen && !AnySelectorOpen())
                {
                    Log.Event(DiagnosticLevel.Info, "UI", "quick-panel.auto-hide",
                        new { reason = "deactivated" });
                    HideAnimated();
                }
            });
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _meterTimer.Stop();
            _boostApplyTimer.Stop();
            ReleasePeakReader();
            Localization.Changed -= Refresh;
        };
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains(
            "--qa-quick-panel-meters", StringComparer.OrdinalIgnoreCase))
        {
            SystemAudioSection.IsExpanded = true;
            MicrophoneSection.IsExpanded = true;
        }
#endif
    }

    public void TogglePanel()
    {
        if (IsVisible && !_closing)
        {
            HideAnimated();
            return;
        }

        _closing = false;
        long sequence = ++_animationSequence;
        nint foreground = GetForegroundWindow();
        _foregroundWindow = foreground;
        _escapeWasDown = GetAsyncKeyState(0x1B) < 0;
        _mouseWasDown = GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0;
        Opacity = 0;
        if (!IsVisible)
            Show();
        PlaceOnForegroundMonitor(foreground);
        EnsureTopmost();
        Refresh();
        _ = RefreshLastClipAsync();
        _refreshTimer.Start();
        _meterTimer.Start();
        UpdateAudioMeters();
        double offset = _config.QuickPanelSide == "left" ? -Width : Width;
        _slide.X = offset;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(190)));
        var slide = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(290))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        slide.Completed += (_, _) =>
        {
            if (sequence == _animationSequence)
                _slide.X = 0;
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    public void Refresh()
    {
        bool running = _isRunning();
        bool recording = _config.CaptureMode == "recording";
        ReplayModeButton.IsChecked = !recording;
        RecordingModeButton.IsChecked = recording;
        CaptureToggle.IsChecked = running;
        ReplayCaptureRow.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecordingActionButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordingActionButton.IsEnabled = !_busy;
        RecordingActionText.Text = Localization.Text(
            running ? "L.Capture.StopRecording" : "L.Capture.StartRecording");
        RecordingActionHotkey.Text = _config.Hotkey;
        RecordingActionButton.SetValue(System.Windows.Automation.AutomationProperties.AcceleratorKeyProperty,
            _config.Hotkey);
        RecordingActionIcon.Data = (Geometry)FindResource(running ? "RecordingStopIcon" : "IconDownload");
        CaptureToggle.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            Localization.Text(recording ? "L.Capture.Recording" : "L.Capture.Replay"));
        CaptureDetail.Text = recording
            ? running
                ? Localization.Text("L.Status.Recording")
                : $"{Localization.Text("L.QuickPanel.Ready")} · {_config.Hotkey}"
            : running
                ? $"{Localization.Text("L.QuickPanel.On")} · {Localization.Format("L.Status.BufferLast", $"{_config.BufferSeconds / 60}:{_config.BufferSeconds % 60:00}")}"
                : Localization.Text("L.QuickPanel.Off");
        SaveReplayButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        SaveReplayButton.IsEnabled = !_busy && running && !recording;
        ReplayModeButton.IsEnabled = RecordingModeButton.IsEnabled = CaptureToggle.IsEnabled = !_busy;
        SystemAudioDetail.Text = _config.AudioRoutingMode == "advanced"
            ? Localization.Text("L.Audio.PerAppRouting")
            : !_config.CaptureSystemAudio
                ? Localization.Text("L.QuickPanel.Off")
                : DeviceName(isMicrophone: false, _config.SystemAudioDeviceId);
        MicrophoneDetail.Text = _config.CaptureMicrophone
            ? DeviceName(isMicrophone: true, _config.MicrophoneDeviceId)
            : Localization.Text("L.QuickPanel.Off");
        RecordingParametersDetail.Text =
            $"{_config.RecordingResolution} · {_config.FrameRate} FPS · {_config.Codec.ToUpperInvariant()}";
        FolderDetail.Text = _config.OutputDirectory;
        SourceDetail.Text = Localization.Text(_config.CaptureSource == "game"
            ? "L.Video.GameSource" : "L.Video.Desktop");

        try
        {
            DriveInfo drive = new(Path.GetPathRoot(_config.OutputDirectory)!);
            DiskDetail.Text = drive.IsReady
                ? Localization.Format("L.Storage.Free",
                    $"{drive.AvailableFreeSpace / 1073741824.0:0.#} GB")
                : Localization.Text("L.Storage.Unavailable");
        }
        catch (Exception)
        {
            DiskDetail.Text = Localization.Text("L.Storage.Unavailable");
        }

        var monitors = CaptureInterop.EnumerateMonitors();
        if (!_monitorChoices.SequenceEqual(monitors))
            PopulateMonitorChoices(monitors);
        CaptureInterop.MonitorInfo? monitor = monitors.FirstOrDefault(item => item.Index == _config.MonitorIndex);
        MonitorDetail.Text = monitor is null
            ? Localization.Text("L.Video.PrimaryMonitor")
            : Localization.Format("L.Video.MonitorFormat",
                monitor.Index + 1, monitor.Width, monitor.Height);

        bool configureSystemAudio = _config.AudioRoutingMode == "advanced" ||
                                    _config.CaptureSource == "game";
        SystemAudioSelector.Visibility = configureSystemAudio
            ? Visibility.Collapsed : Visibility.Visible;
        SystemAudioConfigure.Visibility = configureSystemAudio
            ? Visibility.Visible : Visibility.Collapsed;
        MicBoostRow.IsEnabled = _config.CaptureMicrophone;

        if (!_boostApplying && !_boostApplyTimer.IsEnabled)
        {
            _syncingBoost = true;
            try
            {
                MicBoostSlider.Value = _config.MicrophoneBoostDb;
            }
            finally
            {
                _syncingBoost = false;
            }
        }

        _syncingControls = true;
        try
        {
            SelectValue(FrameRateBox, _config.FrameRate.ToString());
            SelectValue(CodecBox, _config.Codec);
            SelectValue(ResolutionBox, _config.RecordingResolution);
            SelectValue(SourceBox, _config.CaptureSource);
            SelectValue(MonitorBox, _config.MonitorIndex.ToString());
            SelectValue(SystemAudioBox, !_config.CaptureSystemAudio ? "off" :
                string.IsNullOrEmpty(_config.SystemAudioDeviceId) ? "default" :
                _config.SystemAudioDeviceId);
            SelectValue(MicrophoneBox, !_config.CaptureMicrophone ? "off" :
                string.IsNullOrEmpty(_config.MicrophoneDeviceId) ? "default" :
                _config.MicrophoneDeviceId);
            foreach (ComboBoxItem item in CodecBox.Items)
                item.IsEnabled = _supportsCodec((string)item.Tag);
        }
        finally
        {
            _syncingControls = false;
        }
    }

    public void ClosePermanently()
    {
        _refreshTimer.Stop();
        _meterTimer.Stop();
        _boostApplyTimer.Stop();
        ReleasePeakReader();
        Close();
    }

    private static string DeviceName(bool isMicrophone, string id)
    {
        if (string.IsNullOrEmpty(id))
            return Localization.Text("L.Audio.DefaultWindows");
        return SafeDevices(isMicrophone).FirstOrDefault(item => item.Id == id).Name ??
               Localization.Text("L.Audio.UnknownDevice");
    }

    private static IReadOnlyList<(string Id, string Name)> SafeDevices(bool isMicrophone)
    {
        try
        {
            return isMicrophone
                ? AudioDevices.ListCaptureDevices()
                : AudioDevices.ListRenderDevices();
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel audio device list failed: {exception.Message}");
            return [];
        }
    }

    private void UpdateAudioMeters()
    {
        if (!IsVisible || (!SystemAudioSection.IsExpanded && !MicrophoneSection.IsExpanded))
            return;

        try
        {
            _peakReader ??= new AudioPeakReader();
            bool systemAvailable = _config.CaptureSystemAudio &&
                _config.AudioRoutingMode != "advanced" &&
                _config.CaptureSource != "game";
            if (SystemAudioSection.IsExpanded)
            {
                float? peak = systemAvailable
                    ? _peakReader.ReadRenderPeak(_config.SystemAudioDeviceId)
                    : null;
                RenderMeter(SystemMeterText, SystemMeterBar, peak,
                    ref _displayedSystemPeak, _config.SystemAudioVolume / 100f);
            }
            if (MicrophoneSection.IsExpanded)
            {
                float? peak = _config.CaptureMicrophone
                    ? _peakReader.ReadCapturePeak(_config.MicrophoneDeviceId)
                    : null;
                float gain = _config.MicrophoneVolume / 100f *
                    MathF.Pow(10f, _config.MicrophoneBoostDb / 20f);
                RenderMeter(MicMeterText, MicMeterBar, peak,
                    ref _displayedMicPeak, gain);
            }
        }
        catch (Exception exception)
        {
            if (!_meterFailureLogged)
            {
                Log.Write($"Quick panel audio meter unavailable: {exception.Message}");
                _meterFailureLogged = true;
            }
            ReleasePeakReader();
            RenderMeter(SystemMeterText, SystemMeterBar, null,
                ref _displayedSystemPeak, 1);
            RenderMeter(MicMeterText, MicMeterBar, null,
                ref _displayedMicPeak, 1);
        }
    }

    private static void RenderMeter(TextBlock text, ProgressBar bar, float? peak,
        ref float displayedPeak, float gain)
    {
        if (peak is null)
        {
            displayedPeak = 0;
            text.Text = "— dBFS";
            bar.Value = 0;
            return;
        }

        displayedPeak = Math.Max(Math.Clamp(peak.Value * gain, 0f, 1f),
            displayedPeak * 0.7f);
        if (displayedPeak < 0.0001f)
        {
            text.Text = "−∞ dBFS";
            bar.Value = 0;
            return;
        }

        double decibels = Math.Max(-60, 20 * Math.Log10(displayedPeak));
        text.Text = $"{decibels:0} dBFS";
        bar.Value = Math.Clamp((decibels + 60) / 60 * 100, 0, 100);
    }

    private void ReleasePeakReader()
    {
        _peakReader?.Dispose();
        _peakReader = null;
        _displayedSystemPeak = 0;
        _displayedMicPeak = 0;
    }

    private async Task RefreshLastClipAsync()
    {
        try
        {
            ReplayClip? clip = (await _library.GetRecentAsync(_config.OutputDirectory, 1))
                .FirstOrDefault();
            if (!IsVisible)
                return;
            _lastClip = clip;
            LastClipButton.IsEnabled = clip is not null;
            LastClipName.Text = clip is null
                ? Localization.Text("L.QuickPanel.NoClips")
                : Path.GetFileNameWithoutExtension(clip.Name);
            LastClipDetail.Text = clip is null ? "" :
                $"{clip.SavedAt:g} · {clip.Duration.ToString(@"mm\:ss")}";
            LastClipImage.Source = null;
            if (clip?.ThumbnailPath is { } thumbnail && File.Exists(thumbnail))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(thumbnail);
                bitmap.EndInit();
                LastClipImage.Source = bitmap;
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel last clip failed: {exception.Message}");
        }
    }

    private void PlaceOnForegroundMonitor(nint foreground)
    {
        nint monitorHandle = MonitorFromWindow(foreground, 2);
        CaptureInterop.MonitorInfo? monitor = CaptureInterop.EnumerateMonitors()
            .FirstOrDefault(item => item.Handle == monitorHandle);
        if (monitor is null)
        {
            Left = _config.QuickPanelSide == "left"
                ? SystemParameters.WorkArea.Left + 10
                : SystemParameters.WorkArea.Right - Width - 10;
            Top = SystemParameters.WorkArea.Top + 10;
            Height = Math.Min(810, SystemParameters.WorkArea.Height - 20);
            return;
        }

        Matrix pixelsToDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        Point start = pixelsToDip.Transform(new Point(monitor.Left, monitor.Top));
        Point end = pixelsToDip.Transform(new Point(
            monitor.Left + monitor.Width, monitor.Top + monitor.Height));
        Width = Math.Min(736, end.X - start.X - 20);
        Height = Math.Min(810, end.Y - start.Y - 20);
        Left = _config.QuickPanelSide == "left"
            ? start.X + 10
            : end.X - Width - 10;
        Top = start.Y + Math.Max(10, (end.Y - start.Y - Height) / 2);
    }

    private void EnsureTopmost()
    {
        if (!IsVisible)
            return;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            SetWindowPos(hwnd, new nint(-1), 0, 0, 0, 0, 0x0003 | 0x0010);
    }

    private void ConfigureNativeActivation()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessage);
        const int extendedStyle = -20;
        const int noActivate = 0x08000000;
        int styles = GetWindowLong(hwnd, extendedStyle);
        Marshal.SetLastPInvokeError(0);
        int previous = SetWindowLong(hwnd, extendedStyle, styles | noActivate);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            Log.Write($"Could not prevent quick panel activation: Win32 error {error}.");
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            // MA_NOACTIVATE preserves the click without deactivating fullscreen games.
            handled = true;
            return 3;
        }
        return 0;
    }

    private void CheckForegroundInput()
    {
        bool escapeDown = GetAsyncKeyState(0x1B) < 0;
        bool escapePressed = escapeDown && !_escapeWasDown;
        _escapeWasDown = escapeDown;
        bool mouseDown = GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0;
        bool mousePressed = mouseDown && !_mouseWasDown;
        _mouseWasDown = mouseDown;
        if (!IsVisible || _closing || _dialogOpen || AnySelectorOpen())
            return;

        nint foreground = GetForegroundWindow();
        nint panel = new WindowInteropHelper(this).Handle;
        bool outsideClick = mousePressed && GetCursorPos(out NativePoint cursor) &&
            GetWindowRect(panel, out NativeRect bounds) &&
            (cursor.X < bounds.Left || cursor.X >= bounds.Right ||
             cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom);
        // A panel shown without activation receives neither Escape nor Deactivated.
        if ((!IsActive && (escapePressed || outsideClick)) ||
            (foreground != 0 && foreground != panel && foreground != _foregroundWindow))
            HideAnimated();
    }

    private void HideAnimated()
    {
        if (!IsVisible || _closing)
            return;
        _closing = true;
        _refreshTimer.Stop();
        _meterTimer.Stop();
        ReleasePeakReader();
        long sequence = ++_animationSequence;
        double offset = _config.QuickPanelSide == "left" ? -Width : Width;
        var slide = new DoubleAnimation(0, offset, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        slide.Completed += (_, _) =>
        {
            if (sequence != _animationSequence)
                return;
            Hide();
            _closing = false;
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0,
            TimeSpan.FromMilliseconds(220)));
    }

    private async Task ApplyChangeAsync(Action<Config> change)
    {
        if (_busy)
            return;
        _busy = true;
        Refresh();
        try
        {
            Config candidate = _config.Clone();
            change(candidate);
            if (!await _applyConfig(candidate))
            {
                Log.Write("Quick panel setting change was rejected.");
                ShowActionError("L.QuickPanel.ChangeFailed");
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel setting change failed: {exception}");
            MessageBox.Show(this, exception.Message,
                Localization.Text("L.Error.Attention"));
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void CaptureMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string mode } || mode == _config.CaptureMode)
            return;
        await ApplyChangeAsync(candidate =>
        {
            candidate.CaptureMode = mode;
            candidate.ReplayEnabled = false;
        });
    }

    private async void CaptureToggle_Click(object sender, RoutedEventArgs e) =>
        await ChangeEnabledAsync(CaptureToggle.IsChecked == true);

    private async void RecordingAction_Click(object sender, RoutedEventArgs e) =>
        await ChangeEnabledAsync(!_isRunning());

    private async Task ChangeEnabledAsync(bool enabled)
    {
        if (_busy)
            return;
        _busy = true;
        Refresh();
        try
        {
            bool active = await _setEnabled(enabled);
            if (active != enabled)
                ShowActionError("L.QuickPanel.ChangeFailed");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void SaveReplay_Click(object sender, RoutedEventArgs e) => _saveReplay();

    private void Section_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender == SystemAudioSection)
            PopulateAudioChoices(SystemAudioBox, isMicrophone: false);
        else if (sender == MicrophoneSection)
            PopulateAudioChoices(MicrophoneBox, isMicrophone: true);
        else if (sender == MonitorSection)
            PopulateMonitorChoices();

        if (sender == SystemAudioSection || sender == MicrophoneSection)
            UpdateAudioMeters();

        if (sender is Expander section)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                () => section.BringIntoView());
    }

    private void PopulateAudioChoices(ComboBox box, bool isMicrophone)
    {
        _syncingControls = true;
        try
        {
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem
            {
                Content = Localization.Text("L.QuickPanel.Off"), Tag = "off",
            });
            box.Items.Add(new ComboBoxItem
            {
                Content = Localization.Text("L.Audio.DefaultWindows"), Tag = "default",
            });
            foreach ((string id, string name) in SafeDevices(isMicrophone))
                box.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            bool enabled = isMicrophone ? _config.CaptureMicrophone : _config.CaptureSystemAudio;
            string deviceId = isMicrophone ? _config.MicrophoneDeviceId : _config.SystemAudioDeviceId;
            SelectValue(box, !enabled ? "off" :
                string.IsNullOrEmpty(deviceId) ? "default" : deviceId);
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private void PopulateMonitorChoices(IReadOnlyList<CaptureInterop.MonitorInfo>? monitors = null)
    {
        _syncingControls = true;
        try
        {
            MonitorBox.Items.Clear();
            _monitorChoices = monitors ?? CaptureInterop.EnumerateMonitors();
            foreach (CaptureInterop.MonitorInfo monitor in _monitorChoices)
                MonitorBox.Items.Add(new ComboBoxItem
                {
                    Content = Localization.Format("L.Video.MonitorFormat",
                        monitor.Index + 1, monitor.Width, monitor.Height),
                    Tag = monitor.Index.ToString(),
                });
            SelectValue(MonitorBox, _config.MonitorIndex.ToString());
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private static void SelectValue(ComboBox box, string value)
    {
        ComboBoxItem? match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value,
                StringComparison.OrdinalIgnoreCase));
        if (match is not null && box.SelectedItem != match)
            box.SelectedItem = match;
    }

    private bool AnySelectorOpen() =>
        SystemAudioBox.IsDropDownOpen || MicrophoneBox.IsDropDownOpen ||
        FrameRateBox.IsDropDownOpen || CodecBox.IsDropDownOpen ||
        ResolutionBox.IsDropDownOpen || MonitorBox.IsDropDownOpen ||
        SourceBox.IsDropDownOpen;

    private void Selector_DropDownClosed(object sender, EventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.Escape))
        {
            HideAnimated();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsVisible && !IsActive && !_dialogOpen && !AnySelectorOpen())
                HideAnimated();
        });
    }

    private async void SystemAudioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || SystemAudioBox.SelectedItem is not ComboBoxItem item)
            return;
        string value = (string)item.Tag;
        await ChangeAudioAsync(value != "off", _config.CaptureMicrophone,
            value is "off" or "default" ? "" : value, _config.MicrophoneDeviceId);
    }

    private async void MicrophoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || MicrophoneBox.SelectedItem is not ComboBoxItem item)
            return;
        string value = (string)item.Tag;
        await ChangeAudioAsync(_config.CaptureSystemAudio, value != "off",
            _config.SystemAudioDeviceId, value is "off" or "default" ? "" : value);
    }

    private void SystemAudioConfigure_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
        _openSettings();
    }

    private async Task ChangeAudioAsync(bool system, bool microphone, string systemId, string microphoneId)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            if (!await _setAudio(system, microphone, systemId, microphoneId))
                ShowActionError("L.Error.AudioSourceMessage");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void MicBoostSlider_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingBoost || _boostApplyTimer is null)
            return;
        _boostApplyTimer.Stop();
        _boostApplyTimer.Start();
    }

    private async void MicBoostSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        await ApplyMicBoostAsync();

    private async Task ApplyMicBoostAsync()
    {
        _boostApplyTimer.Stop();
        if (_boostApplying)
            return;
        if (_busy)
        {
            _boostApplyTimer.Start();
            return;
        }

        int desired = (int)Math.Round(MicBoostSlider.Value);
        if (desired == _config.MicrophoneBoostDb)
            return;

        _boostApplying = true;
        try
        {
            _busy = true;
            Refresh();
            if (!await _setMicBoost(desired))
                ShowActionError("L.QuickPanel.ChangeFailed");
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel microphone boost failed: {exception}");
            ShowActionError("L.QuickPanel.ChangeFailed");
        }
        finally
        {
            _busy = false;
            _boostApplying = false;
            if ((int)Math.Round(MicBoostSlider.Value) != desired)
                _boostApplyTimer.Start();
            else
                Refresh();
        }
    }

    private async void FrameRateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || FrameRateBox.SelectedItem is not ComboBoxItem item)
            return;
        int value = int.Parse((string)item.Tag);
        await ApplyChangeAsync(candidate => candidate.FrameRate = value);
    }

    private async void CodecBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || CodecBox.SelectedItem is not ComboBoxItem item)
            return;
        string value = (string)item.Tag;
        await ApplyChangeAsync(candidate => candidate.Codec = value);
    }

    private async void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || ResolutionBox.SelectedItem is not ComboBoxItem item)
            return;
        string value = (string)item.Tag;
        await ApplyChangeAsync(candidate => candidate.RecordingResolution = value);
    }

    private async void MonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || MonitorBox.SelectedItem is not ComboBoxItem item)
            return;
        int value = int.Parse((string)item.Tag);
        await ApplyChangeAsync(candidate => candidate.MonitorIndex = value);
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || SourceBox.SelectedItem is not ComboBoxItem item)
            return;
        string value = (string)item.Tag;
        await ApplyChangeAsync(candidate => candidate.CaptureSource = value);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _openFolder();
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel folder open failed: {exception}");
        }
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try
        {
            var dialog = new OpenFolderDialog { InitialDirectory = _config.OutputDirectory };
            if (dialog.ShowDialog(this) == true)
                await ApplyChangeAsync(candidate => candidate.OutputDirectory = dialog.FolderName);
        }
        catch (Exception exception)
        {
            Log.Write($"Quick panel folder change failed: {exception}");
            MessageBox.Show(this, exception.Message,
                Localization.Text("L.Error.Attention"));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void LastClip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClip is not { } clip)
            return;
        Hide();
        _refreshTimer.Stop();
        _meterTimer.Stop();
        ReleasePeakReader();
        var player = new ClipEditorWindow(_library, _config.OutputDirectory, clip,
            savedPath => { _ = RefreshLastClipAsync(); }, ClipWindowMode.Preview,
            _config.PlayerVolume);
        player.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
        _openSettings();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideAnimated();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        foreach (ComboBox box in new[] { SystemAudioBox, MicrophoneBox, FrameRateBox,
                     CodecBox, ResolutionBox, MonitorBox, SourceBox })
            box.IsDropDownOpen = false;
        HideAnimated();
        e.Handled = true;
    }

    private void Selector_PreviewKeyDown(object sender, KeyEventArgs e) =>
        Window_PreviewKeyDown(sender, e);

    private void ShowActionError(string messageKey) =>
        MessageBox.Show(this, Localization.Text(messageKey),
            Localization.Text("L.Error.Attention"));

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
