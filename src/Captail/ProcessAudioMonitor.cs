using Captail.Interop;

namespace Captail;

internal enum ProcessAudioMonitorEventKind
{
    PersistentFailure,
    Recovered,
    RoutingConflict,
}

internal readonly record struct ProcessAudioMonitorEvent(
    ProcessAudioMonitorEventKind Kind,
    int Count,
    long ErrorCode = 0);

internal sealed class ProcessAudioHealthTracker
{
    private const int PersistentFailurePolls = 3;
    private int _consecutiveFailurePolls;
    private bool _failureReported;
    private int _lastConflictCount;

    internal IReadOnlyList<ProcessAudioMonitorEvent> Observe(
        ProcessAudioReconcileResult result)
    {
        List<ProcessAudioMonitorEvent> events = [];
        if (result.RuntimeFailedSources > 0 && result.ActiveSources > 0)
        {
            _consecutiveFailurePolls++;
            if (!_failureReported &&
                _consecutiveFailurePolls >= PersistentFailurePolls)
            {
                _failureReported = true;
                events.Add(new ProcessAudioMonitorEvent(
                    ProcessAudioMonitorEventKind.PersistentFailure,
                    result.RuntimeFailedSources,
                    result.LastErrorCode));
            }
        }
        else
        {
            _consecutiveFailurePolls = 0;
            if (_failureReported && result.RecoveredSources > 0)
            {
                _failureReported = false;
                events.Add(new ProcessAudioMonitorEvent(
                    ProcessAudioMonitorEventKind.Recovered,
                    result.RecoveredSources));
            }
            else if (result.ActiveSources == 0)
            {
                _failureReported = false;
            }
        }

        if (result.ConflictingSources > 0 &&
            result.ConflictingSources != _lastConflictCount)
        {
            events.Add(new ProcessAudioMonitorEvent(
                ProcessAudioMonitorEventKind.RoutingConflict,
                result.ConflictingSources));
        }
        _lastConflictCount = result.ConflictingSources;
        return events;
    }
}

internal sealed class ProcessAudioPollCadence
{
    internal static readonly TimeSpan SteadyInterval = TimeSpan.FromMilliseconds(1000);
    internal static readonly TimeSpan ReacquisitionInterval = TimeSpan.FromMilliseconds(250);
    private const int ReacquisitionPolls = 8;

    private int _remainingFastPolls;
    private int? _previousDesiredSources;

    internal TimeSpan NextInterval => _remainingFastPolls > 0
        ? ReacquisitionInterval
        : SteadyInterval;

    internal void Observe(ProcessAudioReconcileResult result)
    {
        bool topologyChanged =
            (_previousDesiredSources is int previous &&
             previous != result.DesiredSources) ||
            result.CreatedSources > 0 ||
            result.DestroyedSources > 0;
        _previousDesiredSources = result.DesiredSources;

        if (topologyChanged)
            _remainingFastPolls = ReacquisitionPolls;
        else if (_remainingFastPolls > 0)
            _remainingFastPolls--;
    }
}

internal sealed class ProcessAudioMonitor : IAsyncDisposable
{
    private readonly Func<ProcessSnapshot> _captureSnapshot;
    private readonly Func<ProcessSnapshot, Task<ProcessAudioReconcileResult>>
        _reconcile;
    private readonly Action<string>? _log;
    private readonly Action<ProcessAudioMonitorEvent>? _statusChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private int _disposed;

    internal ProcessAudioMonitor(
        Func<ProcessSnapshot> captureSnapshot,
        Func<ProcessSnapshot, Task<ProcessAudioReconcileResult>> reconcile,
        Action<string>? log = null,
        Action<ProcessAudioMonitorEvent>? statusChanged = null)
    {
        _captureSnapshot = captureSnapshot;
        _reconcile = reconcile;
        _log = log;
        _statusChanged = statusChanged;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var cadence = new ProcessAudioPollCadence();
        var health = new ProcessAudioHealthTracker();
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                ProcessSnapshot snapshot = _captureSnapshot();
                ProcessAudioReconcileResult result = await _reconcile(snapshot);
                cadence.Observe(result);
                foreach (ProcessAudioMonitorEvent status in health.Observe(result))
                    _statusChanged?.Invoke(status);
                if (result.CreatedSources > 0 ||
                    result.DestroyedSources > 0 ||
                    result.FailedSources > 0 ||
                    result.RecoveredSources > 0)
                {
                    _log?.Invoke(
                        $"Process audio reconciliation: desired={result.DesiredSources}, " +
                        $"active={result.ActiveSources}, created={result.CreatedSources}, " +
                        $"destroyed={result.DestroyedSources}, failed={result.FailedSources}, " +
                        $"runtimeFailed={result.RuntimeFailedSources}, " +
                        $"recovered={result.RecoveredSources}, " +
                        $"conflicts={result.ConflictingSources}.");
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log?.Invoke(
                    $"Process audio reconciliation failed ({exception.GetType().Name}).");
            }

            try
            {
                await Task.Delay(cadence.NextInterval, _cancellation.Token);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Cancel();
        try
        {
            await _worker;
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
