using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Captail;
using Captail.Interop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [DllImport("obs.dll")]
    private static extern nint obs_source_get_settings(nint source);
    [DllImport("obs.dll")]
    private static extern nint obs_data_get_string(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll")]
    private static extern long obs_data_get_int(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll")]
    private static extern void obs_sceneitem_get_bounds(nint item, out ObsNative.Vec2 bounds);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--startup-permissions-only"))
        {
            try { StartupPermissionsQa.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Contains("--capture-monitor-only"))
        {
            try { CaptureMonitorQa.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Contains("--game-selection-only"))
        {
            try { GameCaptureSelectionQa.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Contains("--hdr-monitor-only"))
        {
            try { HdrMonitorQa.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Captail;component/Languages/Strings.en.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Captail;component/Themes/Theme.xaml"),
        });
        bool passed = true;
        if (args.Contains("--hdr-only"))
        {
            try { HdrCaptureQa.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Contains("--selection-only"))
        {
            try { RunSelection(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (!args.Contains("--overlay-only"))
        {
            try { RunSelection(); RunCapture(args.Contains("--recording"), args.Contains("--wgc")); }
            catch (Exception error) { Console.Error.WriteLine($"CAPTURE FAIL: {error}"); passed = false; }
        }
        try { RunOverlay(); }
        catch (Exception error) { Console.Error.WriteLine($"OVERLAY FAIL: {error}"); passed = false; }
        Console.WriteLine($"DISPLAY_TOPOLOGY_QA {(passed ? "PASS" : "FAIL")}");
        return passed ? 0 : 1;
    }

    private static void RunSelection()
    {
        foreach (int build in new[] { 10240, 17763, 18362, 19041, 19044, 19045, 21999 })
        {
            foreach (bool wgcReady in new[] { false, true })
                Check(ObsReplayEngine.RecommendedMonitorCaptureMethod(new Version(10, 0, build), wgcReady) == 1,
                    $"Windows 10 build {build} forces DXGI with WGC ready={wgcReady}");
        }
        foreach (int build in new[] { 22000, 22621, 26100 })
        {
            Check(ObsReplayEngine.RecommendedMonitorCaptureMethod(new Version(10, 0, build), true) == 2,
                $"Windows 11 build {build} retains WGC after preflight");
            Check(ObsReplayEngine.RecommendedMonitorCaptureMethod(new Version(10, 0, build), false) == 1,
                $"Windows 11 build {build} forces DXGI after failed borderless preflight");
        }
        Check(ObsReplayEngine.RecommendedMonitorCaptureMethod(new Version(11, 0, 0), true) == 2,
            "Future Windows major versions retain WGC preference");
        var selected = new CaptureInterop.MonitorInfo(new nint(1), 3840, 2160, 0, "4K", 0, 0);
        var other = new CaptureInterop.MonitorInfo(new nint(2), 2560, 1440, 1, "OTHER", 3840, 0);
        var dual = new CaptureInterop.MonitorInfo(new nint(3), 1920, 1080, 1, "1080P", 0, 0);
        Check(ObsReplayEngine.ResolveCaptureMonitor([other with { Index = 0 }, selected with { Index = 1 }],
            selected, selected, 0)?.DeviceId == "4K", "monitor identity survives enumeration reordering");
        Check(ObsReplayEngine.ResolveCaptureMonitor([other with { Index = 0 }, dual],
            selected, selected, 0)?.DeviceId == "1080P", "dual-mode replacement wins over stale index");
        var leftDisplay = selected with { Left = -3840 };
        Check(ObsReplayEngine.ResolveCaptureMonitor([other with { Index = 0, Left = 0 }, dual with { Left = -1920 }],
            leftDisplay, leftDisplay, 0)?.DeviceId == "1080P", "dual-mode display remains selected when its desktop origin moves");
        Check(ObsReplayEngine.ResolveCaptureMonitor([other], selected, selected, 0)?.DeviceId == "OTHER",
            "remaining monitor becomes fallback when selected display disappears");
        Check(ObsReplayEngine.ResolveCaptureMonitor([other, selected], selected, other, 1)?.DeviceId == "4K",
            "preferred display restored after fallback");
        Check(ObsReplayEngine.ResolveCaptureMonitor([], selected, selected, 0) is null,
            "empty topology remains recoverable");
    }

    private static void RunCapture(bool recording, bool wgc)
    {
        var live = CaptureInterop.EnumerateMonitors().First();
        IReadOnlyList<CaptureInterop.MonitorInfo> monitors =
            [live with { DeviceId = "CAPTAIL_QA_RETIRED_MONITOR", Handle = new nint(-123), Width = 3840, Height = 2160 }];
        var config = new Config
        {
            FrameRate = 30, RecordingResolution = "720p", BufferSeconds = 30,
            CaptureSystemAudio = false, CaptureMicrophone = false,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "CaptailDisplayTopologyQa"),
            CaptureMode = recording ? "recording" : "replay",
        };
        Directory.CreateDirectory(config.OutputDirectory);
        using var fixture = new FixtureWindow(live);
        using var engine = new ObsReplayEngine(config, wgc, () => monitors);
        engine.Start();
        long expectedMethod = ObsReplayEngine.RecommendedMonitorCaptureMethod(Environment.OSVersion.Version, wgc);
        Check(ReadCaptureMethod(engine) == expectedMethod,
            $"native desktop source uses explicit backend {expectedMethod} after preflight ready={wgc}");
        nint output = Get<nint>(engine, "_output");
        Check(ReadMonitorId(engine) == "CAPTAIL_QA_RETIRED_MONITOR", "initial source retains disappeared monitor ID");
        monitors = [live];
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(3))
        {
            engine.RefreshCaptureState();
            if (ReadMonitorId(engine) == live.DeviceId) break;
            Thread.Sleep(100);
        }
        Check(ReadMonitorId(engine) == live.DeviceId, "desktop source rebinds after monitor identity/handle replacement");
        Check(ReadCaptureMethod(engine) == expectedMethod,
            "monitor replacement preserves the explicit capture backend");
        Check(Get<nint>(engine, "_output") == output && engine.IsActive, "replay output survives monitor replacement");
        wait.Restart();
        while (wait.Elapsed < TimeSpan.FromSeconds(5) &&
               ObsNative.obs_source_get_width(Get<nint>(engine, "_desktopVideoSource")) == 0)
            Thread.Sleep(100);
        Check(ObsNative.obs_source_get_width(Get<nint>(engine, "_desktopVideoSource")) > 0,
            "replacement source delivers live desktop video");
        obs_sceneitem_get_bounds(Get<nint>(engine, "_desktopVideoSceneItem"), out var bounds);
        Check(bounds.X == 3840 && bounds.Y == 2160, "replacement desktop fills original recording canvas");
        nint source = Get<nint>(engine, "_desktopVideoSource");
        monitors = [live with { Index = 1 }];
        RefreshUntil(engine, () => engine.CaptureMonitorIndex == 1);
        Check(Get<nint>(engine, "_desktopVideoSource") == source, "index-only change keeps existing capture source");
        monitors = [];
        engine.RefreshCaptureState();
        Check(engine.IsActive && Get<nint>(engine, "_output") == output, "temporary empty topology preserves active output");
        monitors = [live];
        RefreshUntil(engine, () => Get<nint>(engine, "_desktopVideoSource") != source);
        Check(ReadMonitorId(engine) == live.DeviceId, "capture recovers when monitor returns after empty topology");
        source = Get<nint>(engine, "_desktopVideoSource");
        monitors = [live with { Handle = new nint(-321), Width = 1920, Height = 1080 }];
        RefreshUntil(engine, () => Get<nint>(engine, "_desktopVideoSource") != source);
        Check(engine.IsActive && Get<nint>(engine, "_output") == output, "resolution/handle change keeps recording output active");
        source = Get<nint>(engine, "_desktopVideoSource");
        monitors = [live];
        RefreshUntil(engine, () => Get<nint>(engine, "_desktopVideoSource") != source);
        Check(ReadMonitorId(engine) == live.DeviceId, "switching back restores live source");
        int frames = engine.EncodedFrameCount;
        wait.Restart();
        while (wait.Elapsed < TimeSpan.FromSeconds(2)) { engine.RefreshCaptureState(); Thread.Sleep(100); }
        Check(engine.EncodedFrameCount > frames, "encoded frames progress after repeated transitions");
        string file = recording ? engine.RecordingPath : engine.SaveReplayAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        if (recording) engine.Dispose();
        Check(File.Exists(file), "capture saves usable file after topology transitions");
        CheckDecodedFrame(file);
    }

    private static void RefreshUntil(ObsReplayEngine engine, Func<bool> complete)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(3))
        {
            engine.RefreshCaptureState();
            if (complete()) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Monitor transition timed out.");
    }

    internal static void CheckDecodedFrame(string file)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-v", "error", "-sseof", "-0.5", "-i", file,
                     "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var pixels = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pixels);
        process.WaitForExit();
        Check(process.ExitCode == 0 && pixels.Length == 160 * 90 * 3,
            $"saved file decodes (exit={process.ExitCode}, bytes={pixels.Length}, {errors.GetAwaiter().GetResult().Trim()})");
        Check(pixels.ToArray().Average(value => (double)value) > 2,
            "saved frame contains desktop pixels rather than black output");
    }

    private sealed class FixtureWindow : IDisposable
    {
        private readonly Window _window;
        public FixtureWindow(CaptureInterop.MonitorInfo monitor)
        {
            _window = new Window
            {
                Title = "Captail display topology QA", Width = 480, Height = 270,
                Left = monitor.Left + 60, Top = monitor.Top + 60,
                Background = Brushes.LimeGreen, ShowInTaskbar = false,
            };
            _window.Show();
            _window.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        public void Dispose() => _window.Close();
    }

    private static void RunOverlay()
    {
        var overlay = new OverlayNotificationWindow();
        try
        {
            overlay.ShowNotification("!", "Display QA", "Removed monitor handle", OverlayTone.Neutral, 60_000);
            typeof(OverlayNotificationWindow).GetField("_targetMonitor", Private)!.SetValue(overlay, new nint(-123));
            typeof(OverlayNotificationWindow).GetMethod("EnsureNativeTopmost", Private)!.Invoke(overlay, [true]);
            nint target = Get<nint>(overlay, "_targetMonitor");
            Check(CaptureInterop.EnumerateMonitors().Any(item => item.Handle == target),
                "notification resolves removed target handle to live monitor");
            Check(GetWindowRect(new WindowInteropHelper(overlay).Handle, out var rect) &&
                  CaptureInterop.EnumerateMonitors().Any(m => m.Handle == target &&
                      rect.Left >= m.Left && rect.Top >= m.Top && rect.Right <= m.Left + m.Width),
                "notification is positioned within replacement monitor");
        }
        finally { overlay.ClosePermanently(); }
    }

    private static string ReadMonitorId(ObsReplayEngine engine)
    {
        nint data = obs_source_get_settings(Get<nint>(engine, "_desktopVideoSource"));
        try { return Marshal.PtrToStringUTF8(obs_data_get_string(data, "monitor_id")) ?? ""; }
        finally { ObsNative.obs_data_release(data); }
    }

    private static long ReadCaptureMethod(ObsReplayEngine engine)
    {
        nint data = obs_source_get_settings(Get<nint>(engine, "_desktopVideoSource"));
        try { return obs_data_get_int(data, "method"); }
        finally { ObsNative.obs_data_release(data); }
    }

    private static T Get<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;

    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
