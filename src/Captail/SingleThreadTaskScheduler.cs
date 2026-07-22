using System.Collections.Concurrent;

namespace Captail;

internal sealed class SingleThreadTaskScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread _thread;
    private int _disposed;

    internal SingleThreadTaskScheduler(string threadName)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    protected override IEnumerable<Task>? GetScheduledTasks() =>
        _tasks.ToArray();

    protected override void QueueTask(Task task)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            _tasks.Add(task);
        }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SingleThreadTaskScheduler));
        }
    }

    protected override bool TryExecuteTaskInline(
        Task task,
        bool taskWasPreviouslyQueued) =>
        !taskWasPreviouslyQueued &&
        ReferenceEquals(Thread.CurrentThread, _thread) &&
        TryExecuteTask(task);

    private void Run()
    {
        foreach (Task task in _tasks.GetConsumingEnumerable())
            TryExecuteTask(task);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _tasks.CompleteAdding();
        if (!ReferenceEquals(Thread.CurrentThread, _thread))
            _thread.Join(TimeSpan.FromSeconds(5));
        // Do not dispose the collection after a timed-out join: worker may still
        // be completing a native libobs call and must finish enumeration safely.
    }
}
