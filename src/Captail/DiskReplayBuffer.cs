using System.IO;

namespace Captail;

/// <summary>
/// Owns closed OBS muxer segments. Native capture never waits for remuxing or cleanup.
/// Snapshots lease their files until FFmpeg exits; the live window can advance meanwhile.
/// </summary>
internal sealed class DiskReplayBuffer : IDisposable
{
    internal const int SegmentSeconds = 2;
    private sealed record Segment(string Path, double Start, double End);
    private readonly object _gate = new();
    private readonly Config _config;
    private readonly List<Segment> _segments = [];
    private readonly HashSet<string> _leased = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _garbage = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileStream _owner;
    private readonly Timer _cleanupTimer;
    private TaskCompletionSource<Segment[]>? _snapshot;
    private bool _saving;
    private bool _disposed;
    private string _currentPath = "";
    private double _currentStart;
    private double _latestEnd;
    private double _windowStart;
    private string? _saveOperationId;

    internal object DiagnosticState
    {
        get
        {
            lock (_gate)
                return new
                {
                    session = Path.GetFileName(DirectoryPath), closedSegments = _segments.Count,
                    leasedSegments = _leased.Count, pendingCleanup = _garbage.Count,
                    _saving, _disposed, snapshotPending = _snapshot is not null,
                    _currentStart, _latestEnd, _windowStart,
                    oldestStart = _segments.Count > 0 ? _segments[0].Start : _currentStart,
                };
        }
    }

    internal string DirectoryPath { get; }
    internal string Extension { get; }
    internal string FileFormat { get; }

    internal DiskReplayBuffer(Config config)
    {
        _config = config;
        Extension = config.AudioCodec.Equals("opus", StringComparison.OrdinalIgnoreCase) ? "mkv" : "mp4";
        string root = Path.GetFullPath(Path.Combine(config.OutputDirectory, ".captail-buffer"));
        Directory.CreateDirectory(root);
        RemoveAbandonedSessions(root);
        string id = Guid.NewGuid().ToString("N");
        DirectoryPath = Path.Combine(root, id);
        Directory.CreateDirectory(DirectoryPath);
        _owner = new FileStream(Path.Combine(DirectoryPath, ".owner"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        FileFormat = "segment_%CCYY-%MM-%DD_%hh-%mm-%ss.tmp";
        _cleanupTimer = new Timer(_ => CleanGarbage(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    internal string BeginWindow()
    {
        lock (_gate)
        {
            ClearWindowLocked();
            if (_currentPath.Length > 0)
                _garbage.Add(_currentPath);
            _currentPath = Path.Combine(DirectoryPath, $"segment_{Guid.NewGuid():N}.tmp.{Extension}");
            _currentStart = _latestEnd = _windowStart = 0;
            return _currentPath;
        }
    }

    internal void FileChanged(string nextPath, double encodedSeconds)
    {
        lock (_gate)
        {
            if (_disposed || !IsSessionFile(nextPath))
                return;
            _latestEnd = Math.Max(_currentStart, encodedSeconds);
            _segments.Add(new Segment(_currentPath, _currentStart, _latestEnd));
            _currentPath = nextPath;
            _currentStart = _latestEnd;
            PruneLocked();
            if (_snapshot is not null)
            {
                Segment[] snapshot = _segments.ToArray();
                foreach (Segment segment in snapshot)
                    _leased.Add(segment.Path);
                Log.Event(DiagnosticLevel.Info, "DiskReplay", "disk.snapshot-ready", new
                {
                    count = snapshot.Length,
                    first = snapshot.FirstOrDefault()?.Start, last = snapshot.LastOrDefault()?.End,
                    nextFile = Path.GetFileName(nextPath),
                }, _saveOperationId);
                _snapshot.TrySetResult(snapshot);
                _snapshot = null;
            }
        }
    }

    internal (Task<string> Completion, Task SnapshotReady) BeginSave(
        Func<bool> split, CancellationToken cancellationToken, string? operationId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<Segment[]> snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_saving)
                throw new InvalidOperationException(Localization.Text("L.Engine.SavePending"));
            _saving = true;
            _saveOperationId = operationId ?? Log.OperationId;
            snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshot = snapshot;
        }
        Log.Event(DiagnosticLevel.Info, "DiskReplay", "disk.save-request", new
        {
            snapshotLimitSeconds = 15, totalLimitSeconds = 120, state = DiagnosticState,
        }, _saveOperationId);
        try
        {
            if (!split())
                throw new InvalidOperationException(Localization.Text("L.Engine.SaveRejected"));
        }
        catch
        {
            lock (_gate)
            {
                if (snapshot.Task.IsCompletedSuccessfully)
                    foreach (Segment file in snapshot.Task.Result)
                        _leased.Remove(file.Path);
                _snapshot = null;
                _saving = false;
            }
            throw;
        }
        // Task.Run also prevents callers with a WPF synchronization context from blocking remuxing.
        return (Task.Run(() => SaveAsync(snapshot, cancellationToken)), snapshot.Task);
    }

    private async Task<string> SaveAsync(TaskCompletionSource<Segment[]> snapshot, CancellationToken cancellationToken)
    {
        Segment[] files = [];
        string? previousOperation = Log.SetOperation(_saveOperationId);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        string stage = "wait-split-callback";
        string? currentFile = null;
        int fileIndex = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            try
            {
                files = await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(15), linked.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Event(DiagnosticLevel.Error, "DiskReplay", "disk.snapshot-timeout",
                    new { limitSeconds = 15, elapsedMs = elapsed.ElapsedMilliseconds, state = DiagnosticState });
                throw new TimeoutException(Localization.Text("L.Engine.SaveTimeout"));
            }
            if (files.Length == 0)
                throw new InvalidOperationException(Localization.Text("L.Engine.SaveRejected"));

            // file_changed is emitted before the helper has closed the previous file.
            // Windows denies an exclusive open until all writer handles are released.
            stage = "wait-segment-close";
            Log.Event(DiagnosticLevel.Info, "DiskReplay", "disk.wait-closed", new { count = files.Length });
            foreach (Segment file in files)
            {
                currentFile = Path.GetFileName(file.Path);
                await WaitForClosedFileAsync(file.Path, linked.Token).ConfigureAwait(false);
                fileIndex++;
            }
            Log.Event(DiagnosticLevel.Info, "DiskReplay", "disk.segments-closed",
                new { count = files.Length, elapsedMs = elapsed.ElapsedMilliseconds });
            stage = "probe-and-remux";
            var ffmpeg = new FfmpegAdapter();
            string destination = Path.Combine(_config.OutputDirectory,
                $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.{Extension}");
            await ffmpeg.ConcatReplaySegmentsAsync(files.Select(file => file.Path).ToArray(),
                destination, _config.BufferSeconds,
                Enumerable.Range(1, ObsReplayEngine.AudioTrackCount(_config))
                    .Select(track => ObsReplayEngine.BuildAudioTrackName(_config, track)).ToArray(),
                linked.Token).ConfigureAwait(false);
            Log.Event(DiagnosticLevel.Info, "DiskReplay", "disk.save-completed",
                new { destination, segments = files.Length, elapsedMs = elapsed.ElapsedMilliseconds });
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            Log.Event(DiagnosticLevel.Error, "DiskReplay", "disk.total-timeout", new
            {
                stage, currentFile, fileIndex, count = files.Length, limitSeconds = 120,
                elapsedMs = elapsed.ElapsedMilliseconds, state = DiagnosticState,
            });
            throw new TimeoutException(Localization.Text("L.Engine.SaveTimeout"));
        }
        catch (Exception exception)
        {
            bool cancelled = exception is OperationCanceledException;
            Log.Event(cancelled ? DiagnosticLevel.Info : DiagnosticLevel.Error, "DiskReplay",
                cancelled ? "disk.save-cancelled" : "disk.save-failed",
                new { stage, currentFile, fileIndex, count = files.Length, elapsedMs = elapsed.ElapsedMilliseconds },
                exception: exception);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                // A split may win the cancellation race before WaitAsync observes it.
                if (files.Length == 0 && snapshot.Task.IsCompletedSuccessfully)
                    files = snapshot.Task.Result;
                if (ReferenceEquals(_snapshot, snapshot))
                    _snapshot = null;
                foreach (Segment file in files)
                    _leased.Remove(file.Path);
                _saving = false;
                _saveOperationId = null;
            }
            CleanGarbage();
            TryRemoveDisposedDirectory();
            Log.SetOperation(previousOperation);
        }
    }

    internal void AdvanceWindow()
    {
        lock (_gate)
        {
            _windowStart = _currentStart;
            ClearWindowLocked();
        }
    }

    internal void EndWindow()
    {
        lock (_gate)
        {
            _snapshot?.TrySetException(new InvalidOperationException(Localization.Text("L.Engine.BufferStopped")));
            ClearWindowLocked();
            if (_currentPath.Length > 0)
                _garbage.Add(_currentPath);
            _currentPath = "";
            _currentStart = _latestEnd = _windowStart = 0;
        }
    }

    internal void OutputStopped()
    {
        lock (_gate)
            _snapshot?.TrySetException(new InvalidOperationException(Localization.Text("L.Engine.BufferStopped")));
    }

    internal ulong BufferedBytes
    {
        get
        {
            lock (_gate)
                return (ulong)(_segments.Sum(segment => FileSize(segment.Path)) + FileSize(_currentPath));
        }
    }

    internal int GetAvailableSeconds(double encodedSeconds)
    {
        lock (_gate)
        {
            double oldest = _segments.Count > 0 ? _segments[0].Start : _currentStart;
            return (int)Math.Clamp(encodedSeconds - Math.Max(_windowStart, oldest), 0, _config.BufferSeconds);
        }
    }

    private void PruneLocked()
    {
        double cutoff = Math.Max(_windowStart, _latestEnd - _config.BufferSeconds);
        while (_segments.Count > 1 && _segments[0].End <= cutoff)
            RemoveFirstLocked();
        if (_config.MaxReplaySizeMb > 0)
        {
            long total = _segments.Sum(segment => FileSize(segment.Path));
            long limit = _config.MaxReplaySizeMb * 1024L * 1024;
            while (_segments.Count > 1 && total > limit)
            {
                total -= FileSize(_segments[0].Path);
                RemoveFirstLocked();
            }
        }
    }

    private void RemoveFirstLocked()
    {
        _garbage.Add(_segments[0].Path);
        _segments.RemoveAt(0);
    }

    private void ClearWindowLocked()
    {
        foreach (Segment segment in _segments)
            _garbage.Add(segment.Path);
        _segments.Clear();
    }

    private bool IsSessionFile(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).StartsWith("segment_", StringComparison.Ordinal) &&
        path.EndsWith($".tmp.{Extension}", StringComparison.OrdinalIgnoreCase);

    private void CleanGarbage()
    {
        lock (_gate)
        {
            foreach (string path in _garbage.ToArray())
            {
                if (_leased.Contains(path) || !IsSessionFile(path))
                    continue;
                try
                {
                    File.Delete(path);
                    _garbage.Remove(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static async Task WaitForClosedFileAsync(string path, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (file.Length > 0)
                    return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow >= deadline)
                throw new IOException("Disk replay segment was not finalized.");
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            _snapshot?.TrySetCanceled();
            ClearWindowLocked();
            if (_currentPath.Length > 0)
                _garbage.Add(_currentPath);
        }
        _cleanupTimer.Dispose();
        CleanGarbage();
        TryRemoveDisposedDirectory();
    }

    private void TryRemoveDisposedDirectory()
    {
        lock (_gate)
        {
            if (!_disposed || _saving)
                return;
            _owner.Dispose();
            _lifetime.Dispose();
            if (_garbage.Count > 0)
                return; // Keep the ownership marker so the next start can retry cleanup.
            try
            {
                File.Delete(Path.Combine(DirectoryPath, ".owner"));
                Directory.Delete(DirectoryPath, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RemoveAbandonedSessions(string root)
    {
        // Only GUID sessions bearing our marker; no recursive deletion or reparse traversal.
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(Path.Combine(directory, ".owner")))
                continue;
            try
            {
                using (var owner = new FileStream(Path.Combine(directory, ".owner"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "segment_*.tmp.*"))
                        File.Delete(file);
                    foreach (string file in Directory.EnumerateFiles(directory, "concat_*.txt"))
                        File.Delete(file);
                }
                File.Delete(Path.Combine(directory, ".owner"));
                Directory.Delete(directory, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
