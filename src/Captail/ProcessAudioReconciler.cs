using Captail.Interop;

namespace Captail;

internal sealed record ProcessAudioReconcileResult(
    int DesiredSources,
    int ActiveSources,
    int CreatedSources,
    int DestroyedSources,
    int FailedSources);

internal sealed record ProcessAudioTarget(string Executable, int Track);

internal sealed record ActiveProcessAudioSource(nint Source, int Track);

internal sealed class ProcessAudioReconciler : IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Func<ProcessIdentity, int, nint> _createSource;
    private readonly Action<nint> _destroySource;
    private readonly Action<string>? _log;
    private readonly Dictionary<ProcessIdentity, ActiveProcessAudioSource>
        _activeSources = [];
    private bool _disposed;

    internal ProcessAudioReconciler(
        Func<ProcessIdentity, int, nint> createSource,
        Action<nint> destroySource,
        Action<string>? log = null)
    {
        _createSource = createSource;
        _destroySource = destroySource;
        _log = log;
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

        ProcessNode[] desiredRoots = snapshot
            .SelectIndependentRoots(targets.Keys)
            .ToArray();
        var desired = desiredRoots.ToDictionary(
            node => node.Identity,
            node => targets[node.Executable]);

        int destroyed = 0;
        int failed = 0;
        ProcessIdentity[] obsolete = _activeSources.Keys
            .Where(identity =>
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
        foreach (ProcessNode root in desiredRoots)
        {
            if (_activeSources.ContainsKey(root.Identity))
                continue;
            try
            {
                int track = desired[root.Identity];
                nint source = _createSource(root.Identity, track);
                if (source == 0)
                    throw new InvalidOperationException("Native source creation returned null.");
                _activeSources.Add(
                    root.Identity,
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
            failed);
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
}
