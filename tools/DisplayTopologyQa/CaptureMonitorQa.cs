using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Captail;
using Captail.Interop;

internal static class CaptureMonitorQa
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("obs.dll")] private static extern nint obs_source_get_settings(nint source);
    [DllImport("obs.dll")] private static extern nint obs_data_get_string(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    internal static void Run()
    {
        var app = new Captail.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Captail;component/Languages/Strings.en.xaml") });
        var scheduler = Get<SingleThreadTaskScheduler>(app, "_obsTaskScheduler");
        try
        {
            RunCadence();
            RunTimeout();
            RunBusyWorker(app, scheduler);
            foreach (string source in new[] { "desktop", "game" }) RunNative(app, scheduler, source);
            Console.WriteLine("CAPTURE_MONITOR_QA PASS");
        }
        finally
        {
            Set(app, "_exiting", 1);
            Get<CaptureMonitor?>(app, "_captureMonitor")?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Get<CancellationTokenSource>(app, "_displayHdrMonitorStop").Cancel();
            Get<CancellationTokenSource>(app, "_displayHdrMonitorStop").Dispose();
            scheduler.Dispose();
        }
    }

    private static void RunCadence()
    {
        int calls = 0, health = 0, active = 0, overlaps = 0, wrongThread = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        using var monitor = new CaptureMonitor(async (check, token) =>
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlaps);
            if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref wrongThread);
            try
            {
                Interlocked.Increment(ref calls);
                if (check) Interlocked.Increment(ref health);
                await Task.Delay(12, token).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref active); }
        }, () => TimeSpan.Zero, _ => throw new InvalidOperationException("Unexpected timeout"),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(2));
        Wait(() => Volatile.Read(ref calls) >= 10 && Volatile.Read(ref health) >= 2,
            "Capture and health cadence advances without pumping WPF");
        monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Wait(() => Volatile.Read(ref active) == 0, "Cooperative in-flight poll observes shutdown cancellation");
        Check(wrongThread == 0 && overlaps == 0 && active == 0, "One background poll at a time, outside UI thread");
        int stopped = calls;
        Thread.Sleep(60);
        Check(calls == stopped, "Disposal stops capture and health callbacks");
    }

    private static void RunTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, timeouts = 0, canceled = 0;
        using var monitor = new CaptureMonitor(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            using var registration = token.Register(() => Interlocked.Exchange(ref canceled, 1));
            await release.Task.ConfigureAwait(false);
        }, () => TimeSpan.Zero, _ => Interlocked.Increment(ref timeouts),
            TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(120));
        try
        {
            Wait(() => Volatile.Read(ref timeouts) == 1, "Hung state probe has a finite deadline");
            Thread.Sleep(100);
            Check(calls == 1 && canceled == 1 && timeouts == 1, "Timed-out probe canceled and quarantined without queue growth");
            var timer = Stopwatch.StartNew();
            monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            Check(timer.Elapsed < TimeSpan.FromSeconds(1), "Shutdown does not wait for uncancelable native work");
        }
        finally { release.TrySetResult(); }
    }

    private static void RunBusyWorker(Captail.App app, SingleThreadTaskScheduler scheduler)
    {
        var gate = Get<SemaphoreSlim>(app, "_pipelineGate");
        using var release = new ManualResetEventSlim();
        gate.Wait();
        Task blocker = Task.Factory.StartNew(() => release.Wait(), CancellationToken.None,
            TaskCreationOptions.None, scheduler);
        int reports = 0;
        using var monitor = new CaptureMonitor((_, _) => Task.CompletedTask,
            () => scheduler.BusyDuration, _ => Interlocked.Increment(ref reports),
            TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        try
        {
            Wait(() => Volatile.Read(ref reports) == 1, "Watchdog detects busy OBS while UI and pipeline gate are blocked");
            Thread.Sleep(70);
            Check(reports == 1, "Busy worker reported once per stall");
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { release.Set(); blocker.GetAwaiter().GetResult(); gate.Release(); }
        Wait(() => scheduler.BusyDuration == TimeSpan.Zero, "OBS busy duration clears after work finishes");
    }

    private static void RunNative(Captail.App app, SingleThreadTaskScheduler scheduler, string source)
    {
        void Obs(Action action) => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, scheduler).GetAwaiter().GetResult();
        var physical = CaptureInterop.EnumerateMonitors().First();
        using var fixture = source == "desktop" ? new DesktopFixture(physical) : null;
        IReadOnlyList<CaptureInterop.MonitorInfo> topology = source == "desktop"
            ? [physical with { DeviceId = "CAPTAIL_QA_RETIRED_MONITOR" }] : [physical];
        var config = new Config
        {
            CaptureSource = source, CaptureMode = "recording", FrameRate = 30,
            RecordingResolution = "720p", Codec = "h264", BitrateMbps = 6,
            CaptureSystemAudio = false, CaptureMicrophone = false, ReplayEnabled = true,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "CaptailCaptureMonitorQa"),
        };
        Directory.CreateDirectory(config.OutputDirectory);
        using var engine = new ObsReplayEngine(config, true, () => Volatile.Read(ref topology));
        Set(app, "_config", config);
        Set(app, "_borderlessCaptureAccessPrepared", true);
        Set(app, "_windowsGraphicsCaptureReady", true);
        Obs(engine.Start);
        IsolateGame(engine, Obs);
        Set(app, "_obs", engine);
        Set(app, "_replayRunning", true);
        Set(app, "_pipelineStartedUtc", DateTime.UtcNow.AddSeconds(-10));
        Set(app, "_captureDescription", "QA stale description");
        Set(app, "_uiStateUpdatePending", 0);
        Invoke(app, "StartCaptureMonitor");
        var monitor = Get<CaptureMonitor>(app, "_captureMonitor");
        var gate = Get<SemaphoreSlim>(app, "_pipelineGate");
        try
        {
            if (source == "desktop")
            {
                nint output = Get<nint>(engine, "_output");
                Volatile.Write(ref topology, new[] { physical });
                string monitorId = "";
                Wait(() =>
                {
                    Obs(() => monitorId = ReadString(Get<nint>(engine, "_desktopVideoSource"), "monitor_id"));
                    return monitorId == physical.DeviceId;
                }, "Desktop monitor rebinds with WPF dispatcher blocked");
                Check(Get<nint>(engine, "_output") == output && engine.IsActive,
                    "Background monitor replacement retains recording output");
                fixture!.Present();
                Thread.Sleep(1500);
            }
            Set(app, "_captureDescription", "QA stale description");
            Wait(() => Get<string?>(app, "_captureDescription") != "QA stale description",
                $"{source}: health/state publication advances without UI callbacks");
            Check(Get<int>(app, "_uiStateUpdatePending") == 1, "UI state coalesced while presentation dispatcher is blocked");
            gate.Wait();
            try
            {
                Set(app, "_obs", null);
                Set(app, "_replayRunning", false);
                Obs(engine.Dispose);
                if (source == "desktop") Program.CheckDecodedFrame(engine.RecordingPath);
            }
            finally { gate.Release(); }
            Wait(() => Get<ObsReplayEngine?>(app, "_obs") is { } restored &&
                       !ReferenceEquals(restored, engine) && Get<bool>(app, "_replayRunning"),
                $"{source}: watchdog restarts missing pipeline without UI dispatcher", 12);
            var recovered = Get<ObsReplayEngine>(app, "_obs");
            IsolateGame(recovered, Obs);
            Check(Get<string?>(app, "_pendingUiError") is null, "Background recovery has no WPF cross-thread error");
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            gate.Wait(); gate.Release();
            RunQueuedCancellation(app, scheduler);
        }
        finally
        {
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Set(app, "_captureMonitor", null);
            Wait(() => Get<int>(app, "_recoveryInProgress") == 0, "Recovery completes before native teardown", 12);
            gate.Wait();
            try
            {
                var current = Get<ObsReplayEngine?>(app, "_obs");
                Set(app, "_obs", null);
                Set(app, "_replayRunning", false);
                if (current is not null) Obs(current.Dispose);
            }
            finally { gate.Release(); }
        }
    }

    private static void RunQueuedCancellation(Captail.App app, SingleThreadTaskScheduler scheduler)
    {
        var poll = (Func<CancellationToken, Task>)typeof(Captail.App)
            .GetMethod("RefreshAutomaticCaptureStateSafeAsync", Private)!
            .CreateDelegate(typeof(Func<CancellationToken, Task>), app);
        var gate = Get<SemaphoreSlim>(app, "_pipelineGate");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Task blocker = Task.Factory.StartNew(() => { entered.Set(); release.Wait(); },
            CancellationToken.None, TaskCreationOptions.None, scheduler);
        Check(entered.Wait(TimeSpan.FromSeconds(2)), "Synthetic OBS stall starts before queued-probe test");
        Set(app, "_captureDescription", "QA canceled probe");
        int reports = 0;
        using var monitor = new CaptureMonitor((_, token) => poll(token), () => TimeSpan.Zero,
            _ => Interlocked.Increment(ref reports), TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(120));
        Set(app, "_captureMonitor", monitor);
        try
        {
            Wait(() => Volatile.Read(ref reports) == 1, "Real App probe times out behind blocked OBS scheduler");
            Check(Get<int>(app, "_captureStateRefreshInProgress") == 1,
                "In-flight native probe remains owned until scheduler drains");
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            release.Set();
            blocker.GetAwaiter().GetResult();
            Check(gate.Wait(TimeSpan.FromSeconds(3)), "Canceled App probe releases pipeline gate after queue resumes");
            gate.Release();
            Set(app, "_captureMonitor", null);
        }
        Check(Get<string?>(app, "_captureDescription") == "QA canceled probe",
            "Canceled queued probe cannot publish stale capture state");
        Check(Get<int>(app, "_captureStateRefreshInProgress") == 0, "Canceled App probe drains without overlap");
    }

    private sealed class DesktopFixture : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<Window> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Window _window;

        internal DesktopFixture(CaptureInterop.MonitorInfo monitor)
        {
            _thread = new Thread(() =>
            {
                var window = new Window
                {
                    Title = "Captail background capture QA", Width = 640, Height = 360,
                    Left = monitor.Left + 60, Top = monitor.Top + 60,
                    Background = Brushes.LimeGreen, ShowInTaskbar = false,
                };
                window.Show();
                _ready.SetResult(window);
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _window = _ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }

        internal void Present() => _window.Dispatcher.Invoke(() => _window.Background = Brushes.Magenta);

        public void Dispose()
        {
            _window.Dispatcher.Invoke(() => { _window.Close(); _window.Dispatcher.InvokeShutdown(); });
            _thread.Join();
        }
    }

    private static void IsolateGame(ObsReplayEngine engine, Action<Action> obs) => obs(() =>
    {
        nint data = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(data, "capture_mode", "window");
            ObsNative.obs_data_set_string(data, "window", "Captail QA:no-window:no-process.exe");
            ObsNative.obs_source_update(Get<nint>(engine, "_gameVideoSource"), data);
        }
        finally { ObsNative.obs_data_release(data); }
    });
    private static string ReadString(nint source, string key)
    {
        nint data = obs_source_get_settings(source);
        try { return Marshal.PtrToStringUTF8(obs_data_get_string(data, key)) ?? ""; }
        finally { ObsNative.obs_data_release(data); }
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
    private static void Wait(Func<bool> predicate, string message, int seconds = 5)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(10);
        Check(predicate(), message);
    }
    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
