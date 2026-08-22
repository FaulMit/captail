using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Captail;

public partial class ProcessAudioRoutingWindow : Window
{
    private readonly ObservableCollection<ProcessAudioRouteItem> _applications = [];
    private readonly bool _microphoneEnabled;
    private readonly AudioRoutingFormatCapabilities _capabilities;
    private ProcessAudioSessionMonitor? _monitor;
    private bool _receivedFirstUpdate;
    private bool _closing;
    private bool _sessionsAvailable = true;

    internal ProcessAudioRoutingWindow(
        IEnumerable<ProcessAudioRoute> routes,
        int microphoneTrack,
        bool microphoneEnabled,
        AudioRoutingFormatCapabilities capabilities)
    {
        _microphoneEnabled = microphoneEnabled;
        _capabilities = capabilities;
        AvailableTracks = new ObservableCollection<AudioTrackOption>(
            Enumerable.Range(1, capabilities.MaxTracks)
                .Select(number => new AudioTrackOption(
                    number,
                    Localization.Format("L.AdvancedAudio.TrackFormat", number))));

        foreach (ProcessAudioRoute route in routes)
        {
            AddApplication(new ProcessAudioRouteItem(
                route.Executable,
                Path.GetFileNameWithoutExtension(route.Executable),
                isSelected: true,
                Math.Clamp(route.Track, 1, capabilities.MaxTracks)));
        }

        ApplicationsView = CollectionViewSource.GetDefaultView(_applications);
        ApplicationsView.Filter = FilterApplication;
        ApplicationsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ProcessAudioRouteItem.GroupName)));
        ApplicationsView.SortDescriptions.Add(
            new SortDescription(nameof(ProcessAudioRouteItem.GroupOrder),
                ListSortDirection.Ascending));
        ApplicationsView.SortDescriptions.Add(
            new SortDescription(nameof(ProcessAudioRouteItem.IsActive),
                ListSortDirection.Descending));
        ApplicationsView.SortDescriptions.Add(
            new SortDescription(nameof(ProcessAudioRouteItem.DisplayName),
                ListSortDirection.Ascending));

        InitializeComponent();
        DataContext = this;
        MicrophoneTrackBox.SelectedValue = Math.Clamp(
            microphoneTrack,
            1,
            capabilities.MaxTracks);
        MicrophoneTrackBox.IsEnabled = microphoneEnabled;
        MicrophoneHintText.Text = Localization.Text(
            microphoneEnabled
                ? "L.AdvancedAudio.MicrophoneEnabled"
                : "L.AdvancedAudio.MicrophoneDisabled");
        UpdateText();

        Loaded += (_, _) =>
        {
            AnimateEntrance();
            _monitor = new ProcessAudioSessionMonitor(OnSessionsUpdated);
            SearchBox.Focus();
        };
        Closed += async (_, _) =>
        {
            _closing = true;
            ProcessAudioSessionMonitor? monitor = _monitor;
            _monitor = null;
            if (monitor is not null)
                await monitor.DisposeAsync();
        };
    }

    public ObservableCollection<AudioTrackOption> AvailableTracks { get; }
    public ICollectionView ApplicationsView { get; }
    internal IReadOnlyList<ProcessAudioRoute> ResultRoutes { get; private set; } = [];
    internal int ResultMicrophoneTrack { get; private set; } = 1;

    private void OnSessionsUpdated(ProcessAudioSessionUpdate update)
    {
        if (_closing)
            return;
        Dispatcher.BeginInvoke(() => ApplySessionUpdate(update));
    }

    private void ApplySessionUpdate(ProcessAudioSessionUpdate update)
    {
        if (_closing)
            return;

        _receivedFirstUpdate = true;
        _sessionsAvailable = update.IsAvailable;
        LoadingState.Visibility = Visibility.Collapsed;
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessAudioSessionSnapshot session in update.Sessions)
        {
            visible.Add(session.Executable);
            ProcessAudioRouteItem? item = _applications.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Executable,
                    session.Executable,
                    StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new ProcessAudioRouteItem(
                    session.Executable,
                    session.DisplayName,
                    isSelected: false,
                    NextDefaultTrack());
                AddApplication(item);
            }
            item.UpdateSession(session);
        }

        foreach (ProcessAudioRouteItem item in _applications.ToArray())
        {
            if (visible.Contains(item.Executable))
                continue;
            if (item.IsSelected)
                item.MarkNotRunning();
            else
                _applications.Remove(item);
        }

        ApplicationsView.Refresh();
        UpdateEmptyState(update.IsAvailable);
        UpdateText();
    }

    private void AddApplication(ProcessAudioRouteItem item)
    {
        item.Changed += Application_Changed;
        _applications.Add(item);
    }

    private void Application_Changed(ProcessAudioRouteItem item)
    {
        ValidationBanner.Visibility = Visibility.Collapsed;
        ApplicationsView.Refresh();
        UpdateText();
    }

    private bool FilterApplication(object value)
    {
        if (value is not ProcessAudioRouteItem item)
            return false;
        string query = SearchBox?.Text.Trim() ?? "";
        return query.Length == 0 ||
               item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Executable.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private int NextDefaultTrack()
    {
        HashSet<int> used = _applications
            .Where(item => item.IsSelected)
            .Select(item => item.Track)
            .ToHashSet();
        return AvailableTracks
                   .Select(option => option.Number)
                   .FirstOrDefault(track => !used.Contains(track)) is int free && free > 0
            ? free
            : 1;
    }

    private void UpdateText()
    {
        int selected = _applications.Count(item => item.IsSelected);
        var tracks = _applications
            .Where(item => item.IsSelected)
            .Select(item => item.Track)
            .ToHashSet();
        if (_microphoneEnabled)
        {
            int micTrack = MicrophoneTrackBox?.SelectedValue is int selectedTrack
                ? selectedTrack
                : 1;
            tracks.Add(micTrack);
        }

        FormatSummaryText?.SetCurrentValue(
            System.Windows.Controls.TextBlock.TextProperty,
            Localization.Format(
                "L.AdvancedAudio.FormatSummary",
                _capabilities.AudioCodec.ToUpperInvariant(),
                _capabilities.Container,
                _capabilities.MaxTracks));
        SelectionSummaryText?.SetCurrentValue(
            System.Windows.Controls.TextBlock.TextProperty,
            Localization.Format(
                "L.AdvancedAudio.SelectionSummary",
                selected,
                tracks.Count));
    }

    private void UpdateEmptyState(bool available)
    {
        bool empty = ApplicationsView.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
            return;

        EmptyTitleText.Text = Localization.Text(
            available
                ? "L.AdvancedAudio.EmptyTitle"
                : "L.AdvancedAudio.UnavailableTitle");
        EmptyDetailText.Text = Localization.Text(
            available
                ? "L.AdvancedAudio.EmptyDetail"
                : "L.AdvancedAudio.UnavailableDetail");
    }

    private void SearchBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplicationsView.Refresh();
        if (_receivedFirstUpdate)
            UpdateEmptyState(_sessionsAvailable);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        ProcessAudioRouteItem[] selected = _applications
            .Where(item => item.IsSelected)
            .ToArray();
        if (selected.Length == 0 && !_microphoneEnabled)
        {
            ValidationText.Text = Localization.Text(
                "L.AdvancedAudio.SelectAtLeastOne");
            ValidationBanner.Visibility = Visibility.Visible;
            return;
        }

        ResultRoutes = selected
            .Select(item => new ProcessAudioRoute
            {
                Executable = item.Executable,
                Track = item.Track,
            })
            .OrderBy(route => route.Executable, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ResultMicrophoneTrack = MicrophoneTrackBox.SelectedValue is int track
            ? track
            : 1;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void AnimateEntrance()
    {
        Opacity = 0;
        BeginAnimation(
            OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(150)));
    }
}

public sealed record AudioTrackOption(int Number, string Label)
{
    public override string ToString() => Label;
}

internal sealed class ProcessAudioRouteItem : INotifyPropertyChanged
{
    private string _displayName;
    private bool _isSelected;
    private int _track;
    private double _peak;
    private bool _isRunning;
    private bool _isActive;
    private int _processCount;

    internal ProcessAudioRouteItem(
        string executable,
        string displayName,
        bool isSelected,
        int track)
    {
        Executable = executable;
        _displayName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(executable)
            : displayName;
        _isSelected = isSelected;
        _track = track;
    }

    internal event Action<ProcessAudioRouteItem>? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Executable { get; }
    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }
    public string Initials => BuildInitials(DisplayName);
    public string GroupName => Localization.Text(
        IsSelected
            ? "L.AdvancedAudio.Selected"
            : "L.AdvancedAudio.Available");
    public int GroupOrder => IsSelected ? 0 : 1;
    public string ProcessCountText => ProcessCount > 1
        ? Localization.Format("L.AdvancedAudio.ProcessCount", ProcessCount)
        : "";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value))
                return;
            OnPropertyChanged(nameof(GroupName));
            OnPropertyChanged(nameof(GroupOrder));
            Changed?.Invoke(this);
        }
    }
    public int Track
    {
        get => _track;
        set
        {
            if (SetField(ref _track, value))
                Changed?.Invoke(this);
        }
    }
    public double Peak
    {
        get => _peak;
        private set => SetField(ref _peak, value);
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetField(ref _isRunning, value);
    }
    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }
    public int ProcessCount
    {
        get => _processCount;
        private set
        {
            if (SetField(ref _processCount, value))
                OnPropertyChanged(nameof(ProcessCountText));
        }
    }

    internal void UpdateSession(ProcessAudioSessionSnapshot session)
    {
        DisplayName = session.DisplayName;
        OnPropertyChanged(nameof(Initials));
        Peak = session.Peak;
        IsRunning = true;
        IsActive = session.IsActive;
        ProcessCount = session.ProcessCount;
    }

    internal void MarkNotRunning()
    {
        Peak = 0;
        IsRunning = false;
        IsActive = false;
        ProcessCount = 0;
    }

    private static string BuildInitials(string displayName)
    {
        string[] words = displayName.Split(
            [' ', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return string.Concat(words.Take(2).Select(word => char.ToUpper(
                word[0],
                CultureInfo.CurrentCulture)));
        return displayName.Length == 0
            ? "?"
            : char.ToUpper(displayName[0], CultureInfo.CurrentCulture).ToString();
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
