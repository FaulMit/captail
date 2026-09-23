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
    private const double TrimWindowBaseHeight = 790;
    private const double AudioTrackRowHeight = 48;
    private const int BaseVisibleAudioTracks = 1;
    private const int MaximumVisibleAudioTracks = 6;
    private static readonly TimeSpan BufferingIndicatorDelay =
        TimeSpan.FromMilliseconds(180);
    private static readonly double[] PlaybackSpeeds =
        [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    private static readonly TimeSpan FullscreenControlsTimeout = TimeSpan.FromSeconds(2.4);
    private const double FullscreenControlsHeight = 58;
    private readonly ReplayLibrary _library;
    private readonly string _rootDirectory;
    private ReplayClip _clip;
    private readonly Action<string> _onSaved;
    private readonly Action<int>? _onVolumeChanged;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _timelineLoadCts;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _fullscreenUiTimer;
    private readonly DispatcherTimer _speedFeedbackTimer;
    private readonly DispatcherTimer _volumePersistTimer;
    private readonly ObservableCollection<BitmapImage?> _timelineImages = [];
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
    private ResizeMode _restoreResizeMode;
    private nint _restoreWindowStyle;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;
    private NativePoint _lastCursorPosition;
    private DateTime _lastPointerActivityUtc;
    private VideoStreamInfo? _videoInfo;
    private bool _previewMode;
    private int _playbackSpeedIndex = 3;
    private int _playbackVolumePercent;
    private bool _updatingVolumeSlider;
    private bool _volumePersistPending;
    private DateTime? _bufferingSinceUtc;
    private int _currentReplayIndex = -1;
    private int _replayLoadVersion;
    private RecentReplayEntry[] _pendingDeleteReplays = [];
    private readonly List<RecentReplayEntry> _allReplayItems = [];
    private bool _updatingReplayFilters;
    private bool _deletingReplays;
    private bool _resumeAfterDeleteConfirmation;
    private Visibility _playerVisibilityBeforeDelete = Visibility.Collapsed;
    private Visibility _imageVisibilityBeforeDelete = Visibility.Visible;
    private HwndSource? _windowSource;
    private bool _interactiveResizeActive;
    private bool _interactiveResizePlayerVisible;
    private bool _resumeAfterInteractiveResize;
    private Visibility _imageVisibilityBeforeInteractiveResize = Visibility.Visible;
    private bool _outputSettingsInitialized;

    public ObservableCollection<AudioTrackRow> AudioTracks { get; } = [];
    public ObservableCollection<RecentReplayEntry> RecentReplayItems { get; } = [];
    public bool HasDeletedReplays { get; private set; }

    public ClipEditorWindow(
        ReplayLibrary library,
        string rootDirectory,
        ReplayClip clip,
        Action<string> onSaved,
        ClipWindowMode mode = ClipWindowMode.Trim,
        int initialVolumePercent = 100,
        Action<int>? onVolumeChanged = null)
    {
        _library = library;
        _rootDirectory = rootDirectory;
        _clip = clip;
        _onSaved = onSaved;
        _onVolumeChanged = onVolumeChanged;
        _previewMode = mode == ClipWindowMode.Preview;
        _playbackVolumePercent = Math.Clamp(initialVolumePercent, 0, 100);
        _selectionEnd = Math.Max(MinimumSelectionSeconds, clip.Duration.TotalSeconds);
        _updatingVolumeSlider = true;
        InitializeComponent();
        PreviewPlayer.NativeMouseWheel += PreviewPlayer_NativeMouseWheel;
        PreviewPlayer.NativeMouseLeftButton += PreviewPlayer_NativeMouseLeftButton;
        EditorVolumeSlider.Value = _playbackVolumePercent;
        PreviewVolumeSlider.Value = _playbackVolumePercent;
        _updatingVolumeSlider = false;
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
        _volumePersistTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _volumePersistTimer.Tick += (_, _) => PersistPlaybackVolumeNow();
        Loaded += async (_, _) => await LoadEditorAsync();
        SourceInitialized += Window_SourceInitialized;
        StateChanged += (_, _) => ClosePopupsWhenHidden();
        IsVisibleChanged += (_, _) => ClosePopupsWhenHidden();
        Closed += (_, _) =>
        {
            if (Owner is not null)
                Owner.StateChanged -= Owner_StateChanged;
            _playbackTimer.Stop();
            _fullscreenUiTimer.Stop();
            _speedFeedbackTimer.Stop();
            _volumePersistTimer.Stop();
            PersistPlaybackVolumeNow();
            Topmost = _restoreTopmost;
            _lifetimeCts.Cancel();
            PreviewPlayer.NativeMouseWheel -= PreviewPlayer_NativeMouseWheel;
            PreviewPlayer.NativeMouseLeftButton -= PreviewPlayer_NativeMouseLeftButton;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            PreviewPlayer.Shutdown();
            _lifetimeCts.Dispose();
        };
    }

    private async Task LoadEditorAsync()
    {
        Task recentTask = _previewMode
            ? LoadRecentReplaysAsync()
            : Task.CompletedTask;
        Task videoInfoTask = LoadVideoInfoAsync();
        _ = LoadTimelineThumbnailsAsync();
        await LoadAudioTracksAsync(loadWaveforms: true);
        await InitializePreviewAsync();
        if (_previewMode && PreviewPlayer.IsReady)
            await StartPlaybackAsync(_selectionStart);
        await videoInfoTask;
        InitializeOutputSettings();
        await recentTask;
    }

    private async Task LoadRecentReplaysAsync()
    {
        IReadOnlyList<ReplayClip> clips = await _library.GetRecentAsync(
            _rootDirectory,
            int.MaxValue,
            _lifetimeCts.Token);
        _allReplayItems.Clear();
        foreach (ReplayClip clip in clips)
        {
            BitmapImage? thumbnail = null;
            if (!string.IsNullOrWhiteSpace(clip.ThumbnailPath) &&
                File.Exists(clip.ThumbnailPath))
            {
                thumbnail = LoadBitmap(clip.ThumbnailPath, 180);
            }
            _allReplayItems.Add(new RecentReplayEntry(
                clip,
                Path.GetFileNameWithoutExtension(clip.Name),
                FormatTime(clip.Duration, false),
                thumbnail));
        }
        _updatingReplayFilters = true;
        GameFilter.Items.Clear();
        GameFilter.Items.Add(new ReplayFilterOption(null, Localization.Text("L.Library.AllGames")));
        foreach (string game in _allReplayItems.Select(item => item.Clip.Collection ?? "")
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(game => game))
            GameFilter.Items.Add(new ReplayFilterOption(game,
                game.Length == 0 ? Localization.Text("L.Library.NoGame") : game));
        GameFilter.SelectedIndex = 0;
        RecordingModeFilter.ItemsSource = new[]
        {
            new ReplayFilterOption(null, Localization.Text("L.Library.AllModes")),
            new ReplayFilterOption("replay", Localization.Text("L.Library.ModeReplay")),
            new ReplayFilterOption("recording", Localization.Text("L.Library.ModeRecording")),
            new ReplayFilterOption("unknown", Localization.Text("L.Library.ModeUnknown")),
        };
        RecordingModeFilter.SelectedIndex = 0;
        _updatingReplayFilters = false;
        ApplyReplayFilters();
    }

    private void ReplayFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingReplayFilters && IsLoaded)
            ApplyReplayFilters();
    }

    private void ApplyReplayFilters()
    {
        string? game = (GameFilter.SelectedItem as ReplayFilterOption)?.Value;
        string? mode = (RecordingModeFilter.SelectedItem as ReplayFilterOption)?.Value;
        RecentReplayItems.Clear();
        foreach (RecentReplayEntry item in _allReplayItems)
        {
            item.IsMarked = false;
            if ((game is null || string.Equals(item.Clip.Collection ?? "", game, StringComparison.OrdinalIgnoreCase)) &&
                (mode is null || item.Clip.RecordingMode == mode))
                RecentReplayItems.Add(item);
        }
        UpdateReplayNavigationState();
        UpdateMarkedReplayCount();
    }

    private void ReplayMarked_Click(object sender, RoutedEventArgs e) => UpdateMarkedReplayCount();

    private void SelectAllReplays_Click(object sender, RoutedEventArgs e)
    {
        bool mark = RecentReplayItems.Any(item => !item.IsMarked);
        foreach (RecentReplayEntry item in RecentReplayItems)
            item.IsMarked = mark;
        UpdateMarkedReplayCount();
    }

    private void UpdateMarkedReplayCount()
    {
        int count = RecentReplayItems.Count(item => item.IsMarked);
        DeleteSelectedReplaysButton.IsEnabled = count > 0 && !_deletingReplays;
        DeleteSelectedReplaysButton.Content = Localization.Format("L.Library.DeleteSelected", count);
    }

    private void DeleteSelectedReplays_Click(object sender, RoutedEventArgs e) =>
        RequestDeleteReplays(RecentReplayItems.Where(item => item.IsMarked).ToArray());

    private async Task LoadReplayAsync(ReplayClip clip)
    {
        if (_playerLoading ||
            string.Equals(clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int replayLoadVersion = ++_replayLoadVersion;
        _playerLoading = true;
        SetReplayNavigationButtonsEnabled(false, false);
        PlayButton.IsEnabled = false;
        PreviewPlayButton.IsEnabled = false;
        try
        {
            PreviewPlayer.Pause();
            _playbackTimer.Stop();
            _playing = false;
            _clip = clip;
            _selectionStart = 0;
            _selectionEnd = Math.Max(
                MinimumSelectionSeconds,
                clip.Duration.TotalSeconds);
            _playbackPosition = 0;
            _videoInfo = null;
            ClipNameText.Text = clip.Name;
            PreviewImage.Source = !string.IsNullOrWhiteSpace(clip.ThumbnailPath) &&
                                  File.Exists(clip.ThumbnailPath)
                ? LoadBitmap(clip.ThumbnailPath, 900)
                : null;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewLoadingOverlay.Visibility = Visibility.Visible;
            _timelineImages.Clear();
            TimelineFrames.ItemsSource = null;
            _outputSettingsInitialized = false;
            OutputSettingsButton.IsEnabled = false;
            OutputSettingsPopup.IsOpen = false;
            AudioTracks.Clear();
            await LoadAudioTracksAsync(loadWaveforms: true);
            await LoadPreviewPlayerAsync(clip, TimeSpan.Zero);
            PreviewPlayer.SetPlaybackSpeed(PlaybackSpeeds[_playbackSpeedIndex]);
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewPlayer.Visibility = Visibility.Visible;
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
            UpdateRangeText();
            UpdatePlayIcon();
            _ = CompleteReplayEnrichmentAsync(clip, replayLoadVersion);
        }
        catch (Exception exception)
        {
            Log.Write($"Replay navigation failed: {exception}");
            EditorStatusText.Text = exception.Message;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _playerLoading = false;
            PlayButton.IsEnabled = PreviewPlayer.IsReady;
            PreviewPlayButton.IsEnabled = PreviewPlayer.IsReady;
            UpdateReplayNavigationState();
        }
    }

    private async Task CompleteReplayEnrichmentAsync(
        ReplayClip clip,
        int replayLoadVersion)
    {
        Task videoInfoTask = LoadVideoInfoAsync(clip, replayLoadVersion);
        _ = LoadTimelineThumbnailsAsync(clip, replayLoadVersion);
        await videoInfoTask;
        if (IsCurrentReplayLoad(clip, replayLoadVersion))
            InitializeOutputSettings();
    }

    private bool IsCurrentReplayLoad(ReplayClip clip, int replayLoadVersion) =>
        replayLoadVersion == _replayLoadVersion &&
        string.Equals(clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase);

    private void UpdateReplayNavigationState()
    {
        foreach (RecentReplayEntry item in _allReplayItems)
        {
            item.IsActive = string.Equals(
                item.Clip.Path,
                _clip.Path,
                StringComparison.OrdinalIgnoreCase);
        }
        _currentReplayIndex = RecentReplayItems
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => pair.item.IsActive)
            .index;
        if (RecentReplayItems.Count == 0 ||
            !RecentReplayItems.Any(item => item.IsActive))
        {
            _currentReplayIndex = -1;
        }
        bool canNavigatePrevious = _currentReplayIndex > 0;
        bool canNavigateNext =
            _currentReplayIndex >= 0 &&
            _currentReplayIndex < RecentReplayItems.Count - 1;
        SetReplayNavigationButtonsEnabled(canNavigatePrevious, canNavigateNext);
        RecentReplayList.UnselectAll();
    }

    private void SetReplayNavigationButtonsEnabled(
        bool previousEnabled,
        bool nextEnabled)
    {
        PreviousReplayButton.IsEnabled = previousEnabled;
        EditorPreviousReplayButton.IsEnabled = previousEnabled;
        NextReplayButton.IsEnabled = nextEnabled;
        EditorNextReplayButton.IsEnabled = nextEnabled;
    }

    private async void PreviousReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_deletingReplays || _currentReplayIndex <= 0)
            return;
        await LoadReplayAsync(RecentReplayItems[_currentReplayIndex - 1].Clip);
    }

    private async void NextReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_deletingReplays || _currentReplayIndex < 0 ||
            _currentReplayIndex >= RecentReplayItems.Count - 1)
        {
            return;
        }
        await LoadReplayAsync(RecentReplayItems[_currentReplayIndex + 1].Clip);
    }

    private async void RecentReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_deletingReplays || DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
            return;
        if (((FrameworkElement)sender).DataContext is RecentReplayEntry entry)
        {
            if (string.Equals(entry.Clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (!_playing)
                {
                    double start = CurrentPlaybackPosition() >= _selectionEnd - 0.05
                        ? _selectionStart
                        : CurrentPlaybackPosition();
                    await StartPlaybackAsync(start);
                }
            }
            else
                await LoadReplayAsync(entry.Clip);
        }
    }

    private void RequestDeleteRecentReplay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is not RecentReplayEntry entry ||
            DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        RequestDeleteReplays([entry]);
    }

    private void RequestDeleteReplays(RecentReplayEntry[] entries)
    {
        if (entries.Length == 0 || _deletingReplays || _playerLoading ||
            DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
            return;
        _pendingDeleteReplays = entries;
        ReplayLibraryStatusText.Visibility = Visibility.Collapsed;
        _resumeAfterDeleteConfirmation = _playing;
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        PauseNativePlayback();
        _playerVisibilityBeforeDelete = PreviewPlayer.Visibility;
        _imageVisibilityBeforeDelete = PreviewImage.Visibility;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        if (PreviewImage.Source is not null)
            PreviewImage.Visibility = Visibility.Visible;
        DeleteRecentReplayFileText.Text = entries.Length == 1 ? entries[0].Clip.Name :
            Localization.Format("L.Library.SelectedCount", entries.Length);
        DeleteRecentReplayOverlay.Visibility = Visibility.Visible;
    }

    private void CancelDeleteRecentReplay_Click(object sender, RoutedEventArgs e) =>
        CancelDeleteRecentReplay();

    private void CancelDeleteRecentReplay(bool resumePlayback = true)
    {
        DeleteRecentReplayOverlay.Visibility = Visibility.Collapsed;
        PreviewPlayer.Visibility = _playerVisibilityBeforeDelete;
        PreviewImage.Visibility = _imageVisibilityBeforeDelete;
        bool resume = resumePlayback && _resumeAfterDeleteConfirmation &&
                      PreviewPlayer.IsReady;
        _pendingDeleteReplays = [];
        _resumeAfterDeleteConfirmation = false;
        if (resume)
        {
            PreviewPlayer.Play();
            _playing = true;
            _playbackTimer.Start();
        }
        UpdatePlayIcon();
        UpdatePlaybackText(readPlayerPosition: false);
    }

    private async void ConfirmDeleteRecentReplay_Click(object sender, RoutedEventArgs e) =>
        await ConfirmDeleteRecentReplaysAsync();

    private async Task ConfirmDeleteRecentReplaysAsync()
    {
        RecentReplayEntry[] entries = _pendingDeleteReplays;
        bool resumePlayback = _resumeAfterDeleteConfirmation;
        CancelDeleteRecentReplay(resumePlayback: false);
        if (entries.Length == 0 || _deletingReplays)
            return;

        bool deletingCurrent = entries.Any(entry => string.Equals(
            entry.Clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase));
        int deletedIndex = _currentReplayIndex;
        RecentReplayEntry? replacement = deletingCurrent
            ? RecentReplayItems
                .Where(item => !entries.Contains(item))
                .OrderBy(item => Math.Abs(RecentReplayItems.IndexOf(item) - deletedIndex))
                .FirstOrDefault() ?? _allReplayItems.FirstOrDefault(item => !entries.Contains(item))
            : null;

        try
        {
            _deletingReplays = true;
            RecentReplaySidebar.IsEnabled = false;
            if (deletingCurrent)
                await PreviewPlayer.StopAsync(_lifetimeCts.Token);
            foreach (RecentReplayEntry entry in entries)
            {
                await Task.Run(
                    () => _library.DeleteToRecycleBin(_rootDirectory, entry.Clip),
                    _lifetimeCts.Token);
                HasDeletedReplays = true;
                _allReplayItems.Remove(entry);
                RecentReplayItems.Remove(entry);
            }
            if (deletingCurrent)
            {
                if (replacement is null)
                {
                    Close();
                    return;
                }
                await LoadReplayAsync(replacement.Clip);
            }
            else
            {
                UpdateReplayNavigationState();
                if (resumePlayback && PreviewPlayer.IsReady)
                {
                    PreviewPlayer.Play();
                    _playing = true;
                    _playbackTimer.Start();
                    UpdatePlayIcon();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Replay delete failed: {exception}");
            EditorStatusText.Text = exception.Message;
            ReplayLibraryStatusText.Text = exception.Message;
            ReplayLibraryStatusText.Visibility = Visibility.Visible;
            if (deletingCurrent && File.Exists(_clip.Path))
            {
                await InitializePreviewAsync();
                if (resumePlayback && PreviewPlayer.IsReady)
                    await StartPlaybackAsync(_playbackPosition);
            }
            else if (deletingCurrent)
            {
                RecentReplayEntry? remaining = _allReplayItems.FirstOrDefault(item => File.Exists(item.Clip.Path));
                if (remaining is null)
                    Close();
                else
                    await LoadReplayAsync(remaining.Clip);
            }
            else if (resumePlayback && PreviewPlayer.IsReady)
            {
                PreviewPlayer.Play();
                _playing = true;
                _playbackTimer.Start();
                UpdatePlayIcon();
            }
        }
        finally
        {
            _deletingReplays = false;
            RecentReplaySidebar.IsEnabled = true;
            UpdateReplayNavigationState();
            UpdateMarkedReplayCount();
        }
    }

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangePlaybackVolume(e.Delta > 0 ? 5 : -5);
        ShowFullscreenControls();
        e.Handled = true;
    }

    private void PreviewPlayer_NativeMouseWheel(int delta)
    {
        ChangePlaybackVolume(delta > 0 ? 5 : -5);
        ShowFullscreenControls();
    }

    private async void PreviewSurface_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        await TogglePlaybackAsync();
        ShowFullscreenControls();
        e.Handled = true;
    }

    private async void PreviewPlayer_NativeMouseLeftButton()
    {
        await TogglePlaybackAsync();
        ShowFullscreenControls();
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

        TimelineEditorPanel.Visibility = Visibility.Visible;
        EditorActionsPanel.Visibility = Visibility.Visible;
        PreviewModePanel.Visibility = Visibility.Collapsed;
        RecentReplaySidebar.Visibility = _previewMode && !_isFullscreen
            ? Visibility.Visible
            : Visibility.Collapsed;
        NormalPlaybackBar.Visibility = Visibility.Visible;
        TimelineRow.Height = GridLength.Auto;
        ActionsRow.Height = GridLength.Auto;
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);
        UpdateAudioTrackViewport();

        if (!adjustWindow)
            return;
        int visibleAudioTracks = Math.Clamp(
            AudioTracks.Count,
            BaseVisibleAudioTracks,
            MaximumVisibleAudioTracks);
        double preferredHeight = TrimWindowBaseHeight +
                                 (visibleAudioTracks - BaseVisibleAudioTracks) *
                                 AudioTrackRowHeight;
        Rect workArea = IsLoaded ? CurrentMonitorWorkArea() : SystemParameters.WorkArea;
        double targetHeight = Math.Min(
            preferredHeight,
            Math.Max(560, workArea.Height - 16));
        if (IsLoaded)
        {
            double delta = targetHeight - ActualHeight;
            Top = Math.Clamp(
                Top - delta / 2,
                workArea.Top + 8,
                workArea.Bottom - targetHeight - 8);
        }
        Height = targetHeight;
        if (_previewMode)
        {
            double targetWidth = Math.Min(
                1120,
                Math.Max(760, workArea.Width - 16));
            Width = Math.Max(ActualWidth, targetWidth);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            EditorWorkspace.UpdateLayout();
            PreviewPlayer.InvalidateVisual();
        });
    }

    private async Task LoadVideoInfoAsync(
        ReplayClip? requestedClip = null,
        int? replayLoadVersion = null)
    {
        ReplayClip clip = requestedClip ?? _clip;
        try
        {
            VideoStreamInfo? videoInfo = await _library.GetVideoInfoAsync(
                _rootDirectory,
                clip,
                _lifetimeCts.Token);
            if (!string.Equals(clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase) ||
                (replayLoadVersion.HasValue &&
                 !IsCurrentReplayLoad(clip, replayLoadVersion.Value)))
            {
                return;
            }
            _videoInfo = videoInfo;
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
    internal async Task<(bool Passed, string Details)> RunResponsivePlayerQaAsync()
    {
        DateTime readyDeadline = DateTime.UtcNow.AddSeconds(12);
        while ((_playerLoading || AudioTracks.Count < 3) &&
               DateTime.UtcNow < readyDeadline)
        {
            await Task.Delay(50, _lifetimeCts.Token);
        }
        if (AudioTracks.Count < 3)
            return (false, $"expected three audio tracks, got {AudioTracks.Count}");

        async Task<(double Preview, double Audio, double TimelineBottom,
            double ActionsTop, double ActionsBottom, double Workspace,
            double ActionLeft, double ActionRight, double PlaybackOptionsLeft,
            double TransportRight, double WorkspaceWidth)> MeasureAsync(
            double width, double height)
        {
            Width = width;
            Height = height;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            EditorWorkspace.UpdateLayout();
            return (
                PreviewBorder.ActualHeight,
                AudioTrackScrollViewer.ActualHeight,
                TimelineEditorPanel.TranslatePoint(
                    new Point(0, TimelineEditorPanel.ActualHeight), EditorWorkspace).Y,
                EditorActionsPanel.TranslatePoint(new Point(), EditorWorkspace).Y,
                EditorActionsPanel.TranslatePoint(
                    new Point(0, EditorActionsPanel.ActualHeight), EditorWorkspace).Y,
                EditorWorkspace.ActualHeight,
                EditorActionButtons.TranslatePoint(new Point(), EditorWorkspace).X,
                EditorActionButtons.TranslatePoint(
                    new Point(EditorActionButtons.ActualWidth, 0), EditorWorkspace).X,
                EditorPlaybackOptions.TranslatePoint(new Point(), EditorWorkspace).X,
                EditorTransportControls.TranslatePoint(
                    new Point(EditorTransportControls.ActualWidth, 0), EditorWorkspace).X,
                EditorWorkspace.ActualWidth);
        }

        var compact = await MeasureAsync(760, 556);
        CaptureVisualToPng(Path.Combine(
            Path.GetTempPath(), "Captail", "responsive-player-compact-qa.png"));
        var normal = await MeasureAsync(1120, 790);
        var expanded = await MeasureAsync(1552, 1026);
        CaptureVisualToPng(Path.Combine(
            Path.GetTempPath(), "Captail", "responsive-player-expanded-qa.png"));
        bool compactFits = compact.Preview >= 150 &&
                           compact.TimelineBottom <= compact.ActionsTop + 1 &&
                           compact.ActionsBottom <= compact.Workspace + 1 &&
                           compact.Audio <= AudioTrackRowHeight + 1 &&
                           compact.ActionLeft >= -1 &&
                           compact.ActionRight <= compact.WorkspaceWidth + 1 &&
                           compact.PlaybackOptionsLeft >= compact.TransportRight - 1;
        bool grows = normal.Preview > compact.Preview + 100 &&
                     expanded.Preview > normal.Preview + 140;

        EnterFullscreen();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        ExitFullscreen();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool restored = PreviewRow.Height.IsStar && TimelineRow.Height.IsAuto &&
                        PreviewBorder.ActualHeight > normal.Preview + 140;
        bool passed = compactFits && grows && restored;
        string details =
            $"preview={compact.Preview:0}/{normal.Preview:0}/{expanded.Preview:0}, " +
            $"compactAudio={compact.Audio:0}, " +
            $"compactBounds={compact.TimelineBottom:0}/{compact.ActionsTop:0}/" +
            $"{compact.ActionsBottom:0}/{compact.Workspace:0}, " +
            $"compactActions={compact.ActionLeft:0}..{compact.ActionRight:0}/" +
            $"{compact.WorkspaceWidth:0}, " +
            $"compactPlayback={compact.TransportRight:0}/" +
            $"{compact.PlaybackOptionsLeft:0}, " +
            $"compactFits={compactFits}, grows={grows}, fullscreenRestore={restored}";
        return (passed, details);
    }

    internal async Task<(bool Passed, string Details)> RunReplayNavigationQaAsync()
    {
        DateTime readyDeadline = DateTime.UtcNow.AddSeconds(12);
        while ((_playerLoading || !PreviewPlayer.IsReady ||
                RecentReplayItems.Count < 2) &&
               DateTime.UtcNow < readyDeadline)
        {
            await Task.Delay(50, _lifetimeCts.Token);
        }

        RecentReplayEntry? target = RecentReplayItems.FirstOrDefault(item =>
            !string.Equals(
                item.Clip.Path,
                _clip.Path,
                StringComparison.OrdinalIgnoreCase));
        if (!PreviewPlayer.IsReady || target is null)
            return (false, "player or second replay did not become ready");

        bool allClipsPassed = RecentReplayItems.Count >= 13;
        bool initialVolumePassed = _playbackVolumePercent == 64 &&
                                   PreviewPlayer.VolumePercent == 64 &&
                                   Math.Abs(EditorVolumeSlider.Value - 64) < 0.1 &&
                                   Math.Abs(PreviewVolumeSlider.Value - 64) < 0.1;
        PreviewVolumeSlider.Value = 73;
        await Task.Delay(500, _lifetimeCts.Token);
        bool sliderVolumePassed = _playbackVolumePercent == 73 &&
                                  PreviewPlayer.VolumePercent == 73 &&
                                  Math.Abs(EditorVolumeSlider.Value - 73) < 0.1;

        int volumeBefore = PreviewPlayer.VolumePercent;
        PreviewPlayer.RaiseNativeMouseWheelForQa(-120);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool wheelPassed = PreviewPlayer.VolumePercent == volumeBefore - 5;

        ReplayClip initialClip = _clip;
        string initialPath = initialClip.Path;
        int initialReplayIndex = _currentReplayIndex;
        ReplayClip firstClip = RecentReplayItems[0].Clip;
        ReplayClip secondClip = RecentReplayItems[1].Clip;
        await LoadReplayAsync(secondClip);
        bool previousButtonEnabled = EditorPreviousReplayButton.IsEnabled;
        EditorPreviousReplayButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent, EditorPreviousReplayButton));
        DateTime previousDeadline = DateTime.UtcNow.AddSeconds(8);
        while ((_playerLoading ||
                !string.Equals(
                    _clip.Path,
                    firstClip.Path,
                    StringComparison.OrdinalIgnoreCase)) &&
               DateTime.UtcNow < previousDeadline)
        {
            await Task.Delay(50, _lifetimeCts.Token);
        }
        bool previousToFirstPassed = previousButtonEnabled &&
                                     string.Equals(
                                         _clip.Path,
                                         firstClip.Path,
                                         StringComparison.OrdinalIgnoreCase);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var firstContainer = (ListBoxItem?)RecentReplayList.ItemContainerGenerator
            .ContainerFromIndex(0);
        var initialContainer = initialReplayIndex >= 0
            ? (ListBoxItem?)RecentReplayList.ItemContainerGenerator
                .ContainerFromIndex(initialReplayIndex)
            : null;
        var firstSelectionBorder = firstContainer?.Template.FindName(
            "ReplayItemBorder",
            firstContainer) as Border;
        var initialSelectionBorder = initialContainer?.Template.FindName(
            "ReplayItemBorder",
            initialContainer) as Border;
        var activeBackground = (SolidColorBrush)FindResource("AccentSubtleBrush");
        var activeBorder = (SolidColorBrush)FindResource("AccentDimBrush");
        bool initialHighlightCleared = initialReplayIndex == 0 ||
                                       initialSelectionBorder is not
                                       {
                                           Background: SolidColorBrush initialBackground,
                                           BorderBrush: SolidColorBrush initialBorder,
                                       } ||
                                       initialBackground.Color != activeBackground.Color ||
                                       initialBorder.Color != activeBorder.Color;
        bool activeHighlightPassed =
            RecentReplayItems[0].IsActive &&
            RecentReplayItems.Count(item => item.IsActive) == 1 &&
            firstSelectionBorder is
            {
                Background: SolidColorBrush firstBackground,
                BorderBrush: SolidColorBrush firstBorder,
            } &&
            firstBackground.Color == activeBackground.Color &&
            firstBorder.Color == activeBorder.Color &&
            initialHighlightCleared;
        var firstClipButton = new Button
        {
            DataContext = RecentReplayItems[0],
        };
        RecentReplay_Click(firstClipButton, new RoutedEventArgs());
        await Task.Delay(150, _lifetimeCts.Token);
        bool activeFirstClickPassed = _playing &&
                                      string.Equals(
                                          _clip.Path,
                                          firstClip.Path,
                                          StringComparison.OrdinalIgnoreCase);

        bool switched = true;
        for (int index = 0; index < 6; index++)
        {
            ReplayClip requested = index % 2 == 0 ? target.Clip : initialClip;
            await LoadReplayAsync(requested);
            switched &= PreviewPlayer.IsReady &&
                        string.Equals(
                            _clip.Path,
                            requested.Path,
                            StringComparison.OrdinalIgnoreCase);
        }
        await LoadReplayAsync(target.Clip);
        switched &= PreviewPlayer.IsReady &&
                    string.Equals(
                        _clip.Path,
                        target.Clip.Path,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        initialPath,
                        _clip.Path,
                        StringComparison.OrdinalIgnoreCase);
        double start = PreviewPlayer.PositionSeconds;
        DateTime playbackDeadline = DateTime.UtcNow.AddSeconds(3);
        while (PreviewPlayer.PositionSeconds < start + 0.15 &&
               DateTime.UtcNow < playbackDeadline)
        {
            await Task.Delay(100, _lifetimeCts.Token);
        }
        bool playbackAdvanced = PreviewPlayer.PositionSeconds >= start + 0.15;

        PreviewPlayer.RaiseNativeMouseLeftButtonForQa();
        await Task.Delay(100, _lifetimeCts.Token);
        bool clickPaused = !_playing;
        PreviewPlayer.RaiseNativeMouseLeftButtonForQa();
        await Task.Delay(100, _lifetimeCts.Token);
        bool clickResumed = _playing;

        bool resizeWasPlaying = _playing;
        Visibility resizeImageVisibility = PreviewImage.Visibility;
        BeginInteractiveResize();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool resizeSuppressed =
            _interactiveResizeActive &&
            PreviewPlayer.Visibility != Visibility.Visible &&
            PreviewImage.Visibility == Visibility.Visible;
        EndInteractiveResize();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        bool resizeRestored =
            !_interactiveResizeActive &&
            PreviewPlayer.Visibility == Visibility.Visible &&
            PreviewImage.Visibility == resizeImageVisibility &&
            _playing == resizeWasPlaying;

        var deleteButton = new Button { DataContext = target };
        RequestDeleteRecentReplay_Click(
            deleteButton,
            new RoutedEventArgs(Button.ClickEvent, deleteButton));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool deletePromptPassed =
            DeleteRecentReplayOverlay.Visibility == Visibility.Visible &&
            PreviewPlayer.Visibility != Visibility.Visible;
        CancelDeleteRecentReplay();
        bool deleteCancelPassed =
            DeleteRecentReplayOverlay.Visibility == Visibility.Collapsed &&
            PreviewPlayer.Visibility == Visibility.Visible;

        RecentReplay_Click(deleteButton, new RoutedEventArgs());
        await Task.Delay(150, _lifetimeCts.Token);
        bool activeClipContinued = _playing && _clip.Path == target.Clip.Path;
        await PausePlaybackAsync();
        RecentReplay_Click(deleteButton, new RoutedEventArgs());
        await Task.Delay(150, _lifetimeCts.Token);
        bool activeClipResumed = _playing && _clip.Path == target.Clip.Path;

        SelectAllReplays_Click(this, new RoutedEventArgs());
        bool markedAll = RecentReplayItems.All(item => item.IsMarked) && DeleteSelectedReplaysButton.IsEnabled;
        DeleteSelectedReplays_Click(this, new RoutedEventArgs());
        bool bulkPrompt = _pendingDeleteReplays.Length == RecentReplayItems.Count;
        CancelDeleteRecentReplay();
        SelectAllReplays_Click(this, new RoutedEventArgs());
        bool clearedAll = RecentReplayItems.All(item => !item.IsMarked) && !DeleteSelectedReplaysButton.IsEnabled;

        int unfilteredCount = RecentReplayItems.Count;
        GameFilter.SelectedIndex = 1;
        string selectedGame = ((ReplayFilterOption)GameFilter.SelectedItem).Value!;
        bool gameFilterPassed = RecentReplayItems.Count > 0 && RecentReplayItems.All(item =>
            string.Equals(item.Clip.Collection ?? "", selectedGame, StringComparison.OrdinalIgnoreCase));
        RecordingModeFilter.SelectedIndex = 2;
        bool combinedFilterPassed = RecentReplayItems.All(item => item.Clip.RecordingMode == "recording" &&
            string.Equals(item.Clip.Collection ?? "", selectedGame, StringComparison.OrdinalIgnoreCase));
        GameFilter.SelectedIndex = 0;
        RecordingModeFilter.SelectedIndex = 0;
        bool filterResetPassed = RecentReplayItems.Count == unfilteredCount;

        DateTime enrichmentDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!_outputSettingsInitialized &&
               DateTime.UtcNow < enrichmentDeadline)
        {
            await Task.Delay(50, _lifetimeCts.Token);
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool unifiedEditorPassed = _previewMode &&
                                   TimelineEditorPanel.Visibility == Visibility.Visible &&
                                   RangeTimeline.Visibility == Visibility.Visible &&
                                   AudioTrackRows.Items.Count == AudioTracks.Count &&
                                   AudioTrackCountText.Text == AudioTracks.Count.ToString() &&
                                   EditorActionsPanel.Visibility == Visibility.Visible &&
                                   OutputSettingsButton.IsVisible &&
                                   _outputSettingsInitialized &&
                                   NormalPlaybackBar.Visibility == Visibility.Visible &&
                                   EditorVolumeSlider.IsVisible &&
                                   Math.Abs(EditorVolumeSlider.Value -
                                            _playbackVolumePercent) < 0.1 &&
                                   PreviewModePanel.Visibility == Visibility.Collapsed &&
                                   RecentReplaySidebar.Visibility == Visibility.Visible;
        PlayerHelp_Click(PlayerHelpButton, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool helpPassed = PlayerHelpPopup.IsOpen &&
                          PlayerHelpPopup.Child is not null;
        PlayerHelp_Click(PlayerHelpButton, new RoutedEventArgs());

        bool batchDeletePassed = true;
        if (File.Exists(Path.Combine(_rootDirectory, ".captail-library-qa")))
        {
            RecentReplayEntry current = RecentReplayItems.Single(item => item.Clip.Path == _clip.Path);
            RecentReplayEntry other = RecentReplayItems.First(item => item.Clip.Path != _clip.Path);
            RequestDeleteReplays([current, other]);
            await ConfirmDeleteRecentReplaysAsync();
            batchDeletePassed = !File.Exists(current.Clip.Path) && !File.Exists(other.Clip.Path) &&
                RecentReplayItems.Count == unfilteredCount - 2 && PreviewPlayer.IsReady &&
                _clip.Path != current.Clip.Path;
        }

        EnterFullscreen();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool fullscreenPassed =
            RecentReplaySidebar.Visibility == Visibility.Collapsed &&
            SidebarColumn.ActualWidth == 0 &&
            Grid.GetColumnSpan(EditorWorkspace) == 2 &&
            EditorWorkspace.Margin == new Thickness(0);
        ExitFullscreen();

        bool passed = allClipsPassed && initialVolumePassed &&
                      sliderVolumePassed && wheelPassed &&
                      previousToFirstPassed && activeHighlightPassed &&
                      activeFirstClickPassed && switched &&
                      playbackAdvanced && clickPaused && clickResumed &&
                      resizeSuppressed && resizeRestored &&
                      deletePromptPassed && deleteCancelPassed && fullscreenPassed &&
                      activeClipContinued && activeClipResumed && markedAll && clearedAll && bulkPrompt &&
                      gameFilterPassed && combinedFilterPassed && filterResetPassed && batchDeletePassed;
        passed &= unifiedEditorPassed && helpPassed;
        return (
            passed,
            $"clips={RecentReplayItems.Count}, volume={volumeBefore}->{PreviewPlayer.VolumePercent}, " +
            $"firstClip={previousToFirstPassed}/{activeHighlightPassed}/{activeFirstClickPassed}, " +
            $"switched={Path.GetFileName(initialPath)}->{Path.GetFileName(_clip.Path)}, " +
            $"click={clickPaused}/{clickResumed}, resize={resizeSuppressed}/{resizeRestored}, " +
            $"delete={deletePromptPassed}/{deleteCancelPassed}, " +
            $"activeClip={activeClipContinued}/{activeClipResumed}, selection={markedAll}/{clearedAll}/{bulkPrompt}, " +
            $"filters={gameFilterPassed}/{combinedFilterPassed}/{filterResetPassed}, batchDelete={batchDeletePassed}, " +
            $"playerUi={unifiedEditorPassed}/{helpPassed}, " +
            $"playbackAdvanced={playbackAdvanced}, fullscreen={fullscreenPassed}");
    }

    internal async Task<(bool Passed, string Details)> RunPreviewGeometryQaAsync()
    {
        Width = MinWidth;
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

        string editorScreenshot = Path.Combine(
            Path.GetTempPath(),
            "Captail",
            "editor-export-layout-qa.png");
        CaptureVisualToPng(editorScreenshot);
        Point outputSettingsButtonPosition = OutputSettingsButton.TranslatePoint(
            new Point(),
            EditorActionsPanel);
        Point cancelButtonPosition = CancelButton.TranslatePoint(
            new Point(),
            EditorActionsPanel);
        double actionColumnLeft = EditorActionsPanel.ColumnDefinitions[0].ActualWidth;
        bool exportLayoutPassed =
            OutputSettingsButton.IsVisible &&
            OutputSettingsButton.ActualWidth > 0 &&
            outputSettingsButtonPosition.X >= actionColumnLeft &&
            outputSettingsButtonPosition.X + OutputSettingsButton.ActualWidth <=
                EditorActionsPanel.ActualWidth &&
            outputSettingsButtonPosition.X < cancelButtonPosition.X;

        OutputSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        string outputSettingsScreenshot = Path.Combine(
            Path.GetTempPath(),
            "Captail",
            "editor-output-settings-qa.png");
        bool outputSettingsOpenedByClick = OutputSettingsPopup.IsOpen;
        bool outputSettingsPassed =
            outputSettingsOpenedByClick &&
            VideoCodecComboBox.SelectedIndex == 0 &&
            AudioCodecComboBox.SelectedIndex == 0 &&
            ResolutionComboBox.SelectedIndex == 0 &&
            BitRateComboBox.SelectedIndex == 0 &&
            MergeAudioCheckBox.Visibility == Visibility.Visible;
        if (OutputSettingsPopup.Child is FrameworkElement outputSettingsPanel)
            CaptureVisualToPng(outputSettingsPanel, outputSettingsScreenshot);
        VideoCodecComboBox.SelectedIndex = 1;
        AudioCodecComboBox.SelectedIndex = 1;
        ResolutionComboBox.SelectedItem = ResolutionComboBox.Items
            .Cast<OutputSettingOption>()
            .First(option => option.Width == 1280 && option.Height == 720);
        BitRateComboBox.SelectedItem = BitRateComboBox.Items
            .Cast<OutputSettingOption>()
            .First(option => option.BitRateKbps == 5_000);
        MergeAudioCheckBox.IsChecked = true;
        VideoOutputSettings selectedSettings = CurrentOutputSettings();
        bool outputSettingsSelectionPassed =
            selectedSettings.VideoCodec is not null &&
            selectedSettings.AudioCodec is not null &&
            selectedSettings is
            {
                Width: 1280,
                Height: 720,
                VideoBitRateKbps: 5_000,
                MergeAudioTracks: true,
            };
        CloseOutputSettingsOnOutsideClick(VideoCodecComboBox);
        bool popupInteractionPassed = OutputSettingsPopup.IsOpen;
        CloseOutputSettingsOnOutsideClick(EditorActionsPanel);
        bool outsideClickPassed = !OutputSettingsPopup.IsOpen;
        OutputSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ResetOutputSettings_Click(this, new RoutedEventArgs());
        OutputSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool outputSettingsClosedByClick = !OutputSettingsPopup.IsOpen;
        OutputSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WindowState = WindowState.Minimized;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        bool popupClosedOnMinimize = !OutputSettingsPopup.IsOpen;
        WindowState = WindowState.Normal;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

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

        PreviewPlayer.SetVolumePercent(85);
        bool volumePassed = PreviewPlayer.VolumePercent == 85;
        PreviewPlayer.SetVolumePercent(100);

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
        await Task.Delay(250, _lifetimeCts.Token);
        bool tracksPassed = PreviewPlayer.IsReady &&
                            PreviewPlayer.DetectedAudioTrackCount == allTrackIds.Length;
        string audioMix = "audio mix needs at least two tracks";
        bool audioMixPassed = allTrackIds.Length < 2 ||
            PreviewPlayer.TryValidateAudioMix(allTrackIds.Length, out audioMix);

        string geometry = "preview window is not ready";
        bool geometryPassed = PreviewPlayer.TryValidateGeometry(out geometry);
        string videoOutput = "video output is not ready";
        bool videoOutputPassed = PreviewPlayer.TryValidateVideoOutput(out videoOutput);
        await PreviewPlayer.StopAsync(_lifetimeCts.Token);
        bool stopPassed = !PreviewPlayer.IsReady;
        bool passed = exportLayoutPassed && outputSettingsPassed &&
                      outputSettingsClosedByClick &&
                      popupInteractionPassed && outsideClickPassed &&
                      popupClosedOnMinimize &&
                      outputSettingsSelectionPassed &&
                      overwriteOverlayPassed && savingOverlayPassed &&
                      clockAdvanced &&
                      seekPassed && tracksPassed && audioMixPassed && volumePassed &&
                      geometryPassed && videoOutputPassed && stopPassed;
        string details =
            $"exportLayout={(exportLayoutPassed ? "left-of-cancel" : "failed")}, " +
            $"editorScreenshot={editorScreenshot}, " +
            $"outputSettings={(outputSettingsPassed ? "click-opened/current-defaults" : "failed")}/" +
            $"{(outputSettingsSelectionPassed ? "changes-mapped" : "changes-failed")}, " +
            $"popupClick={(popupInteractionPassed ? "kept-open" : "closed")}, " +
            $"outsideClick={(outsideClickPassed ? "closed" : "kept-open")}, " +
            $"settingsClose={(outputSettingsClosedByClick ? "click-closed" : "failed")}, " +
            $"minimize={(popupClosedOnMinimize ? "closed" : "kept-open")}, " +
            $"outputSettingsScreenshot={outputSettingsScreenshot}, " +
            $"overlay={(overwriteOverlayPassed ? "clear" : "occluded")}, " +
            $"saving={(savingOverlayPassed ? "visible" : "hidden")}, " +
            $"savingScreenshot={savingScreenshot}, " +
            $"{geometry}, {videoOutput}, " +
            $"clock={playbackStart:0.000}->{playbackAfter:0.000}" +
            $"{(clockAdvanced ? "" : " (startup pending)")}, " +
            $"volume={(volumePassed ? "85%" : "failed")}, " +
            $"seek={seekPosition:0.000}/{seekTarget:0.000}, " +
            $"tracks={PreviewPlayer.DetectedAudioTrackCount}/{allTrackIds.Length}, " +
            $"{audioMix}, " +
            $"stop={(stopPassed ? "released" : "busy")}";
        return (passed, details);
    }

    private void CaptureVisualToPng(string path)
    {
        CaptureVisualToPng(this, path);
    }

    private static void CaptureVisualToPng(FrameworkElement element, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        int width = Math.Max(
            1,
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(
            1,
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
#endif

    private async Task LoadTimelineThumbnailsAsync(
        ReplayClip? requestedClip = null,
        int? replayLoadVersion = null)
    {
        ReplayClip clip = requestedClip ?? _clip;
        using var timelineCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
        CancellationTokenSource? previousTimelineCts = Interlocked.Exchange(
            ref _timelineLoadCts,
            timelineCts);
        previousTimelineCts?.Cancel();
        try
        {
            if (!IsCurrentTimelineLoad())
                return;

            _timelineImages.Clear();
            for (int index = 0; index < TimelineFrameCount; index++)
                _timelineImages.Add(null);
            TimelineFrames.ItemsSource = _timelineImages;

            await _library.GetTimelineThumbnailsAsync(
                _rootDirectory,
                clip,
                TimelineFrameCount,
                ShowThumbnail,
                timelineCts.Token);

            void ShowThumbnail(int index, string path)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentTimelineLoad() &&
                        index >= 0 &&
                        index < _timelineImages.Count)
                    {
                        _timelineImages[index] = LoadBitmap(path, 240);
                    }
                });
            }

            bool IsCurrentTimelineLoad() =>
                string.Equals(clip.Path, _clip.Path, StringComparison.OrdinalIgnoreCase) &&
                (!replayLoadVersion.HasValue ||
                 IsCurrentReplayLoad(clip, replayLoadVersion.Value));
        }
        catch (OperationCanceledException) when (timelineCts.IsCancellationRequested)
        {
            // Window is closing or a newer replay replaced this timeline.
        }
        catch (Exception exception)
        {
            Log.Write($"Timeline thumbnail generation failed: {exception.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _timelineLoadCts, null, timelineCts);
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
            PreviewNoAudioText.Visibility = NoAudioText.Visibility;
            UpdateMergeAudioState();
            UpdateAudioTrackLayout(tracks.Count);

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
            PreviewNoAudioText.Visibility = Visibility.Visible;
            MergeAudioCheckBox.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateAudioTrackLayout(int trackCount)
    {
        AudioTrackCountText.Text = trackCount.ToString();
        PreviewAudioTrackCountText.Text = trackCount.ToString();
        AudioTrackCountBadge.Visibility = trackCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewAudioTrackCountBadge.Visibility = AudioTrackCountBadge.Visibility;
        ApplyWindowModeLayout(adjustWindow: true);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateAudioTrackViewport();

    private void EditorWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;

        bool compactActions = e.NewSize.Width < 740;
        EditorActionsPanel.RowDefinitions[1].Height =
            compactActions ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(EditorActionButtons, compactActions ? 1 : 0);
        Grid.SetColumn(EditorActionButtons, compactActions ? 0 : 1);
        Grid.SetColumnSpan(EditorActionButtons, compactActions ? 2 : 1);

        bool compactPlayback = e.NewSize.Width < 600;
        EditorVolumeSlider.Visibility = compactPlayback
            ? Visibility.Collapsed
            : Visibility.Visible;
        RangeDurationText.Visibility = compactPlayback
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateAudioTrackViewport()
    {
        if (_isFullscreen)
            return;

        double windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        int rowsForHeight = Math.Clamp(
            (int)Math.Floor((windowHeight - 650) / AudioTrackRowHeight) + 1,
            1,
            MaximumVisibleAudioTracks);
        int visibleTrackCount = Math.Min(AudioTracks.Count, rowsForHeight);
        AudioTrackScrollViewer.Height = visibleTrackCount * AudioTrackRowHeight;
        AudioTrackScrollViewer.VerticalScrollBarVisibility =
            AudioTracks.Count > visibleTrackCount
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
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
            await LoadPreviewPlayerAsync(
                _clip,
                TimeSpan.FromSeconds(_playbackPosition));
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
        // A seek to the position mpv is already paused on briefly reports
        // buffering and creates a spinner flash on every resume.
        if (Math.Abs(PreviewPlayer.PositionSeconds - _playbackPosition) > 0.05)
            PreviewPlayer.Seek(_playbackPosition, exact: true);
        PreviewPlayer.Play();
        _playing = true;
        _playbackTimer.Start();
        EditorStatusText.Text = "";
        UpdatePreviewLoadingState();
        UpdatePlayIcon();
        UpdatePlaybackText();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) =>
        await TogglePlaybackAsync();

    private async Task TogglePlaybackAsync()
    {
        if (_playerLoading || _deletingReplays || DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
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
        if (!buffering)
        {
            _bufferingSinceUtc = null;
            PreviewLoadingOverlay.Visibility = Visibility.Collapsed;
            PreviewPlayer.Visibility = Visibility.Visible;
            return;
        }

        _bufferingSinceUtc ??= DateTime.UtcNow;
        bool sustained = DateTime.UtcNow - _bufferingSinceUtc >=
                         BufferingIndicatorDelay;
        PreviewLoadingOverlay.Visibility = sustained
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Keep the last decoded frame visible under the delayed overlay.
        PreviewPlayer.Visibility = Visibility.Visible;
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
        bool canMerge = hasSeparateTracks &&
            AudioTracks.Count(track => track.IsSelected) > 1;
        MergeAudioCheckBox.IsEnabled = canMerge;
        if (!canMerge)
            MergeAudioCheckBox.IsChecked = false;
    }

    private async void SaveTrim_Click(object sender, RoutedEventArgs e) =>
        await SaveTrimAsync(overwrite: false);

    private void OutputSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_outputSettingsInitialized || _saveInProgress)
            return;
        PlayerHelpPopup.IsOpen = false;
        OutputSettingsPopup.IsOpen = !OutputSettingsPopup.IsOpen;
    }

    private void PlayerHelp_Click(object sender, RoutedEventArgs e)
    {
        OutputSettingsPopup.IsOpen = false;
        PlayerHelpPopup.IsOpen = !PlayerHelpPopup.IsOpen;
    }

    private void ResetOutputSettings_Click(object sender, RoutedEventArgs e)
    {
        VideoCodecComboBox.SelectedIndex = 0;
        AudioCodecComboBox.SelectedIndex = 0;
        ResolutionComboBox.SelectedIndex = 0;
        BitRateComboBox.SelectedIndex = 0;
        MergeAudioCheckBox.IsChecked = false;
        UpdateMergeAudioState();
    }

    private async void ExportAs_Click(object sender, RoutedEventArgs e)
    {
        if (_saveInProgress)
            return;

        OutputSettingsPopup.IsOpen = false;

        string sourceExtension = Path.GetExtension(_clip.Path).ToLowerInvariant();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localization.Text("L.Library.ExportVideo"),
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = sourceExtension,
            FileName = $"{Path.GetFileNameWithoutExtension(_clip.Name)}_converted",
            Filter =
                "MP4 video (*.mp4)|*.mp4|" +
                "Matroska video (*.mkv)|*.mkv|" +
                "WebM video (*.webm)|*.webm|" +
                "QuickTime video (*.mov)|*.mov",
            FilterIndex = sourceExtension switch
            {
                ".mkv" => 2,
                ".webm" => 3,
                ".mov" => 4,
                _ => 1,
            },
            InitialDirectory = Path.GetDirectoryName(_clip.Path),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string destination = Path.GetFullPath(dialog.FileName);
        string extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".mp4" or ".mkv" or ".webm" or ".mov"))
        {
            EditorStatusText.Text = Localization.Text(
                "L.Library.ExportUnsupportedFormat");
            return;
        }
        if (string.Equals(
                destination,
                Path.GetFullPath(_clip.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            EditorStatusText.Text = Localization.Text(
                "L.Library.ExportChooseDifferentFile");
            return;
        }

        await ExportVideoAsync(
            destination,
            OutputSettingsForDestination(destination));
    }

    private async Task ExportVideoAsync(
        string destination,
        VideoOutputSettings outputSettings)
    {
        _saveInProgress = true;
        OutputSettingsButton.IsEnabled = false;
        SaveTrimButton.IsEnabled = false;
        OverwriteButton.IsEnabled = false;
        MergeAudioCheckBox.IsEnabled = false;
        string exportingStatus = Localization.Text("L.Library.ExportingVideo");
        EditorStatusText.Text = exportingStatus;
        SavingStatusText.Text = exportingStatus;
        try
        {
            PauseNativePlayback();
            PreviewPlayer.Visibility = Visibility.Collapsed;
            ShowSavingOverlay();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await PreviewPlayer.StopAsync(_lifetimeCts.Token);
            await _library.ExportTranscodedAsync(
                _rootDirectory,
                _clip,
                destination,
                TimeSpan.FromSeconds(_selectionStart),
                TimeSpan.FromSeconds(_selectionEnd),
                SelectedAudioStreamIndices(),
                outputSettings,
                _lifetimeCts.Token);
            HideSavingOverlay();
            EditorStatusText.Text = Localization.Text("L.Library.ExportedVideo");
            OutputSettingsButton.IsEnabled = true;
            SaveTrimButton.IsEnabled = true;
            OverwriteButton.IsEnabled = true;
            UpdateMergeAudioState();
            await InitializePreviewAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Replay export failed: {exception}");
            HideSavingOverlay();
            EditorStatusText.Text = IsSharingViolation(exception)
                ? Localization.Text("L.Library.FileInUse")
                : exception.Message;
            OutputSettingsButton.IsEnabled = true;
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

        OutputSettingsPopup.IsOpen = false;
        OverwriteMessageText.Text = Localization.Text(
            CurrentOutputSettings().MergeAudioTracks
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
        OutputSettingsPopup.IsOpen = false;
        OutputSettingsButton.IsEnabled = false;
        SaveTrimButton.IsEnabled = false;
        OverwriteButton.IsEnabled = false;
        MergeAudioCheckBox.IsEnabled = false;
        VideoOutputSettings outputSettings = CurrentOutputSettings();
        string savingStatus = Localization.Text(
            outputSettings.VideoCodec is not null ||
            outputSettings.AudioCodec is not null ||
            outputSettings.Width is not null ||
            outputSettings.VideoBitRateKbps is not null
                ? "L.Library.ExportingVideo"
                : outputSettings.MergeAudioTracks
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
                    outputSettings,
                    _lifetimeCts.Token)
                : await _library.TrimAsync(
                    _rootDirectory,
                    _clip,
                    start,
                    end,
                    audioStreams,
                    outputSettings,
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
            OutputSettingsButton.IsEnabled = true;
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

    internal static string AudioLabel(AudioTrackInfo track, int count)
    {
        int trackNumber = track.Ordinal + 1;
        string title = track.Title?.Trim() ?? "";
        if (!IsGenericAudioTitle(title))
        {
            int separator = title.IndexOf(" - ", StringComparison.Ordinal);
            if (title.StartsWith("Track ", StringComparison.OrdinalIgnoreCase) &&
                separator > 0 && separator + 3 < title.Length)
            {
                return title[(separator + 3)..];
            }
            return title;
        }

        if (count == 1)
            return Localization.Text("L.Library.MixedAudioTrack");
        return Localization.Format("L.Library.AudioTrackNumber", trackNumber);
    }

    private async Task LoadPreviewPlayerAsync(ReplayClip clip, TimeSpan start)
    {
        try
        {
            await PreviewPlayer.LoadAsync(
                clip.Path,
                start,
                SelectedAudioTrackIds(),
                _lifetimeCts.Token);
        }
        catch (InvalidOperationException) when (
            !_lifetimeCts.IsCancellationRequested &&
            _library is not null)
        {
            string proxyPath = await _library.GetPreviewProxyAsync(
                _rootDirectory,
                clip,
                _lifetimeCts.Token);
            await PreviewPlayer.LoadAsync(
                proxyPath,
                start,
                [1],
                _lifetimeCts.Token);
            Log.Write($"Replay preview fallback proxy created for {clip.Name}");
        }
        PreviewPlayer.SetPlaybackSpeed(PlaybackSpeeds[_playbackSpeedIndex]);
        PreviewPlayer.SetVolumePercent(_playbackVolumePercent);
    }

    private static bool IsGenericAudioTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ||
        title.StartsWith("Captail Audio", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("SoundHandler", StringComparison.OrdinalIgnoreCase) ||
        (title.StartsWith("Track ", StringComparison.OrdinalIgnoreCase) &&
         !title.Contains(" - ", StringComparison.Ordinal));

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
        _restoreResizeMode = ResizeMode;
        _isFullscreen = true;

        WindowState = WindowState.Normal;
        nint handle = new WindowInteropHelper(this).Handle;
        _restoreWindowStyle = GetWindowLongPtrW(handle, GwlStyle);
        SetWindowLongPtrW(
            handle,
            GwlStyle,
            _restoreWindowStyle &
            ~(WsThickFrame | WsBorder | WsDlgFrame));
        ResizeMode = ResizeMode.NoResize;
        SetWindowPos(
            handle,
            0,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        Rect monitor = CurrentMonitorBounds();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Left;
        Top = monitor.Top;
        Width = monitor.Width;
        Height = monitor.Height;
        Topmost = true;
        NativeRect monitorPixels = CurrentMonitorPixelBounds();
        SetWindowPos(
            handle,
            -1,
            monitorPixels.Left,
            monitorPixels.Top,
            monitorPixels.Right - monitorPixels.Left,
            monitorPixels.Bottom - monitorPixels.Top,
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);

        HeaderRow.Height = new GridLength(0);
        EditorHeader.Visibility = Visibility.Collapsed;
        RecentReplaySidebar.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
        Grid.SetColumnSpan(EditorWorkspace, 2);
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

        nint handle = new WindowInteropHelper(this).Handle;
        if (_restoreWindowStyle != 0)
        {
            SetWindowLongPtrW(handle, GwlStyle, _restoreWindowStyle);
            SetWindowPos(
                handle,
                0,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }
        ResizeMode = _restoreResizeMode;

        HeaderRow.Height = new GridLength(56);
        EditorHeader.Visibility = Visibility.Visible;
        SidebarColumn.Width = GridLength.Auto;
        Grid.SetColumnSpan(EditorWorkspace, 1);
        EditorWorkspace.Margin = new Thickness(20, 0, 20, 20);
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);
        PlaybackRow.Height = GridLength.Auto;
        TimelineRow.Height = GridLength.Auto;
        ActionsRow.Height = GridLength.Auto;
        NormalPlaybackBar.Visibility = Visibility.Visible;
        WindowChrome.BorderThickness = new Thickness(1);
        WindowChrome.CornerRadius = new CornerRadius(0);
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
        => CurrentMonitorArea(useWorkArea: false);

    private NativeRect CurrentMonitorPixelBounds()
    {
        nint window = new WindowInteropHelper(this).Handle;
        nint monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfoW(monitor, ref info)
            ? info.Monitor
            : new NativeRect
            {
                Right = (int)SystemParameters.PrimaryScreenWidth,
                Bottom = (int)SystemParameters.PrimaryScreenHeight,
            };
    }

    private Rect CurrentMonitorWorkArea()
        => CurrentMonitorArea(useWorkArea: true);

    private Rect CurrentMonitorArea(bool useWorkArea)
    {
        nint window = new WindowInteropHelper(this).Handle;
        nint monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return SystemParameters.WorkArea;

        Matrix fromDevice = PresentationSource.FromVisual(this)?
                                .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        NativeRect area = useWorkArea ? info.WorkArea : info.Monitor;
        Point topLeft = fromDevice.Transform(new Point(area.Left, area.Top));
        Point bottomRight = fromDevice.Transform(new Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        CloseOutputSettingsOnOutsideClick(source);
        ClosePlayerHelpOnOutsideClick(source);
    }

    private void Owner_StateChanged(object? sender, EventArgs e)
    {
        if (Owner?.WindowState == WindowState.Minimized)
            CloseEditorPopups();
    }

    private void ClosePopupsWhenHidden()
    {
        if (WindowState == WindowState.Minimized || !IsVisible)
            CloseEditorPopups();
    }

    private void CloseEditorPopups()
    {
        OutputSettingsPopup.IsOpen = false;
        PlayerHelpPopup.IsOpen = false;
    }

    private void ClosePlayerHelpOnOutsideClick(DependencyObject? source)
    {
        if (!PlayerHelpPopup.IsOpen ||
            PlayerHelpButton.IsMouseOver ||
            PlayerHelpPopup.Child?.IsMouseOver == true ||
            IsDescendantOrSelf(source, PlayerHelpPopup.Child))
        {
            return;
        }

        PlayerHelpPopup.IsOpen = false;
    }

    private void CloseOutputSettingsOnOutsideClick(DependencyObject? source)
    {
        if (!OutputSettingsPopup.IsOpen ||
            OutputSettingsButton.IsMouseOver ||
            OutputSettingsPopup.Child?.IsMouseOver == true ||
            IsDescendantOrSelf(source, OutputSettingsPopup.Child))
        {
            return;
        }

        OutputSettingsPopup.IsOpen = false;
    }

    private static bool IsDescendantOrSelf(
        DependencyObject? source,
        DependencyObject? ancestor)
    {
        if (source is null || ancestor is null)
            return false;

        for (DependencyObject? current = source;
             current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_saveInProgress || _deletingReplays)
        {
            e.Handled = true;
            return;
        }
        if (OutputSettingsPopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                OutputSettingsPopup.IsOpen = false;
                e.Handled = true;
            }
            return;
        }
        if (PlayerHelpPopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                PlayerHelpPopup.IsOpen = false;
                e.Handled = true;
            }
            return;
        }
        if (DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CancelDeleteRecentReplay();
                e.Handled = true;
            }
            return;
        }
        if (GameFilter.IsKeyboardFocusWithin || RecordingModeFilter.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is CheckBox ||
            (e.Key == Key.Space && Keyboard.FocusedElement is ButtonBase))
            return;
        if (e.Key == Key.Escape &&
            DeleteRecentReplayOverlay.Visibility == Visibility.Visible)
        {
            CancelDeleteRecentReplay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape &&
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
        {
            Close();
            e.Handled = true;
        }
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
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            ShowFullscreenControls();
            ChangePlaybackVolume(5);
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            ShowFullscreenControls();
            ChangePlaybackVolume(-5);
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

    private void ChangePlaybackVolume(int delta)
    {
        if (!PreviewPlayer.IsReady || delta == 0)
            return;

        int volume = Math.Clamp(
            _playbackVolumePercent + delta,
            0,
            100);
        if (volume == _playbackVolumePercent)
            return;

        SetPlaybackVolume(volume, showFeedback: true);
    }

    private void PlayerVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider)
            return;
        SetPlaybackVolume((int)Math.Round(e.NewValue), showFeedback: false);
    }

    private void SetPlaybackVolume(int volumePercent, bool showFeedback)
    {
        int normalized = Math.Clamp(volumePercent, 0, 100);
        _playbackVolumePercent = normalized;
        if (PreviewPlayer.IsReady)
            PreviewPlayer.SetVolumePercent(normalized);
        if (Math.Abs(EditorVolumeSlider.Value - normalized) > 0.1 ||
            Math.Abs(PreviewVolumeSlider.Value - normalized) > 0.1)
        {
            _updatingVolumeSlider = true;
            EditorVolumeSlider.Value = normalized;
            PreviewVolumeSlider.Value = normalized;
            _updatingVolumeSlider = false;
        }
        if (showFeedback)
            ShowPlaybackFeedback($"{normalized}%");
        SchedulePlaybackVolumePersist();
    }

    private void SchedulePlaybackVolumePersist()
    {
        if (_onVolumeChanged is null)
            return;
        _volumePersistPending = true;
        _volumePersistTimer.Stop();
        _volumePersistTimer.Start();
    }

    private void PersistPlaybackVolumeNow()
    {
        _volumePersistTimer.Stop();
        if (!_volumePersistPending || _onVolumeChanged is null)
            return;
        _volumePersistPending = false;
        _onVolumeChanged(_playbackVolumePercent);
    }

    private void ShowPlaybackSpeedFeedback(double speed)
    {
        ShowPlaybackFeedback($"{speed:0.##}×");
    }

    private void ShowPlaybackFeedback(string value)
    {
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

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (Owner is not null)
            Owner.StateChanged += Owner_StateChanged;
        ApplyNativeCornerPreference();
        nint handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmEnterSizeMove)
            BeginInteractiveResize();
        else if (message == WmExitSizeMove)
            EndInteractiveResize();
        return 0;
    }

    private void BeginInteractiveResize()
    {
        if (_interactiveResizeActive)
            return;
        _interactiveResizeActive = true;
        _interactiveResizePlayerVisible =
            PreviewPlayer.Visibility == Visibility.Visible;
        _imageVisibilityBeforeInteractiveResize = PreviewImage.Visibility;
        _resumeAfterInteractiveResize = _interactiveResizePlayerVisible && _playing;
        if (!_interactiveResizePlayerVisible)
            return;

        if (_resumeAfterInteractiveResize)
        {
            PreviewPlayer.Pause();
            _playbackTimer.Stop();
        }
        PreviewPlayer.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
    }

    private void EndInteractiveResize()
    {
        if (!_interactiveResizeActive)
            return;
        _interactiveResizeActive = false;
        if (!_interactiveResizePlayerVisible)
            return;

        PreviewImage.Visibility = _imageVisibilityBeforeInteractiveResize;
        PreviewPlayer.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_lifetimeCts.IsCancellationRequested)
                return;
            PreviewPlayer.RefreshVideoLayout();
            if (_resumeAfterInteractiveResize && _playing)
            {
                PreviewPlayer.Play();
                _playbackTimer.Start();
            }
            _interactiveResizePlayerVisible = false;
            _resumeAfterInteractiveResize = false;
        });
    }

    private void ApplyNativeCornerPreference()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            int preference = 2;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));
        }
        catch
        {
            // Rounded corners are cosmetic and unavailable on older Windows builds.
        }
    }

    private void InitializeOutputSettings()
    {
        string extension = Path.GetExtension(_clip.Path).ToLowerInvariant();
        string currentVideoCodec = FormatVideoCodec(_videoInfo?.Codec);
        string[] audioCodecs = AudioTracks
            .Select(track => FormatAudioCodec(track.Track.Codec))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string currentAudioCodec = audioCodecs.Length switch
        {
            0 => "—",
            1 => audioCodecs[0],
            _ => string.Join(" / ", audioCodecs),
        };
        int width = Math.Max(1, _videoInfo?.Width ?? 0);
        int height = Math.Max(1, _videoInfo?.Height ?? 0);
        int currentBitRate = _videoInfo?.BitRateKbps > 0
            ? _videoInfo.BitRateKbps
            : EstimateSourceBitRateKbps();

        var videoOptions = new List<OutputSettingOption>
        {
            new("current", Localization.Format(
                "L.Library.CurrentValue", currentVideoCodec)),
        };
        foreach ((string value, string label) in VideoCodecOptions(extension))
            videoOptions.Add(new OutputSettingOption(value, label));
        VideoCodecComboBox.ItemsSource = videoOptions;

        var audioOptions = new List<OutputSettingOption>
        {
            new("current", Localization.Format(
                "L.Library.CurrentValue", currentAudioCodec)),
        };
        foreach ((string value, string label) in AudioCodecOptions(extension))
            audioOptions.Add(new OutputSettingOption(value, label));
        AudioCodecComboBox.ItemsSource = audioOptions;

        ResolutionComboBox.ItemsSource = BuildResolutionOptions(width, height);
        BitRateComboBox.ItemsSource = BuildBitRateOptions(currentBitRate);
        VideoCodecComboBox.SelectedIndex = 0;
        AudioCodecComboBox.SelectedIndex = 0;
        ResolutionComboBox.SelectedIndex = 0;
        BitRateComboBox.SelectedIndex = 0;
        _outputSettingsInitialized = true;
        OutputSettingsButton.IsEnabled = true;
        UpdateMergeAudioState();
    }

    private VideoOutputSettings CurrentOutputSettings()
    {
        if (!_outputSettingsInitialized)
        {
            return new VideoOutputSettings(
                MergeAudioTracks: MergeAudioCheckBox.IsChecked == true);
        }

        var video = (OutputSettingOption)VideoCodecComboBox.SelectedItem;
        var audio = (OutputSettingOption)AudioCodecComboBox.SelectedItem;
        var resolution = (OutputSettingOption)ResolutionComboBox.SelectedItem;
        var bitRate = (OutputSettingOption)BitRateComboBox.SelectedItem;
        return new VideoOutputSettings(
            VideoCodec: video.Value == "current" ? null : video.Value,
            AudioCodec: audio.Value == "current" ? null : audio.Value,
            Width: resolution.Width > 0 ? resolution.Width : null,
            Height: resolution.Height > 0 ? resolution.Height : null,
            VideoBitRateKbps: bitRate.BitRateKbps > 0
                ? bitRate.BitRateKbps
                : null,
            MergeAudioTracks: MergeAudioCheckBox.IsChecked == true);
    }

    private VideoOutputSettings OutputSettingsForDestination(string destination)
    {
        VideoOutputSettings settings = CurrentOutputSettings();
        string sourceExtension = Path.GetExtension(_clip.Path).ToLowerInvariant();
        string destinationExtension = Path.GetExtension(destination).ToLowerInvariant();
        if (destinationExtension == sourceExtension)
            return settings;

        return settings with
        {
            VideoCodec = settings.VideoCodec ??
                (destinationExtension == ".webm" ? "vp9" : "h264"),
            AudioCodec = settings.AudioCodec ??
                (destinationExtension == ".webm" ? "opus" : "aac"),
        };
    }

    private int EstimateSourceBitRateKbps()
    {
        double seconds = Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds);
        return (int)Math.Clamp(_clip.SizeBytes * 8d / seconds / 1000d, 500, 100_000);
    }

    private static IReadOnlyList<OutputSettingOption> BuildResolutionOptions(
        int currentWidth,
        int currentHeight)
    {
        var options = new List<OutputSettingOption>
        {
            new(
                "current",
                Localization.Format(
                    "L.Library.CurrentValue",
                    $"{currentWidth}×{currentHeight}")),
        };
        foreach ((int width, int height) in new[]
                 {
                     (3840, 2160),
                     (2560, 1440),
                     (1920, 1080),
                     (1280, 720),
                     (854, 480),
                 })
        {
            if (width != currentWidth || height != currentHeight)
            {
                options.Add(new OutputSettingOption(
                    $"{width}x{height}",
                    $"{width}×{height}",
                    width,
                    height));
            }
        }
        return options;
    }

    private static IReadOnlyList<OutputSettingOption> BuildBitRateOptions(
        int currentBitRateKbps)
    {
        var options = new List<OutputSettingOption>
        {
            new(
                "current",
                Localization.Format(
                    "L.Library.CurrentValue",
                    FormatBitRate(currentBitRateKbps))),
        };
        foreach (int bitRateKbps in new[]
                 { 5_000, 10_000, 15_000, 20_000, 30_000, 50_000, 75_000 })
        {
            if (Math.Abs(bitRateKbps - currentBitRateKbps) >= 250)
            {
                options.Add(new OutputSettingOption(
                    bitRateKbps.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    FormatBitRate(bitRateKbps),
                    BitRateKbps: bitRateKbps));
            }
        }
        return options;
    }

    private static IEnumerable<(string Value, string Label)> VideoCodecOptions(
        string extension) => extension switch
        {
            ".webm" => [("vp9", "VP9"), ("av1", "AV1")],
            ".mov" => [("h264", "H.264"), ("hevc", "HEVC")],
            ".mp4" => [("h264", "H.264"), ("hevc", "HEVC"), ("av1", "AV1")],
            _ =>
            [
                ("h264", "H.264"),
                ("hevc", "HEVC"),
                ("av1", "AV1"),
                ("vp9", "VP9"),
            ],
        };

    private static IEnumerable<(string Value, string Label)> AudioCodecOptions(
        string extension) => extension == ".webm"
            ? [("opus", "Opus")]
            : extension is ".mp4" or ".mov"
                ? [("aac", "AAC")]
                : [("aac", "AAC"), ("opus", "Opus")];

    private static string FormatAudioCodec(string? codec) =>
        codec?.ToLowerInvariant() switch
        {
            "aac" => "AAC",
            "opus" => "Opus",
            "mp3" => "MP3",
            null or "" => "—",
            _ => codec.ToUpperInvariant(),
        };

    private static string FormatBitRate(int bitRateKbps) =>
        $"{bitRateKbps / 1000d:0.#} Mbps";

    private sealed record OutputSettingOption(
        string Value,
        string Label,
        int Width = 0,
        int Height = 0,
        int BitRateKbps = 0)
    {
        public override string ToString() => Label;
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

    private const int GwlStyle = -16;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsThickFrame = 0x00040000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int DwmwaWindowCornerPreference = 33;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(
        nint window,
        int index,
        nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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

public sealed record RecentReplayEntry(
    ReplayClip Clip,
    string Title,
    string Duration,
    ImageSource? Thumbnail) : INotifyPropertyChanged
{
    private bool _isMarked;
    private bool _isActive;
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarked)));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ReplayFilterOption(string? Value, string Label)
{
    public override string ToString() => Label;
}
