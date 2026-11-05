using System.Diagnostics;

namespace Captail;

/// <summary>Runs capture observation and watchdog checks without a WPF dispatcher.</summary>
internal sealed class CaptureMonitor : IDisposable, IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, Task> _poll;
    private readonly Func<TimeSpan> _obsBusyDuration;
    private readonly Action<string> _timedOut;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _healthInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly TimeSpan _obsBusyTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private int _disposed;

    internal CaptureMonitor(Func<bool, CancellationToken, Task> poll,
        Func<TimeSpan> obsBusyDuration, Action<string> timedOut,
        TimeSpan? interval = null, TimeSpan? healthInterval = null,
        TimeSpan? pollTimeout = null, TimeSpan? obsBusyTimeout = null)
    {
        _poll = poll;
        _obsBusyDuration = obsBusyDuration;
        _timedOut = timedOut;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
        _healthInterval = healthInterval ?? TimeSpan.FromSeconds(2);
        _pollTimeout = pollTimeout ?? TimeSpan.FromSeconds(8);
        _obsBusyTimeout = obsBusyTimeout ?? TimeSpan.FromSeconds(30);
        if (_interval <= TimeSpan.Zero || _healthInterval <= TimeSpan.Zero ||
            _pollTimeout <= TimeSpan.Zero || _obsBusyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        CancellationToken stopping = _stopping.Token;
        using var timer = new PeriodicTimer(_interval);
        long lastHealthCheck = Stopwatch.GetTimestamp();
        bool busyReported = false;
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                try
                {
                    if (_obsBusyDuration() >= _obsBusyTimeout)
                    {
                        if (!busyReported)
                        {
                            busyReported = true;
                            ReportTimeout("OBS worker exceeded its capture-monitor deadline.");
                        }
                        continue;
                    }
                    busyReported = false;
                    bool checkHealth = Stopwatch.GetElapsedTime(lastHealthCheck) >= _healthInterval;
                    if (checkHealth) lastHealthCheck = Stopwatch.GetTimestamp();
                    using var pollStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    CancellationToken polling = pollStopping.Token;
                    // A synchronous probe must not prevent its own deadline from firing.
                    Task poll = Task.Run(() => _poll(checkHealth, polling), CancellationToken.None);
                    _ = poll.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    try { await poll.WaitAsync(_pollTimeout, stopping).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        pollStopping.Cancel();
                        if (!stopping.IsCancellationRequested)
                            ReportTimeout("Capture state probe exceeded its OBS-queue deadline.");
                        // Native work cannot be aborted. Quarantine this probe rather
                        // than queuing more work or starting another OBS context.
                        await poll.WaitAsync(stopping).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Log.Event(DiagnosticLevel.Warning, "Capture", "capture.monitor.failed",
                        new { exception.Message });
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private void ReportTimeout(string detail)
    {
        try { _timedOut(detail); }
        catch (Exception exception)
        {
            Log.Event(DiagnosticLevel.Error, "Capture", "capture.monitor.timeout-handler-failed",
                new { exception.Message });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _ = _worker.ContinueWith(_ => _stopping.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }
}
