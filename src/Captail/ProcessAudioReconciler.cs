using Captail.Interop;

namespace Captail;

internal sealed record ProcessAudioReconcileResult(
    int DesiredSources,
    int ActiveSources,
    int CreatedSources,
    int DestroyedSources,
    int FailedSources)
{
    internal int RuntimeFailedSources { get; init; }
    internal int RecoveredSources { get; init; }
    internal int ConflictingSources { get; init; }
    internal long LastErrorCode { get; init; }
}

internal sealed record ProcessAudioTarget(string Executable, int Track);

internal enum ProcessAudioSourceState : long
{
    Idle,
    Starting,
    Capturing,
    TargetExited,
    ActivationFailed,
    CaptureFailed,
    Stopped,
}

internal readonly record struct ProcessAudioSourceStatus(
    ProcessAudioSourceState State,
    long ErrorCode);

internal sealed record ActiveProcessAudioSource(
    nint Source,
    int Track,
    ProcessAudioSourceState LastState = ProcessAudioSourceState.Starting);

internal sealed class ProcessAudioReconciler : IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Func<ProcessIdentity, int, nint> _createSource;
    private readonly Action<nint> _destroySource;
    private readonly Action<string>? _log;
    private readonly Func<nint, ProcessAudioSourceStatus>? _readSourceStatus;
    private readonly Dictionary<ProcessIdentity, ActiveProcessAudioSource>
        _activeSources = [];
    private bool _disposed;

    internal ProcessAudioReconciler(
        Func<ProcessIdentity, int, nint> createSource,
        Action<nint> destroySource,
        Action<string>? log = null,
        Func<nint, ProcessAudioSourceStatus>? readSourceStatus = null)
    {
        _createSource = createSource;
        _destroySource = destroySource;
        _log = log;
        _readSourceStatus = readSourceStatus;
    }

    internal IReadOnlyCollection<ProcessIdentity> ActiveIdentities =>
        _activeSources.Keys.ToArray();

    internal IReadOnlyDictionary<ProcessIdentity, int> ActiveTracks =>
        _activeSources.ToDictionary(item => item.Key, item => item.Value.Track);

    internal ProcessAudioReconcileResult Reconcile(
        ProcessSnapshot snapshot,
        IEnumerable<ProcessAudioTarget> executableTargets)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessAudioTarget target in executableTargets)
        {
            if (target.Track is >= 1 and <= 6)
                targets.TryAdd(target.Executable, target.Track);
        }

        RoutedProcessSelection selection = snapshot.SelectRoutedRoots(targets);
        RoutedProcessRoot[] desiredRoots = selection.Roots.ToArray();
        var desired = desiredRoots.ToDictionary(
            root => root.Node.Identity,
            root => root.Track);

        int destroyed = 0;
        int failed = 0;
        int runtimeFailed = 0;
        int recovered = 0;
        long lastErrorCode = 0;
        var unhealthy = new HashSet<ProcessIdentity>();
        if (_readSourceStatus is not null)
        {
            foreach ((ProcessIdentity identity, ActiveProcessAudioSource active) in
                     _activeSources.ToArray())
            {
                try
                {
                    ProcessAudioSourceStatus status = _readSourceStatus(active.Source);
                    bool wasFailed = IsRuntimeFailure(active.LastState);
                    bool isFailed = IsRuntimeFailure(status.State);
                    if (isFailed)
                    {
                        runtimeFailed++;
                        lastErrorCode = status.ErrorCode;
                        if (!wasFailed || active.LastState != status.State)
                        {
                            _log?.Invoke(
                                $"Process audio source entered {status.State} " +
                                $"(HRESULT=0x{unchecked((uint)status.ErrorCode):X8}).");
                        }
                    }
                    else if (wasFailed && status.State == ProcessAudioSourceState.Capturing)
                    {
                        recovered++;
                        _log?.Invoke("Process audio source recovered.");
                    }

                    if (status.State is ProcessAudioSourceState.TargetExited or
                        ProcessAudioSourceState.Stopped)
                    {
                        unhealthy.Add(identity);
                    }
                    _activeSources[identity] = active with { LastState = status.State };
                }
                catch (Exception exception)
                {
                    failed++;
                    _log?.Invoke(
                        $"Process audio source status failed ({exception.GetType().Name}).");
                }
            }
        }

        ProcessIdentity[] obsolete = _activeSources.Keys
            .Where(identity =>
                unhealthy.Contains(identity) ||
                !desired.TryGetValue(identity, out int track) ||
                _activeSources[identity].Track != track)
            .OrderBy(identity => identity.CreationTime)
            .ThenBy(identity => identity.ProcessId)
            .ToArray();
        foreach (ProcessIdentity identity in obsolete)
        {
            nint source = _activeSources[identity].Source;
            _activeSources.Remove(identity);
            try
            {
                _destroySource(source);
                destroyed++;
            }
            catch (Exception exception)
            {
                failed++;
                _log?.Invoke(
                    $"Process audio source cleanup failed ({exception.GetType().Name}).");
            }
        }

        int created = 0;
        foreach (RoutedProcessRoot root in desiredRoots)
        {
            if (_activeSources.ContainsKey(root.Node.Identity))
                continue;
            try
            {
                int track = root.Track;
                nint source = _createSource(root.Node.Identity, track);
                if (source == 0)
                    throw new InvalidOperationException("Native source creation returned null.");
                _activeSources.Add(
                    root.Node.Identity,
                    new ActiveProcessAudioSource(source, track));
                created++;
            }
            catch (Exception exception)
            {
                failed++;
                _log?.Invoke(
                    $"Process audio source creation failed ({exception.GetType().Name}).");
            }
        }

        return new ProcessAudioReconcileResult(
            desired.Count,
            _activeSources.Count,
            created,
            destroyed,
            failed)
        {
            ConflictingSources = selection.ConflictingSources,
            RuntimeFailedSources = runtimeFailed,
            RecoveredSources = recovered,
            LastErrorCode = lastErrorCode,
        };
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_disposed)
            return;
        _disposed = true;

        foreach (ActiveProcessAudioSource active in _activeSources.Values.Reverse())
        {
            try
            {
                _destroySource(active.Source);
            }
            catch (Exception exception)
            {
                _log?.Invoke(
                    $"Process audio source cleanup failed ({exception.GetType().Name}).");
            }
        }
        _activeSources.Clear();
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Process audio sources must be reconciled on their owning OBS thread.");
        }
    }

    private static bool IsRuntimeFailure(ProcessAudioSourceState state) =>
        state is ProcessAudioSourceState.ActivationFailed or
            ProcessAudioSourceState.CaptureFailed;
}
