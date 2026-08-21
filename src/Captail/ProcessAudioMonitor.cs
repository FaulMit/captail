using Captail.Interop;

namespace Captail;

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
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private int _disposed;

    internal ProcessAudioMonitor(
        Func<ProcessSnapshot> captureSnapshot,
        Func<ProcessSnapshot, Task<ProcessAudioReconcileResult>> reconcile,
        Action<string>? log = null)
    {
        _captureSnapshot = captureSnapshot;
        _reconcile = reconcile;
        _log = log;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var cadence = new ProcessAudioPollCadence();
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                ProcessSnapshot snapshot = _captureSnapshot();
                ProcessAudioReconcileResult result = await _reconcile(snapshot);
                cadence.Observe(result);
                if (result.CreatedSources > 0 ||
                    result.DestroyedSources > 0 ||
                    result.FailedSources > 0)
                {
                    _log?.Invoke(
                        $"Process audio reconciliation: desired={result.DesiredSources}, " +
                        $"active={result.ActiveSources}, created={result.CreatedSources}, " +
                        $"destroyed={result.DestroyedSources}, failed={result.FailedSources}.");
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
