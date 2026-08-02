using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Captail;

public partial class ClipEditorWindow : Window
{
    private const double MinimumSelectionSeconds = 0.25;
    private const int TimelineFrameCount = 12;
    private readonly ReplayLibrary _library;
    private readonly string _rootDirectory;
    private readonly ReplayClip _clip;
    private readonly Action<string> _onSaved;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private readonly List<BitmapImage> _timelineImages = [];
    private double _selectionStart;
    private double _selectionEnd;
    private double _playbackPosition;
    private bool _playing;
    private bool _playerLoading;
    private bool _resumeAfterScrub;
    private int _stillRequest;
    private VideoStreamInfo? _videoInfo;

    public ObservableCollection<AudioTrackRow> AudioTracks { get; } = [];

    public ClipEditorWindow(
        ReplayLibrary library,
        string rootDirectory,
        ReplayClip clip,
        Action<string> onSaved)
    {
        _library = library;
        _rootDirectory = rootDirectory;
        _clip = clip;
        _onSaved = onSaved;
        _selectionEnd = Math.Max(MinimumSelectionSeconds, clip.Duration.TotalSeconds);
        InitializeComponent();
        DataContext = this;
        ClipNameText.Text = clip.Name;
        if (clip.ThumbnailPath is not null && File.Exists(clip.ThumbnailPath))
            PreviewImage.Source = LoadBitmap(clip.ThumbnailPath, 900);
        UpdateRangeText();
        UpdateTimelineVisual();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += async (_, _) => await UpdatePlaybackAsync();
        Loaded += async (_, _) => await LoadEditorAsync();
        SourceInitialized += (_, _) => ApplyNativeCornerPreference();
        Closed += (_, _) =>
        {
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _lifetimeCts.Cancel();
            PreviewPlayer.Stop();
            _lifetimeCts.Dispose();
        };
    }

    private async Task LoadEditorAsync()
    {
        await Task.WhenAll(
            LoadTimelineThumbnailsAsync(),
            LoadAudioTracksAsync(),
            LoadVideoInfoAsync(),
            UpdateStillFrameAsync(_selectionStart));
        if (!_lifetimeCts.IsCancellationRequested)
            PlayButton.IsEnabled = true;
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
        await Task.Delay(200, _lifetimeCts.Token);
        await StartPlaybackAsync(_selectionStart);
        await Task.Delay(700, _lifetimeCts.Token);
        string details = "preview window is not ready";
        bool passed = PreviewPlayer.IsReady &&
                      PreviewPlayer.TryValidateGeometry(out details);
        StopNativePlayback();
        return (passed, details);
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

    private async Task LoadAudioTracksAsync()
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

            await Task.WhenAll(AudioTracks.Select(LoadWaveformAsync));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Audio track inspection failed: {exception.Message}");
            NoAudioText.Visibility = Visibility.Visible;
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

    private async Task StartPlaybackAsync(double position)
    {
        if (_playerLoading || _lifetimeCts.IsCancellationRequested)
            return;
        _playerLoading = true;
        PlayButton.IsEnabled = false;
        _playbackTimer.Stop();
        _playbackClock.Reset();
        _playing = false;
        _playbackPosition = Math.Clamp(position, _selectionStart, _selectionEnd);
        UpdatePlayIcon();
        UpdatePlaybackText();
        try
        {
            double remaining = Math.Max(
                MinimumSelectionSeconds,
                _selectionEnd - _playbackPosition);
            PreviewPlayer.Visibility = Visibility.Visible;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            TimeSpan startupDelay = await PreviewPlayer.PlayAsync(
                _clip.Path,
                TimeSpan.FromSeconds(_playbackPosition),
                TimeSpan.FromSeconds(remaining),
                SelectedAudioStreamIndices(),
                _lifetimeCts.Token);
            _playbackPosition = Math.Min(
                _selectionEnd,
                _playbackPosition + startupDelay.TotalSeconds);
            _playing = true;
            _playbackClock.Restart();
            _playbackTimer.Start();
            EditorStatusText.Text = "";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            PreviewPlayer.Visibility = Visibility.Collapsed;
            Log.Write($"Clip preview failed ({_clip.Name}): {exception}");
            EditorStatusText.Text = exception.Message;
        }
        finally
        {
            _playerLoading = false;
            PlayButton.IsEnabled = !_lifetimeCts.IsCancellationRequested;
            UpdatePlayIcon();
        }
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

    private async Task UpdatePlaybackAsync()
    {
        double position = CurrentPlaybackPosition();
        if (position >= _selectionEnd || !PreviewPlayer.IsReady)
        {
            StopNativePlayback();
            _playbackPosition = _selectionStart;
            UpdatePlayIcon();
            UpdatePlaybackText();
            UpdateTimelineVisual();
            await UpdateStillFrameAsync(_playbackPosition);
            return;
        }
        UpdatePlaybackText();
        UpdateTimelineVisual();
    }

    private double CurrentPlaybackPosition() =>
        Math.Min(
            _selectionEnd,
            _playbackPosition + (_playing ? _playbackClock.Elapsed.TotalSeconds : 0));

    private async Task PausePlaybackAsync(bool updateStill = true)
    {
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        StopNativePlayback();
        UpdatePlayIcon();
        UpdatePlaybackText();
        UpdateTimelineVisual();
        if (updateStill)
            await UpdateStillFrameAsync(_playbackPosition);
    }

    private void StopNativePlayback()
    {
        _playbackClock.Reset();
        _playing = false;
        _playbackTimer.Stop();
        PreviewPlayer.Stop();
        PreviewPlayer.Visibility = Visibility.Collapsed;
    }

    private async Task UpdateStillFrameAsync(double position)
    {
        int request = Interlocked.Increment(ref _stillRequest);
        try
        {
            string? path = await _library.GetPreviewThumbnailAsync(
                _rootDirectory,
                _clip,
                TimeSpan.FromSeconds(position),
                _lifetimeCts.Token);
            if (request == Volatile.Read(ref _stillRequest) &&
                path is not null && File.Exists(path) &&
                !_lifetimeCts.IsCancellationRequested)
            {
                PreviewImage.Source = LoadBitmap(path, 900);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            Log.Write($"Preview still generation failed: {exception.Message}");
        }
    }

    private void PauseForTimelineEdit()
    {
        if (_playing)
            _playbackPosition = CurrentPlaybackPosition();
        StopNativePlayback();
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
        ShowNearestTimelineFrame(_playbackPosition);
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
        ShowNearestTimelineFrame(_playbackPosition);
        UpdateRangeText();
        UpdateTimelineVisual();
    }

    private async void RangeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _playbackPosition = sender == StartThumb ? _selectionStart : _selectionEnd;
        ShowNearestTimelineFrame(_playbackPosition);
        UpdatePlaybackText();
        UpdateTimelineVisual();
        await UpdateStillFrameAsync(_playbackPosition);
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
        ShowNearestTimelineFrame(_playbackPosition);
        UpdatePlaybackText();
        UpdateTimelineVisual();
    }

    private async void PlayheadThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        bool resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        if (resume)
            await StartPlaybackAsync(_playbackPosition);
        else
            await UpdateStillFrameAsync(_playbackPosition);
    }

    private async void RangeTimeline_MouseLeftButtonDown(
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
        ShowNearestTimelineFrame(_playbackPosition);
        UpdatePlaybackText();
        UpdateTimelineVisual();
        if (resume)
            await StartPlaybackAsync(_playbackPosition);
        else
            await UpdateStillFrameAsync(_playbackPosition);
        e.Handled = true;
    }

    private async void Back_Click(object sender, RoutedEventArgs e) =>
        await SeekAsync(CurrentPlaybackPosition() - 5);

    private async void Forward_Click(object sender, RoutedEventArgs e) =>
        await SeekAsync(CurrentPlaybackPosition() + 5);

    private async Task SeekAsync(double position)
    {
        bool resume = _playing;
        PauseForTimelineEdit();
        _playbackPosition = Math.Clamp(position, _selectionStart, _selectionEnd);
        ShowNearestTimelineFrame(_playbackPosition);
        UpdatePlaybackText();
        UpdateTimelineVisual();
        if (resume)
            await StartPlaybackAsync(_playbackPosition);
        else
            await UpdateStillFrameAsync(_playbackPosition);
    }

    private async void AudioTrackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing || _playerLoading)
            return;
        double position = CurrentPlaybackPosition();
        PauseForTimelineEdit();
        await StartPlaybackAsync(position);
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

    private void ShowNearestTimelineFrame(double position)
    {
        if (_timelineImages.Count == 0)
            return;
        double duration = Math.Max(MinimumSelectionSeconds, _clip.Duration.TotalSeconds);
        int index = Math.Clamp(
            (int)(position / duration * _timelineImages.Count),
            0,
            _timelineImages.Count - 1);
        PreviewImage.Source = _timelineImages[index];
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

    private void UpdatePlaybackText()
    {
        if (PlaybackTimeText is null)
            return;
        PlaybackTimeText.Text =
            $"{FormatTime(TimeSpan.FromSeconds(CurrentPlaybackPosition()), false)} / " +
            FormatTime(_clip.Duration, false);
    }

    private void UpdatePlayIcon()
    {
        if (PlayIcon is null)
            return;
        PlayIcon.Data = (Geometry)FindResource(_playing ? "IconPause" : "IconPlay");
    }

    private IReadOnlyList<int> SelectedAudioStreamIndices() =>
        AudioTracks
            .Where(track => track.IsSelected)
            .Select(track => track.Track.StreamIndex)
            .ToArray();

    private async void SaveTrim_Click(object sender, RoutedEventArgs e) =>
        await SaveTrimAsync(overwrite: false);

    private void RequestOverwrite_Click(object sender, RoutedEventArgs e)
    {
        OverwriteFileText.Text = _clip.Name;
        OverwriteConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void CancelOverwrite_Click(object sender, RoutedEventArgs e) =>
        CancelOverwrite();

    private void CancelOverwrite() =>
        OverwriteConfirmOverlay.Visibility = Visibility.Collapsed;

    private async void ConfirmOverwrite_Click(object sender, RoutedEventArgs e)
    {
        CancelOverwrite();
        await SaveTrimAsync(overwrite: true);
    }

    private async Task SaveTrimAsync(bool overwrite)
    {
        SaveTrimButton.IsEnabled = false;
        OverwriteButton.IsEnabled = false;
        EditorStatusText.Text = Localization.Text("L.Library.Trimming");
        try
        {
            StopNativePlayback();
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
                    _lifetimeCts.Token)
                : await _library.TrimAsync(
                    _rootDirectory,
                    _clip,
                    start,
                    end,
                    audioStreams,
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
            EditorStatusText.Text = exception.Message;
            SaveTrimButton.IsEnabled = true;
            OverwriteButton.IsEnabled = true;
        }
    }

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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            OverwriteConfirmOverlay.Visibility == Visibility.Visible)
        {
            CancelOverwrite();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.Space)
        {
            PlayPause_Click(PlayButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            Back_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Forward_Click(this, new RoutedEventArgs());
            e.Handled = true;
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
