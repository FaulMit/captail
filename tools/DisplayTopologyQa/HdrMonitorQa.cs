using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Captail;
using Captail.Interop;

internal static class HdrMonitorQa
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
        TaskScheduler scheduler = Get<TaskScheduler>(app, "_obsTaskScheduler");
        try
        {
            using (var physicalMonitor = new DisplayHdrMonitor())
            {
                physicalMonitor.StartAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                foreach (var display in CaptureInterop.EnumerateMonitors())
                {
                    var observed = physicalMonitor.Read(display.Handle);
                    var direct = DisplayColorState.ReadHdrState(display.Handle);
                    Check(observed.HasValue && direct.HasValue &&
                        observed.Value.UserEnabled == direct.Value.UserEnabled &&
                        observed.Value.Active == direct.Value.Active,
                        $"Background Windows snapshot matches physical display {display.Index + 1}");
                }
            }
            foreach (string source in new[] { "desktop", "game" }) RunSource(app, scheduler, source);
            Console.WriteLine("HDR_MONITOR_QA PASS");
        }
        finally
        {
            Set(app, "_exiting", 1);
            typeof(Captail.App).GetMethod("StopDisplayHdrMonitor", Private)!.Invoke(app, null);
            ((IDisposable)scheduler).Dispose();
            Get<CancellationTokenSource>(app, "_displayHdrMonitorStop").Dispose();
        }
    }

    private static void RunSource(Captail.App app, TaskScheduler scheduler, string source)
    {
        void Obs(Action action) => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, scheduler).GetAwaiter().GetResult();
        var physical = CaptureInterop.EnumerateMonitors().First();
        IReadOnlyList<CaptureInterop.MonitorInfo> displays = [physical];
        int stateIndex = 3, calls = 0, readerThread = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        using var monitor = new DisplayHdrMonitor(() => Volatile.Read(ref displays), _ =>
        {
            Interlocked.Increment(ref calls);
            Volatile.Write(ref readerThread, Environment.CurrentManagedThreadId);
            int index = Volatile.Read(ref stateIndex);
            if (index == -2) throw new InvalidOperationException("Injected display API failure");
            return index < 0 ? null : new DisplayColorState.HdrState((index & 2) != 0, (index & 1) != 0);
        }, TimeSpan.FromMilliseconds(15));
        Set(app, "_displayHdrMonitor", monitor);
        var changed = (Action)typeof(Captail.App).GetMethod("OnDisplayHdrChanged", Private)!.CreateDelegate(typeof(Action), app);
        monitor.Changed += changed;
        monitor.StartAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Check(readerThread != callerThread, "Windows HDR query runs outside blocked UI thread");
        int idleCalls = Volatile.Read(ref calls);
        Wait(() => Volatile.Read(ref calls) >= idleCalls + 3, "Observation continues with capture disabled");
        var config = new Config
        {
            CaptureSource = source, CaptureMode = "recording", FrameRate = 30,
            RecordingResolution = "720p", CaptureSystemAudio = false, CaptureMicrophone = false,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "CaptailHdrMonitorQa"),
        };
        Directory.CreateDirectory(config.OutputDirectory);
        using var engine = new ObsReplayEngine(config, true, () => [physical], monitor.Read);
        var gate = Get<SemaphoreSlim>(app, "_pipelineGate");
        bool gateHeld = false;
        try
        {
            Obs(engine.Start);
            nint game = Get<nint>(engine, "_gameVideoSource");
            Obs(() =>
            {
                nint data = ObsNative.obs_data_create();
                try
                {
                    ObsNative.obs_data_set_string(data, "capture_mode", "window");
                    ObsNative.obs_data_set_string(data, "window", "Captail HDR QA:no-window:no-process.exe");
                    ObsNative.obs_source_update(game, data);
                }
                finally { ObsNative.obs_data_release(data); }
            });
            Set(app, "_obs", engine);
            string Color() { string color = ""; Obs(() => color = ReadString(game, "rgb10a2_space")); return color; }
            void Sample(int index)
            {
                long revision = monitor.Read(physical.Handle)!.Value.Revision;
                Volatile.Write(ref stateIndex, index);
                Wait(() => monitor.Read(physical.Handle)?.Revision > revision, "Observer receives display transition");
            }
            Check(Color() == "2100pq", $"{source}: startup consumes initial background HDR snapshot");
            gate.Wait(); gateHeld = true;
            int before = Volatile.Read(ref calls);
            Sample(2); // Active HDR disappears, Windows Use HDR remains on.
            Sample(3); // Active HDR returns while OBS application remains blocked.
            Thread.Sleep(80);
            Check(Volatile.Read(ref calls) > before + 2, "HDR sampling continues while UI and capture gate are blocked");
            Check(Color() == "2100pq", "Background observer never mutates OBS behind capture gate");
            gate.Release(); gateHeld = false;
            Wait(() => Color() == "srgb", "Missed inactive/active pair still locks SDR after queue resumes");
            gate.Wait(); gateHeld = true;
            Sample(0);
            Sample(3);
            Check(Color() == "srgb", "Blocked OBS retains current color until serialized application");
            gate.Release(); gateHeld = false;
            Wait(() => Color() == "2100pq", "Windows off/on edge survives coalescing with same final HDR flags");
            gate.Wait(); gateHeld = true;
            Sample(0);
            Sample(2);
            gate.Release(); gateHeld = false;
            Wait(() => Color() == "srgb", "Pending HDR enable waits for active output");
            Sample(3);
            Wait(() => Color() == "2100pq", "Delayed active output completes retained enable request");
            var known = monitor.Read(physical.Handle);
            Volatile.Write(ref stateIndex, -1);
            before = Volatile.Read(ref calls);
            Wait(() => Volatile.Read(ref calls) >= before + 3, "Unknown display samples retried in background");
            Check(monitor.Read(physical.Handle) == known && Color() == "2100pq", "Unknown samples retain known mode and history");
            Volatile.Write(ref stateIndex, -2);
            before = Volatile.Read(ref calls);
            Wait(() => Volatile.Read(ref calls) >= before + 3, "Display API exception does not stop worker");
            Check(monitor.Read(physical.Handle) == known, "Failed query retains complete snapshot");
            Volatile.Write(ref stateIndex, 3);
            var replacement = physical with { Handle = new nint(-123), Index = 1 };
            Volatile.Write(ref displays, new[] { replacement });
            Wait(() => monitor.Read(replacement.Handle).HasValue, "Replacement monitor handle published independently of capture mode");
            Check(monitor.Read(replacement.Handle) == known, "Stable monitor identity retains transition history after handle replacement");
            Check(monitor.Read(physical.Handle) is null, "Retired handle removed from snapshot");
            Wait(() => Get<int>(app, "_displayHdrApplyInProgress") == 0, "Application drains coalesced HDR updates");
            if (source == "game")
            {
                gate.Wait(); gateHeld = true;
                Volatile.Write(ref displays, new[] { physical });
                Wait(() => monitor.Read(physical.Handle).HasValue, "Observer restores original display handle");
                Sample(0);
                Wait(() => Get<int>(app, "_displayHdrApplyInProgress") != 0, "HDR apply waits behind busy capture gate");
                typeof(Captail.App).GetMethod("StopDisplayHdrMonitor", Private)!.Invoke(app, null);
                Wait(() => Get<int>(app, "_displayHdrApplyInProgress") == 0, "Shutdown cancels pending HDR apply without waiting for capture gate");
                Check(Color() == "2100pq", "Canceled HDR update cannot mutate recording during teardown");
                gate.Release(); gateHeld = false;
            }
        }
        finally
        {
            if (gateHeld) gate.Release();
            monitor.Changed -= changed;
            monitor.Dispose();
            Set(app, "_displayHdrMonitor", null);
            gate.Wait();
            try { Set(app, "_obs", null); Obs(engine.Dispose); }
            finally { gate.Release(); }
            int stoppedCalls = Volatile.Read(ref calls);
            Thread.Sleep(60);
            Check(Volatile.Read(ref calls) == stoppedCalls, "Disposed observer stops querying Windows");
        }
    }

    private static string ReadString(nint source, string key)
    {
        nint data = obs_source_get_settings(source);
        try { return Marshal.PtrToStringUTF8(obs_data_get_string(data, key)) ?? ""; }
        finally { ObsNative.obs_data_release(data); }
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Wait(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(4)) Thread.Sleep(5);
        Check(predicate(), message);
    }
    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
