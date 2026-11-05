using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Captail;

internal static class StartupPermissionsQa
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        Check(!new Config().ReplayEnabled, "Fresh configuration starts with replay disabled");
        string directory = Path.Combine(Path.GetTempPath(), "CaptailStartupQa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (bool enabled in new[] { false, true })
        {
            string path = Path.Combine(directory, $"saved-{enabled}.json");
            File.WriteAllText(path, $"{{\"ReplayEnabled\":{enabled.ToString().ToLowerInvariant()}}}");
            object?[] arguments = [path, null];
            bool loaded = (bool)typeof(Config).GetMethod("TryLoad", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, arguments)!;
            Check(loaded && arguments[1] is Config saved && saved.ReplayEnabled == enabled,
                $"Saved replay preference {enabled} survives loading");
        }

        var app = new StartupTestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var scheduler = Get<SingleThreadTaskScheduler>(app, "_obsTaskScheduler");
        try
        {
            Set(app, "_config", new Config { ReplayEnabled = false });
            Invoke(app, true);
            Check(!Get<bool>(app, "_borderlessCaptureAccessPrepared"),
                "Disabled background startup does not trigger a permission prompt");
            Set(app, "_uiOnly", true);
            Invoke(app, false);
            Check(!Get<bool>(app, "_borderlessCaptureAccessPrepared"),
                "UI-only QA does not request capture permissions");
            Set(app, "_uiOnly", false);
            Invoke(app, false);
            Check(Get<bool>(app, "_borderlessCaptureAccessPrepared"),
                "Foreground startup prepares capture permission while replay is disabled");
            Check(!Get<Config>(app, "_config").ReplayEnabled &&
                  !Get<bool>(app, "_replayRunning") && Get<ObsReplayEngine?>(app, "_obs") is null,
                "Permission preflight leaves replay disabled and never creates an OBS pipeline");
            Invoke(app, false);
            Check(Get<bool>(app, "_borderlessCaptureAccessPrepared"),
                "Repeated startup preparation reuses completed permission preflight");
            Console.WriteLine("STARTUP_PERMISSIONS_QA PASS");
        }
        finally
        {
            Set(app, "_exiting", 1);
            Get<CancellationTokenSource>(app, "_displayHdrMonitorStop").Dispose();
            scheduler.Dispose();
        }
    }

    private static void Invoke(Captail.App app, bool background)
    {
        Task task = (Task)typeof(Captail.App).GetMethod("PrepareCapturePermissionsAtStartupAsync", Private)!
            .Invoke(app, [background])!;
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            var frame = new DispatcherFrame();
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Startup permission QA did not complete");
        task.GetAwaiter().GetResult();
    }

    private static T Get<T>(object instance, string name) =>
        (T)typeof(Captail.App).GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) =>
        typeof(Captail.App).GetField(name, Private)!.SetValue(instance, value);
    private sealed class StartupTestApp : Captail.App
    {
        // Pumping WPF must not launch the real application or load user settings.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
