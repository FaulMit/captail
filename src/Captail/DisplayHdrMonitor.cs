using Captail.Interop;

namespace Captail;

/// <summary>Observes Windows display state without using WPF or the OBS queue.</summary>
internal sealed class DisplayHdrMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<CaptureInterop.MonitorInfo>> _enumerate;
    private readonly Func<nint, DisplayColorState.HdrState?> _read;
    private readonly TimeSpan _interval;
    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Dictionary<string, DisplayColorState.HdrState> _history = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<nint, DisplayColorState.HdrState> _snapshot = [];
    private long _revision;
    private long _version;
    private int _started;
    private int _disposed;
    private bool _queryFailed;

    internal DisplayHdrMonitor(
        Func<IReadOnlyList<CaptureInterop.MonitorInfo>>? enumerate = null,
        Func<nint, DisplayColorState.HdrState?>? read = null,
        TimeSpan? interval = null)
    {
        _enumerate = enumerate ?? CaptureInterop.EnumerateMonitors;
        _read = read ?? DisplayColorState.ReadHdrState;
        _interval = interval ?? TimeSpan.FromMilliseconds(250);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _thread = new Thread(Run) { IsBackground = true, Name = "Captail Windows HDR" };
    }

    internal event Action? Changed;
    internal long Version => Volatile.Read(ref _version);
    internal DisplayColorState.HdrState? Read(nint monitor) =>
        Volatile.Read(ref _snapshot).TryGetValue(monitor, out var state) ? state : null;

    internal Task StartAsync()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started == 0) { _started = 1; _thread.Start(); }
            return _ready.Task;
        }
    }

    private void Run()
    {
        try
        {
            while (!_stop.IsSet)
            {
                try { Sample(); }
                catch (Exception exception)
                {
                    if (!_queryFailed)
                        Log.Event(DiagnosticLevel.Warning, "Capture", "display.hdr.query-failed",
                            new { exception.Message });
                    _queryFailed = true;
                }
                _ready.TrySetResult();
                if (_stop.Wait(_interval)) break;
            }
        }
        finally { _ready.TrySetCanceled(); _stop.Dispose(); }
    }

    private void Sample()
    {
        var next = new Dictionary<nint, DisplayColorState.HdrState>();
        foreach (var monitor in _enumerate())
        {
            string identity = string.IsNullOrWhiteSpace(monitor.DeviceId)
                ? $"handle:{monitor.Handle:X}" : monitor.DeviceId;
            bool known = _history.TryGetValue(identity, out var previous);
            var current = _read(monitor.Handle);
            if (current is not { } state)
            {
                if (known) next[monitor.Handle] = previous;
                continue;
            }
            if (!known || previous.UserEnabled != state.UserEnabled || previous.Active != state.Active)
            {
                long revision = ++_revision;
                state = state with
                {
                    Revision = revision,
                    LastSdrRevision = !state.UserEnabled || !state.Active
                        ? revision : previous.LastSdrRevision,
                    LastEnableRevision = known && !previous.UserEnabled && state.UserEnabled
                        ? revision : previous.LastEnableRevision,
                };
                _history[identity] = state;
                Log.Event(DiagnosticLevel.Info, "Capture", "display.hdr.observed", new
                {
                    monitor = $"0x{monitor.Handle:X}", device = identity,
                    state.UserEnabled, state.Active, state.Revision,
                    state.LastSdrRevision, state.LastEnableRevision,
                });
            }
            else state = previous;
            next[monitor.Handle] = state;
        }
        _queryFailed = false;
        var previousSnapshot = Volatile.Read(ref _snapshot);
        if (next.Count == previousSnapshot.Count &&
            next.All(pair => previousSnapshot.TryGetValue(pair.Key, out var value) && value == pair.Value))
            return;
        // Publish only complete, immutable-after-publication dictionaries.
        Volatile.Write(ref _snapshot, next);
        Interlocked.Increment(ref _version);
        if (!_stop.IsSet) Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _ready.TrySetCanceled();
            _stop.Set();
            if (_started == 0)
            {
                _ready.TrySetCanceled();
                _stop.Dispose();
                return;
            }
        }
        if (!ReferenceEquals(Thread.CurrentThread, _thread))
            _thread.Join(TimeSpan.FromSeconds(2));
    }
}
