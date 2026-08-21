using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Captail;

public enum ClipWindowMode
{
    Trim,
    Preview,
}

public partial class ClipEditorWindow : Window
{
    private const double MinimumSelectionSeconds = 0.25;
    private const int TimelineFrameCount = 12;
    private static readonly double[] PlaybackSpeeds =
        [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    private static readonly TimeSpan FullscreenControlsTimeout = TimeSpan.FromSeconds(2.4);
    private const double FullscreenControlsHeight = 58;
    private readonly ReplayLibrary _library;
    private readonly string _rootDirectory;
    private readonly ReplayClip _clip;
    private readonly Action<string> _onSaved;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _fullscreenUiTimer;
    private readonly DispatcherTimer _speedFeedbackTimer;
    private readonly List<BitmapImage> _timelineImages = [];
    private double _selectionStart;
    private double _selectionEnd;
    private double _playbackPosition;
    private bool _playing;
    private bool _playerLoading;
    private bool _resumeAfterScrub;
    private bool _fullscreenProgressScrubbing;
    private bool _updatingFullscreenProgress;
    private bool _previewProgressScrubbing;
    private bool _updatingPreviewProgress;
    private bool _resumeAfterOverwriteConfirmation;
    private bool _saveInProgress;
    private Visibility _playerVisibilityBeforeOverwrite = Visibility.Collapsed;
    private Visibility _imageVisibilityBeforeOverwrite = Visibility.Visible;
    private bool _isFullscreen;
    private bool _restoreTopmost;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;
    private NativePoint _lastCursorPosition;
    private DateTime _lastPointerActivityUtc;
    private VideoStreamInfo? _videoInfo;
    private bool _previewMode;
    private bool _editorAssetsStarted;
    private int _playbackSpeedIndex = 3;

    public ObservableCollection<AudioTrackRow> AudioTracks { get; } = [];

    public ClipEditorWindow(
        ReplayLibrary library,
        string rootDirectory,
        ReplayClip clip,
        Action<string> onSaved,
        ClipWindowMode mode = ClipWindowMode.Trim)
    {
        _library = library;
        _rootDirectory = rootDirectory;
        _clip = clip;
        _onSaved = onSaved;
        _previewMode = mode == ClipWindowMode.Preview;
        _selectionEnd = Math.Max(MinimumSelectionSeconds, clip.Duration.TotalSeconds);
        InitializeComponent();
        DataContext = this;
        ClipNameText.Text = clip.Name;
        ApplyWindowModeLayout(adjustWindow: true);
        if (clip.ThumbnailPath is not null && File.Exists(clip.ThumbnailPath))
            PreviewImage.Source = LoadBitmap(clip.ThumbnailPath, 900);
        UpdateRangeText();
        UpdateTimelineVisual();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += async (_, _) => await UpdatePlaybackAsync();
        _fullscreenUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fullscreenUiTimer.Tick += (_, _) => UpdateFullscreenControls();
        _speedFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.25) };
        _speedFeedbackTimer.Tick += (_, _) => HidePlaybackSpeedFeedback();
        Loaded += async (_, _) => await LoadEditorAsync();
        SourceInitialized += (_, _) => ApplyNativeCornerPreference();
        Closed += (_, _) =>
        {
            _playbackTimer.Stop();
            _fullscreenUiTimer.Stop();
            _speedFeedbackTimer.Stop();
            Topmost = _restoreTopmost;
            _lifetimeCts.Cancel();
            PreviewPlayer.Shutdown();
            _lifetimeCts.Dispose();
        };
    }

    private async Task LoadEditorAsync()
    {
        Task videoInfoTask = LoadVideoInfoAsync();
        Task timelineTask = _previewMode
            ? Task.CompletedTask
            : LoadTimelineThumbnailsAsync();
        _editorAssetsStarted = !_previewMode;
        await LoadAudioTracksAsync(loadWaveforms: !_previewMode);
        await InitializePreviewAsync();
        if (_previewMode && PreviewPlayer.IsReady)
            await StartPlaybackAsync(_selectionStart);
        await Task.WhenAll(timelineTask, videoInfoTask);
    }

    private void ApplyWindowModeLayout(bool adjustWindow)
    {
        HeaderTrimIcon.Visibility = _previewMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        HeaderPreviewIcon.Visibility = _previewMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        HeaderTitleText.Text = Localization.Text(
            _previewMode ? "L.Library.PreviewTitle" : "L.Library.TrimTitle");
        Title = HeaderTitleText.Text;

        TimelineEditorPanel.Visibility = _previewMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        EditorActionsPanel.Visibility = _previewMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewModePanel.Visibility = _previewMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        NormalPlaybackBar.Visibility = _previewMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        TimelineRow.Height = _previewMode
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        ActionsRow.Height = _previewMode ? new GridLength(0) : GridLength.Auto;

        if (!adjustWindow)
            return;
        double targetHeight = _previewMode ? 720 : 790;
        if (IsLoaded)
        {
            double delta = targetHeight - ActualHeight;
            Rect workArea = SystemParameters.WorkArea;
            Top = Math.Clamp(Top - delta / 2, workArea.Top, workArea.Bottom - targetHeight);
        }
        Height = targetHeight;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            EditorWorkspace.UpdateLayout();
            PreviewPlayer.InvalidateVisual();
        });
    }

    private void EnterTrimMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewMode)
            return;
        _previewMode = false;
        ApplyWindowModeLayout(adjustWindow: true);
        BeginEditorAssetsLoad();
    }

    private void BeginEditorAssetsLoad()
    {
        if (_editorAssetsStarted)
            return;
        _editorAssetsStarted = true;
        _ = LoadEditorAssetsAsync();
    }

    private async Task LoadEditorAssetsAsync()
    {
        await Task.WhenAll(
            LoadTimelineThumbnailsAsync(),
            Task.WhenAll(AudioTracks.Select(LoadWaveformAsync)));
    }

    private async Task LoadVideoInfoAsync()
    {
        try
        {
            _videoInfo = await _library.GetVideoInfoAsync(
                _rootDirectory,
                _clip,
                _lifetimeCts.Token);
            UpdateClipInfoText();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Video metadata inspection failed: {exception.Message}");
            UpdateClipInfoText();
        }
    }

#if DEBUG
    internal async Task<(bool Passed, string Details)> RunPreviewGeometryQaAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        DateTime readyDeadline = DateTime.UtcNow.AddSeconds(10);
        while ((_playerLoading || !PreviewPlayer.IsReady) &&
               DateTime.UtcNow < readyDeadline)
        {
            await Task.Delay(50, _lifetimeCts.Token);
        }
        if (!PreviewPlayer.IsReady)
            return (false, "preview player did not become ready");

        RequestOverwrite_Click(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool overwriteOpened =
            OverwriteConfirmOverlay.Visibility == Visibility.Visible;
        bool playerSuppressed = PreviewPlayer.Visibility != Visibility.Visible;
        CancelOverwrite();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool playerRestored = PreviewPlayer.Visibility == Visibility.Visible;
        bool overwriteOverlayPassed =
            overwriteOpened && playerSuppressed && playerRestored;

        PreviewPlayer.Visibility = Visibility.Collapsed;
        SavingStatusText.Text = Localization.Text("L.Library.Trimming");
        ShowSavingOverlay();
        await Task.Delay(220, _lifetimeCts.Token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        string savingScreenshot = Path.Combine(
            Path.GetTempPath(),
            "Captail",
            "saving-overlay-qa.png");
        CaptureVisualToPng(savingScreenshot);
        bool savingOverlayPassed =
            SavingOverlay.Visibility == Visibility.Visible &&
            PreviewPlayer.Visibility != Visibility.Visible &&
            TextOptions.GetTextRenderingMode(SavingOverlay) ==
                TextRenderingMode.Grayscale;
        HideSavingOverlay();
        PreviewPlayer.Visibility = Visibility.Visible;

        await StartPlaybackAsync(_selectionStart);
        double playbackStart = PreviewPlayer.PositionSeconds;
        double playbackAfter = playbackStart;
        DateTime playbackDeadline = DateTime.UtcNow.AddSeconds(3);
        while (playbackAfter < playbackStart + 0.2 &&
               DateTime.UtcNow < playbackDeadline)
        {
            await Task.Delay(100, _lifetimeCts.Token);
            playbackAfter = PreviewPlayer.PositionSeconds;
        }
        bool clockAdvanced = playbackAfter >= playbackStart + 0.2;

        PreviewPlayer.Pause();
        _playing = false;
        _playbackTimer.Stop();
        double seekTarget = Math.Clamp(
            _clip.Duration.TotalSeconds * 0.5,
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(seekTarget, exact: true);
        await Task.Delay(350, _lifetimeCts.Token);
        double seekPosition = PreviewPlayer.PositionSeconds;
        bool seekPassed = Math.Abs(seekPosition - seekTarget) <= 1.0;

        int[] allTrackIds = AudioTracks
            .Select(track => track.Track.Ordinal + 1)
            .ToArray();
        if (allTrackIds.Length > 0)
            PreviewPlayer.SetAudioTracks([allTrackIds[0]]);
        if (allTrackIds.Length > 1)
            PreviewPlayer.SetAudioTracks(allTrackIds);
        bool tracksPassed = PreviewPlayer.IsReady &&
                            PreviewPlayer.DetectedAudioTrackCount == allTrackIds.Length;

        string geometry = "preview window is not ready";
        bool geometryPassed = PreviewPlayer.TryValidateGeometry(out geometry);
        string videoOutput = "video output is not ready";
        bool videoOutputPassed = PreviewPlayer.TryValidateVideoOutput(out videoOutput);
        await PreviewPlayer.StopAsync(_lifetimeCts.Token);
        bool stopPassed = !PreviewPlayer.IsReady;
        bool passed = overwriteOverlayPassed && savingOverlayPassed &&
                      seekPassed && tracksPassed &&
                      geometryPassed && videoOutputPassed && stopPassed;
        string details =
            $"overlay={(overwriteOverlayPassed ? "clear" : "occluded")}, " +
            $"saving={(savingOverlayPassed ? "visible" : "hidden")}, " +
            $"savingScreenshot={savingScreenshot}, " +
            $"{geometry}, {videoOutput}, " +
            $"clock={playbackStart:0.000}->{playbackAfter:0.000}" +
            $"{(clockAdvanced ? "" : " (startup pending)")}, " +
            $"seek={seekPosition:0.000}/{seekTarget:0.000}, " +
            $"tracks={PreviewPlayer.DetectedAudioTrackCount}/{allTrackIds.Length}, " +
            $"stop={(stopPassed ? "released" : "busy")}";
        return (passed, details);
    }

    private void CaptureVisualToPng(string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
#endif

    private async Task LoadTimelineThumbnailsAsync()
    {
        try
        {
            IReadOnlyList<string> paths = await _library.GetTimelineThumbnailsAsync(
                _rootDirectory,
                _clip,
                TimelineFrameCount,
                _lifetimeCts.Token);
            _timelineImages.Clear();
            _timelineImages.AddRange(paths.Select(path => LoadBitmap(path, 240)));
            TimelineFrames.ItemsSource = _timelineImages;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Timeline thumbnail generation failed: {exception.Message}");
        }
    }

    private async Task LoadAudioTracksAsync(bool loadWaveforms = true)
    {
        try
        {
            IReadOnlyList<AudioTrackInfo> tracks = await _library.GetAudioTracksAsync(
                _rootDirectory,
                _clip,
                _lifetimeCts.Token);
            AudioTracks.Clear();
            foreach (AudioTrackInfo track in tracks)
            {
                AudioTracks.Add(new AudioTrackRow(
                    track,
                    AudioLabel(track, tracks.Count)));
            }
            NoAudioText.Visibility = tracks.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateMergeAudioState();

            if (loadWaveforms)
            {
                foreach (AudioTrackRow row in AudioTracks)
                    _ = LoadWaveformAsync(row);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Audio track inspection failed: {exception.Message}");
            NoAudioText.Visibility = Visibility.Visible;
            MergeAudioCheckBox.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadWaveformAsync(AudioTrackRow row)
    {
        try
        {
            string? path = await _library.GetAudioWaveformAsync(
                _rootDirectory,
                _clip,
                row.Track,
                _lifetimeCts.Token);
            if (path is not null && File.Exists(path) && !_lifetimeCts.IsCancellationRequested)
                row.Waveform = LoadBitmap(path, 1200);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Audio waveform generation failed: {exception.Message}");
        }
    }

    private async Task InitializePreviewAsync()
    {
        if (_playerLoading || _lifetimeCts.IsCancellationRequested)
            return;
        _playerLoading = true;
        PlayButton.IsEnabled = false;
        try
        {
            PreviewLoadingOverlay.Visibility = Visibility.Visible;
            PreviewPlayer.Visibility = Visibility.Visible;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            PreviewPlayer.Visibility = Visibility.Collapsed;
            await PreviewPlayer.LoadAsync(
                _clip.Path,
                TimeSpan.FromSeconds(_playbackPosition),
                SelectedAudioTrackIds(),
                _lifetimeCts.Token);
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewPlayer.Visibility = Visibility.Visible;
            _playbackPosition = PreviewPlayer.PositionSeconds;
            EditorStatusText.Text = "";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            PreviewPlayer.Visibility = Visibility.Collapsed;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            Log.Write($"Clip preview failed ({_clip.Name}): {exception}");
            EditorStatusText.Text = exception.Message;
        }
        finally
        {
            _playerLoading = false;
            PlayButton.IsEnabled =
                !_lifetimeCts.IsCancellationRequested && PreviewPlayer.IsReady;
            PreviewPlayButton.IsEnabled = PlayButton.IsEnabled;
            UpdatePlayIcon();
            UpdatePlaybackText();
            UpdateTimelineVisual();
        }
    }

    private async Task StartPlaybackAsync(double position)
    {
        if (_playerLoading || _lifetimeCts.IsCancellationRequested)
            return;
        if (!PreviewPlayer.IsReady)
            await InitializePreviewAsync();
        if (!PreviewPlayer.IsReady)
            return;

        _playbackPosition = Math.Clamp(position, _selectionStart, _selectionEnd);
        PreviewPlayer.SetAudioTracks(SelectedAudioTrackIds());
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        PreviewPlayer.Play();
        _playing = true;
        _playbackTimer.Start();
        EditorStatusText.Text = "";
        UpdatePreviewLoadingState();
        UpdatePlayIcon();
        UpdatePlaybackText();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playerLoading)
            return;
        if (_playing)
        {
            await PausePlaybackAsync();
            return;
        }
        double start = CurrentPlaybackPosition() >= _selectionEnd - 0.05
            ? _selectionStart
            : CurrentPlaybackPosition();
        await StartPlaybackAsync(start);
    }

    private Task UpdatePlaybackAsync()
    {
        double position = CurrentPlaybackPosition();
        if (position >= _selectionEnd || !PreviewPlayer.IsReady)
        {
            PauseNativePlayback();
            _playbackPosition = _selectionStart;
            if (PreviewPlayer.IsReady)
                PreviewPlayer.Seek(_selectionStart, exact: true);
            UpdatePlayIcon();
            UpdatePlaybackText();
            UpdateTimelineVisual();
            return Task.CompletedTask;
        }
        UpdatePlaybackText();
        UpdateTimelineVisual();
        UpdatePreviewLoadingState();
        return Task.CompletedTask;
    }

    private double CurrentPlaybackPosition()
    {
        if (PreviewPlayer.IsReady)
            _playbackPosition = PreviewPlayer.PositionSeconds;
        return Math.Clamp(_playbackPosition, _selectionStart, _selectionEnd);
    }

    private Task PausePlaybackAsync()
    {
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        PauseNativePlayback();
        UpdatePlayIcon();
        UpdatePlaybackText();
        UpdateTimelineVisual();
        return Task.CompletedTask;
    }

    private void PauseNativePlayback()
    {
        _playing = false;
        _playbackTimer.Stop();
        PreviewPlayer.Pause();
        UpdatePreviewLoadingState();
    }

    private void UpdatePreviewLoadingState()
    {
        if (_playerLoading || !PreviewPlayer.IsReady)
            return;
        bool buffering = _playing && PreviewPlayer.IsBuffering;
        PreviewLoadingOverlay.Visibility = buffering
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewPlayer.Visibility = buffering
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void PauseForTimelineEdit()
    {
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        PauseNativePlayback();
        UpdatePlayIcon();
    }

    private void StartThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PauseForTimelineEdit();
        _selectionStart = Math.Clamp(
            _selectionStart + PixelsToSeconds(e.HorizontalChange),
            0,
            _selectionEnd - MinimumSelectionSeconds);
        _playbackPosition = _selectionStart;
        PreviewPlayer.Seek(_playbackPosition, exact: false);
        UpdateRangeText();
        UpdateTimelineVisual();
    }

    private void EndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PauseForTimelineEdit();
        _selectionEnd = Math.Clamp(
            _selectionEnd + PixelsToSeconds(e.HorizontalChange),
            _selectionStart + MinimumSelectionSeconds,
            Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds));
        if (_playbackPosition > _selectionEnd)
            _playbackPosition = _selectionEnd;
        PreviewPlayer.Seek(_playbackPosition, exact: false);
        UpdateRangeText();
        UpdateTimelineVisual();
    }

    private void RangeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _playbackPosition = sender == StartThumb ? _selectionStart : _selectionEnd;
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        UpdatePlaybackText();
        UpdateTimelineVisual();
    }

    private void PlayheadThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resumeAfterScrub = _playing;
        PauseForTimelineEdit();
    }

    private void PlayheadThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _playbackPosition = Math.Clamp(
            _playbackPosition + PixelsToSeconds(e.HorizontalChange),
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: false);
        UpdatePlaybackText();
        UpdateTimelineVisual();
    }

    private void PlayheadThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        bool resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdatePlayIcon();
        }
    }

    private void RangeTimeline_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;
        bool resume = _playing;
        PauseForTimelineEdit();
        double fraction = Math.Clamp(
            e.GetPosition(RangeTimeline).X / Math.Max(1, RangeTimeline.ActualWidth),
            0,
            1);
        _playbackPosition = Math.Clamp(
            fraction * Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds),
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        UpdatePlaybackText();
        UpdateTimelineVisual();
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdatePlayIcon();
        }
        e.Handled = true;
    }

    private async void Back_Click(object sender, RoutedEventArgs e) =>
        await SeekAsync(CurrentPlaybackPosition() - 5);

    private async void Forward_Click(object sender, RoutedEventArgs e) =>
        await SeekAsync(CurrentPlaybackPosition() + 5);

    private Task SeekAsync(double position)
    {
        bool resume = _playing;
        PauseForTimelineEdit();
        _playbackPosition = Math.Clamp(position, _selectionStart, _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        UpdatePlaybackText(readPlayerPosition: false);
        UpdateTimelineVisual();
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdatePlayIcon();
        }
        return Task.CompletedTask;
    }

    private void FullscreenProgress_DragStarted(
        object sender,
        DragStartedEventArgs e)
    {
        ShowFullscreenControls();
        _fullscreenProgressScrubbing = true;
        _resumeAfterScrub = _playing;
        PauseForTimelineEdit();
    }

    private void FullscreenProgress_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFullscreenProgress || !PreviewPlayer.IsReady)
            return;
        if (!_fullscreenProgressScrubbing)
        {
            _ = SeekAsync(e.NewValue);
            ShowFullscreenControls();
            return;
        }
        _playbackPosition = Math.Clamp(
            e.NewValue,
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: false);
        UpdatePlaybackText(readPlayerPosition: false);
        UpdateTimelineVisual();
        ShowFullscreenControls();
    }

    private void FullscreenProgress_DragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        if (!_fullscreenProgressScrubbing)
            return;
        bool resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        _fullscreenProgressScrubbing = false;
        _playbackPosition = Math.Clamp(
            FullscreenProgressSlider.Value,
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        UpdatePlaybackText(readPlayerPosition: false);
        UpdateTimelineVisual();
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdatePlayIcon();
        }
        ShowFullscreenControls();
    }

    private void FullscreenProgress_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        ShowFullscreenControls();
    }

    private void PreviewProgress_DragStarted(
        object sender,
        DragStartedEventArgs e)
    {
        _previewProgressScrubbing = true;
        _resumeAfterScrub = _playing;
        PauseForTimelineEdit();
    }

    private void PreviewProgress_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPreviewProgress || !PreviewPlayer.IsReady)
            return;
        if (!_previewProgressScrubbing)
        {
            _ = SeekAsync(e.NewValue);
            return;
        }
        _playbackPosition = Math.Clamp(e.NewValue, _selectionStart, _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: false);
        UpdatePlaybackText(readPlayerPosition: false);
        UpdateTimelineVisual();
    }

    private void PreviewProgress_DragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        if (!_previewProgressScrubbing)
            return;
        bool resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        _previewProgressScrubbing = false;
        _playbackPosition = Math.Clamp(
            PreviewProgressSlider.Value,
            _selectionStart,
            _selectionEnd);
        PreviewPlayer.Seek(_playbackPosition, exact: true);
        UpdatePlaybackText(readPlayerPosition: false);
        UpdateTimelineVisual();
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdatePlayIcon();
        }
    }

    private void PreviewProgress_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        Focus();
    }

    private void AudioTrackToggle_Click(object sender, RoutedEventArgs e)
    {
        UpdateMergeAudioState();
        if (_playerLoading || !PreviewPlayer.IsReady)
            return;
        PreviewPlayer.SetAudioTracks(SelectedAudioTrackIds());
    }

    private void RangeTimeline_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTimelineVisual();

    private double PixelsToSeconds(double pixels) =>
        pixels / Math.Max(1, RangeTimeline.ActualWidth) *
        Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds);

    private void UpdateTimelineVisual()
    {
        if (RangeTimeline is null || StartThumb is null || EndThumb is null)
            return;
        double duration = Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds);
        double width = Math.Max(1, RangeTimeline.ActualWidth);
        double handleWidth = StartThumb.Width;
        double startEdge = _selectionStart / duration * width;
        double endEdge = _selectionEnd / duration * width;

        double handleLimit = Math.Max(0, width - handleWidth);
        Canvas.SetLeft(StartThumb, Math.Clamp(startEdge - handleWidth / 2, 0, handleLimit));
        Canvas.SetLeft(EndThumb, Math.Clamp(endEdge - handleWidth / 2, 0, handleLimit));
        Canvas.SetLeft(LeftShade, 0);
        LeftShade.Width = Math.Max(0, startEdge);
        Canvas.SetLeft(RightShade, endEdge);
        RightShade.Width = Math.Max(0, width - endEdge);
        Canvas.SetLeft(SelectionBorder, startEdge);
        SelectionBorder.Width = Math.Max(0, endEdge - startEdge);

        double playhead = CurrentPlaybackPosition() / duration * width;
        double playheadLimit = Math.Max(0, width - PlayheadThumb.Width);
        Canvas.SetLeft(
            PlayheadThumb,
            Math.Clamp(playhead - PlayheadThumb.Width / 2, 0, playheadLimit));
    }

    private void UpdateRangeText()
    {
        if (StartTimeText is null || EndTimeText is null)
            return;
        StartTimeText.Text = FormatTime(TimeSpan.FromSeconds(_selectionStart), true);
        EndTimeText.Text = FormatTime(TimeSpan.FromSeconds(_selectionEnd), true);
        RangeDurationText.Text = Localization.Format(
            "L.Library.RangeSummary",
            FormatTime(TimeSpan.FromSeconds(_selectionEnd - _selectionStart), false));
        UpdateClipInfoText();
        UpdatePlaybackText();
    }

    private void UpdateClipInfoText()
    {
        if (ClipInfoText is null)
            return;
        double sourceSeconds = Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds);
        double selectedSeconds = Math.Clamp(
            _selectionEnd - _selectionStart,
            MinimumSelectionSeconds,
            sourceSeconds);
        long estimatedBytes = Math.Max(
            1,
            (long)Math.Round(_clip.SizeBytes * selectedSeconds / sourceSeconds));
        string resolution = _videoInfo is { Width: > 0, Height: > 0 }
            ? $"{_videoInfo.Width}×{_videoInfo.Height}"
            : "—";
        string frameRate = _videoInfo is { FrameRate: > 0 }
            ? _videoInfo.FrameRate.ToString(
                Math.Abs(_videoInfo.FrameRate - Math.Round(_videoInfo.FrameRate)) < 0.01
                    ? "0"
                    : "0.##",
                System.Globalization.CultureInfo.InvariantCulture)
            : "—";
        string codec = FormatVideoCodec(_videoInfo?.Codec);
        ClipInfoText.Text =
            $"≈{FormatFileSize(estimatedBytes)} / {FormatFileSize(_clip.SizeBytes)} · " +
            $"{resolution} · {frameRate} FPS · {codec}";
    }

    private static string FormatVideoCodec(string? codec) =>
        codec?.ToLowerInvariant() switch
        {
            "av1" => "AV1",
            "h264" => "H.264",
            "hevc" or "h265" => "HEVC",
            "vp9" => "VP9",
            "vp8" => "VP8",
            null or "" => "—",
            _ => codec.ToUpperInvariant(),
        };

    private static string FormatFileSize(long bytes)
    {
        const double megabyte = 1024d * 1024;
        const double gigabyte = 1024d * 1024 * 1024;
        return bytes >= gigabyte
            ? $"{bytes / gigabyte:0.##} GB"
            : $"{bytes / megabyte:0.#} MB";
    }

    private void UpdatePlaybackText(bool readPlayerPosition = true)
    {
        if (PlaybackTimeText is null)
            return;
        double position = readPlayerPosition
            ? CurrentPlaybackPosition()
            : _playbackPosition;
        PlaybackTimeText.Text =
            $"{FormatTime(TimeSpan.FromSeconds(position), false)} / " +
            FormatTime(_clip.Duration, false);
        if (FullscreenPlaybackTimeText is not null)
            FullscreenPlaybackTimeText.Text = PlaybackTimeText.Text;
        if (PreviewPlaybackTimeText is not null)
            PreviewPlaybackTimeText.Text = PlaybackTimeText.Text;
        if (FullscreenProgressSlider is not null && !_fullscreenProgressScrubbing)
        {
            _updatingFullscreenProgress = true;
            try
            {
                FullscreenProgressSlider.Minimum = _selectionStart;
                FullscreenProgressSlider.Maximum = _selectionEnd;
                FullscreenProgressSlider.Value = Math.Clamp(
                    position,
                    _selectionStart,
                    _selectionEnd);
            }
            finally
            {
                _updatingFullscreenProgress = false;
            }
        }
        if (PreviewProgressSlider is not null && !_previewProgressScrubbing)
        {
            _updatingPreviewProgress = true;
            try
            {
                PreviewProgressSlider.Minimum = _selectionStart;
                PreviewProgressSlider.Maximum = _selectionEnd;
                PreviewProgressSlider.Value = Math.Clamp(
                    position,
                    _selectionStart,
                    _selectionEnd);
            }
            finally
            {
                _updatingPreviewProgress = false;
            }
        }
    }

    private void UpdatePlayIcon()
    {
        if (PlayIcon is null)
            return;
        Geometry icon = (Geometry)FindResource(_playing ? "IconPause" : "IconPlay");
        PlayIcon.Data = icon;
        if (PreviewPlayIcon is not null)
            PreviewPlayIcon.Data = icon;
        if (FullscreenPlayIcon is not null)
            FullscreenPlayIcon.Data = icon;
    }

    private IReadOnlyList<int> SelectedAudioStreamIndices() =>
        AudioTracks
            .Where(track => track.IsSelected)
            .Select(track => track.Track.StreamIndex)
            .ToArray();

    private IReadOnlyList<int> SelectedAudioTrackIds() =>
        AudioTracks
            .Where(track => track.IsSelected)
            .Select(track => track.Track.Ordinal + 1)
            .ToArray();

    private void UpdateMergeAudioState()
    {
        bool hasSeparateTracks = AudioTracks.Count > 1;
        MergeAudioCheckBox.Visibility = hasSeparateTracks
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool canMerge = hasSeparateTracks &&
            AudioTracks.Count(track => track.IsSelected) > 1;
        MergeAudioCheckBox.IsEnabled = canMerge;
        if (!canMerge)
            MergeAudioCheckBox.IsChecked = false;
    }

    private async void SaveTrim_Click(object sender, RoutedEventArgs e) =>
        await SaveTrimAsync(overwrite: false);

    private void RequestOverwrite_Click(object sender, RoutedEventArgs e)
    {
        if (OverwriteConfirmOverlay.Visibility == Visibility.Visible)
            return;
        _resumeAfterOverwriteConfirmation = _playing;
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        PauseNativePlayback();
        _playerVisibilityBeforeOverwrite = PreviewPlayer.Visibility;
        _imageVisibilityBeforeOverwrite = PreviewImage.Visibility;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        if (PreviewImage.Source is not null)
            PreviewImage.Visibility = Visibility.Visible;

        OverwriteMessageText.Text = Localization.Text(
            MergeAudioCheckBox.IsChecked == true
                ? "L.Library.OverwriteMergeMessage"
                : "L.Library.OverwriteMessage");
        OverwriteFileText.Text = _clip.Name;
        OverwriteConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void CancelOverwrite_Click(object sender, RoutedEventArgs e) =>
        CancelOverwrite();

    private void CancelOverwrite(bool resumePlayback = true)
    {
        OverwriteConfirmOverlay.Visibility = Visibility.Collapsed;
        PreviewPlayer.Visibility = _playerVisibilityBeforeOverwrite;
        PreviewImage.Visibility = _imageVisibilityBeforeOverwrite;
        bool resume = resumePlayback && _resumeAfterOverwriteConfirmation &&
                      PreviewPlayer.IsReady;
        _resumeAfterOverwriteConfirmation = false;
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
        }
        UpdatePlayIcon();
        UpdatePlaybackText(readPlayerPosition: false);
    }

    private async void ConfirmOverwrite_Click(object sender, RoutedEventArgs e)
    {
        CancelOverwrite(resumePlayback: false);
        await SaveTrimAsync(overwrite: true);
    }

    private async Task SaveTrimAsync(bool overwrite)
    {
        if (_saveInProgress)
            return;
        _saveInProgress = true;
        SaveTrimButton.IsEnabled = false;
        OverwriteButton.IsEnabled = false;
        MergeAudioCheckBox.IsEnabled = false;
        bool mergeAudioTracks = MergeAudioCheckBox.IsChecked == true;
        string savingStatus = Localization.Text(
            mergeAudioTracks
                ? "L.Library.TrimmingMerge"
                : "L.Library.Trimming");
        EditorStatusText.Text = savingStatus;
        SavingStatusText.Text = savingStatus;
        try
        {
            PauseNativePlayback();
            PreviewPlayer.Visibility = Visibility.Collapsed;
            ShowSavingOverlay();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await PreviewPlayer.StopAsync(_lifetimeCts.Token);
            TimeSpan start = TimeSpan.FromSeconds(_selectionStart);
            TimeSpan end = TimeSpan.FromSeconds(_selectionEnd);
            IReadOnlyList<int> audioStreams = SelectedAudioStreamIndices();
            string path = overwrite
                ? await _library.TrimOverwriteAsync(
                    _rootDirectory,
                    _clip,
                    start,
                    end,
                    audioStreams,
                    mergeAudioTracks,
                    _lifetimeCts.Token)
                : await _library.TrimAsync(
                    _rootDirectory,
                    _clip,
                    start,
                    end,
                    audioStreams,
                    mergeAudioTracks,
                    _lifetimeCts.Token);
            _onSaved(path);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Replay trim failed: {exception}");
            HideSavingOverlay();
            EditorStatusText.Text = IsSharingViolation(exception)
                ? Localization.Text("L.Library.FileInUse")
                : exception.Message;
            SaveTrimButton.IsEnabled = true;
            OverwriteButton.IsEnabled = true;
            UpdateMergeAudioState();
            await InitializePreviewAsync();
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    private void ShowSavingOverlay()
    {
        SavingBackdrop.BeginAnimation(OpacityProperty, null);
        SavingBackdrop.Opacity = 0;
        SavingOverlay.Visibility = Visibility.Visible;
        SavingBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void HideSavingOverlay()
    {
        SavingBackdrop.BeginAnimation(OpacityProperty, null);
        SavingBackdrop.Opacity = 0;
        SavingOverlay.Visibility = Visibility.Collapsed;
    }

    private static bool IsSharingViolation(Exception exception) =>
        exception is IOException &&
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static string AudioLabel(AudioTrackInfo track, int count)
    {
        string title = track.Title ?? "";
        if (title.Contains("microphone", StringComparison.OrdinalIgnoreCase) ||
            title.Contains(" mic", StringComparison.OrdinalIgnoreCase))
        {
            return Localization.Text("L.Library.MicrophoneTrack");
        }
        if (title.Contains("system", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("game", StringComparison.OrdinalIgnoreCase))
        {
            return Localization.Text("L.Library.SystemGameTrack");
        }
        if (count == 1)
            return Localization.Text("L.Library.MixedAudioTrack");
        if (track.Ordinal == 0)
            return Localization.Text("L.Library.SystemGameTrack");
        if (track.Ordinal == 1)
            return Localization.Text("L.Library.MicrophoneTrack");
        return Localization.Format("L.Library.AudioTrackNumber", track.Ordinal + 1);
    }

    private void EnterFullscreen_Click(object sender, RoutedEventArgs e) =>
        EnterFullscreen();

    private void ExitFullscreen_Click(object sender, RoutedEventArgs e) =>
        ExitFullscreen();

    private void EnterFullscreen()
    {
        if (_isFullscreen)
            return;

        HidePlaybackSpeedFeedback(immediate: true);

        _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        _restoreWindowState = WindowState;
        _restoreTopmost = Topmost;
        _isFullscreen = true;

        WindowState = WindowState.Normal;
        Rect monitor = CurrentMonitorBounds();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Left;
        Top = monitor.Top;
        Width = monitor.Width;
        Height = monitor.Height;
        Topmost = true;

        HeaderRow.Height = new GridLength(0);
        EditorHeader.Visibility = Visibility.Collapsed;
        EditorWorkspace.Margin = new Thickness(0);
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);
        PlaybackRow.Height = new GridLength(FullscreenControlsHeight);
        TimelineRow.Height = new GridLength(0);
        ActionsRow.Height = new GridLength(0);
        NormalPlaybackBar.Visibility = Visibility.Collapsed;
        WindowChrome.BorderThickness = new Thickness(0);
        WindowChrome.CornerRadius = new CornerRadius(0);
        PreviewBorder.BorderThickness = new Thickness(0);
        PreviewBorder.CornerRadius = new CornerRadius(0);

        _lastPointerActivityUtc = DateTime.UtcNow;
        if (GetCursorPos(out NativePoint cursor))
            _lastCursorPosition = cursor;
        ShowFullscreenControls();
        _fullscreenUiTimer.Start();
        RefreshFullscreenLayout();
        Focus();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
            return;

        HidePlaybackSpeedFeedback(immediate: true);

        _isFullscreen = false;
        _fullscreenUiTimer.Stop();
        FullscreenControlBar.BeginAnimation(OpacityProperty, null);
        FullscreenControlBar.Visibility = Visibility.Collapsed;
        FullscreenControlBar.Opacity = 0;

        HeaderRow.Height = new GridLength(56);
        EditorHeader.Visibility = Visibility.Visible;
        EditorWorkspace.Margin = new Thickness(20, 0, 20, 20);
        PreviewRow.Height = new GridLength(390);
        PlaybackRow.Height = GridLength.Auto;
        TimelineRow.Height = new GridLength(1, GridUnitType.Star);
        ActionsRow.Height = GridLength.Auto;
        NormalPlaybackBar.Visibility = Visibility.Visible;
        WindowChrome.BorderThickness = new Thickness(1);
        WindowChrome.CornerRadius = (CornerRadius)FindResource("RadiusWindow");
        PreviewBorder.BorderThickness = new Thickness(1);
        PreviewBorder.CornerRadius = new CornerRadius(12);

        Topmost = _restoreTopmost;
        WindowState = WindowState.Normal;
        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        WindowState = _restoreWindowState;
        ApplyWindowModeLayout(adjustWindow: false);
        EditorWorkspace.UpdateLayout();
        Focus();
    }

    private void FullscreenControls_MouseMove(object sender, MouseEventArgs e) =>
        ShowFullscreenControls();

    private void UpdateFullscreenControls()
    {
        if (!_isFullscreen)
            return;
        if (GetCursorPos(out NativePoint cursor) &&
            (cursor.X != _lastCursorPosition.X || cursor.Y != _lastCursorPosition.Y))
        {
            _lastCursorPosition = cursor;
            ShowFullscreenControls();
            return;
        }
        if (!FullscreenControlBar.IsMouseOver &&
            DateTime.UtcNow - _lastPointerActivityUtc >= FullscreenControlsTimeout)
        {
            HideFullscreenControls();
        }
    }

    private void ShowFullscreenControls()
    {
        if (!_isFullscreen)
            return;
        _lastPointerActivityUtc = DateTime.UtcNow;
        PlaybackRow.Height = new GridLength(FullscreenControlsHeight);
        FullscreenControlBar.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        FullscreenControlBar.BeginAnimation(OpacityProperty, animation);
        RefreshFullscreenLayout();
    }

    private void HideFullscreenControls()
    {
        if (!_isFullscreen || FullscreenControlBar.Visibility != Visibility.Visible)
            return;
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        animation.Completed += (_, _) =>
        {
            if (!_isFullscreen || FullscreenControlBar.IsMouseOver ||
                DateTime.UtcNow - _lastPointerActivityUtc < FullscreenControlsTimeout)
            {
                return;
            }
            FullscreenControlBar.Visibility = Visibility.Collapsed;
            PlaybackRow.Height = new GridLength(0);
            RefreshFullscreenLayout();
        };
        FullscreenControlBar.BeginAnimation(OpacityProperty, animation);
    }

    private void RefreshFullscreenLayout()
    {
        EditorWorkspace.UpdateLayout();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            EditorWorkspace.UpdateLayout();
            PreviewPlayer.InvalidateVisual();
        });
    }

    private Rect CurrentMonitorBounds()
    {
        nint window = new WindowInteropHelper(this).Handle;
        nint monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return SystemParameters.WorkArea;

        Matrix fromDevice = PresentationSource.FromVisual(this)?
                                .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_saveInProgress)
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape &&
            OverwriteConfirmOverlay.Visibility == Visibility.Visible)
        {
            CancelOverwrite();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.F)
        {
            if (_isFullscreen)
                ExitFullscreen();
            else
                EnterFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !e.IsRepeat)
        {
            ShowFullscreenControls();
            PlayPause_Click(PlayButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            ShowFullscreenControls();
            Back_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            ShowFullscreenControls();
            Forward_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ShowFullscreenControls();
            ChangePlaybackSpeed(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            ShowFullscreenControls();
            ChangePlaybackSpeed(-1);
            e.Handled = true;
        }
    }

    private void ChangePlaybackSpeed(int direction)
    {
        if (!PreviewPlayer.IsReady || direction == 0)
            return;

        int nextIndex = Math.Clamp(
            _playbackSpeedIndex + Math.Sign(direction),
            0,
            PlaybackSpeeds.Length - 1);
        if (nextIndex == _playbackSpeedIndex)
            return;

        _playbackSpeedIndex = nextIndex;
        double speed = PlaybackSpeeds[_playbackSpeedIndex];
        PreviewPlayer.SetPlaybackSpeed(speed);
        ShowPlaybackSpeedFeedback(speed);
    }

    private void ShowPlaybackSpeedFeedback(double speed)
    {
        string value = $"{speed:0.##}×";
        PreviewSpeedFeedbackText.Text = value;
        FullscreenSpeedFeedbackText.Text = value;

        Border visible = _isFullscreen
            ? FullscreenSpeedFeedback
            : PreviewSpeedFeedback;
        Border hidden = _isFullscreen
            ? PreviewSpeedFeedback
            : FullscreenSpeedFeedback;

        hidden.BeginAnimation(OpacityProperty, null);
        hidden.Visibility = Visibility.Collapsed;
        hidden.Opacity = 0;
        visible.BeginAnimation(OpacityProperty, null);
        visible.Visibility = Visibility.Visible;
        visible.Opacity = 1;

        _speedFeedbackTimer.Stop();
        _speedFeedbackTimer.Start();
    }

    private void HidePlaybackSpeedFeedback(bool immediate = false)
    {
        _speedFeedbackTimer.Stop();
        Border[] feedbackBadges = [PreviewSpeedFeedback, FullscreenSpeedFeedback];
        foreach (Border badge in feedbackBadges)
        {
            badge.BeginAnimation(OpacityProperty, null);
            if (badge.Visibility != Visibility.Visible)
                continue;
            if (immediate)
            {
                badge.Opacity = 0;
                badge.Visibility = Visibility.Collapsed;
                continue;
            }

            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            animation.Completed += (_, _) =>
            {
                badge.BeginAnimation(OpacityProperty, null);
                badge.Opacity = 0;
                badge.Visibility = Visibility.Collapsed;
            };
            badge.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyNativeCornerPreference()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            int preference = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Rounded corners are cosmetic and unavailable on older Windows builds.
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static BitmapImage LoadBitmap(string path, int decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodePixelWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FormatTime(TimeSpan time, bool milliseconds) =>
        milliseconds
            ? $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);
}

public sealed class AudioTrackRow : INotifyPropertyChanged
{
    private ImageSource? _waveform;
    private bool _isSelected = true;

    public AudioTrackRow(AudioTrackInfo track, string label)
    {
        Track = track;
        Label = label;
        Codec = track.Codec.ToUpperInvariant();
    }

    public AudioTrackInfo Track { get; }
    public string Label { get; }
    public string Codec { get; }

    public ImageSource? Waveform
    {
        get => _waveform;
        set
        {
            if (ReferenceEquals(_waveform, value))
                return;
            _waveform = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
