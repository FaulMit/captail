using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Captail;
using Localization = Captail.Localization;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObsTask(nint parameter);
    [DllImport("obs.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void obs_queue_task(int type, ObsTask task, nint parameter,
        [MarshalAs(UnmanagedType.I1)] bool wait);
    [DllImport("libobs-winrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void winrt_capture_thread_stop();
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindow callback, nint parameter);
    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int Run(string[] args)
    {
        if (args.Contains("--hotkeys"))
            return HotkeyQa.Run();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Languages/Strings.en.xaml", "Themes/Theme.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Captail;component/{resource}"),
            });
        if (args.Contains("--ru"))
            app.Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Captail;component/Languages/Strings.ru.xaml"),
            };
        if (args.Contains("--preview"))
        {
            foreach (var (key, color) in new[]
            {
                ("AccentBrush", "#B69AF7"), ("AccentHoverBrush", "#C8B2FA"),
                ("AccentDimBrush", "#785AC6"), ("OnAccentBrush", "#160F2B"),
                ("AccentSubtleBrush", "#24B69AF7"),
            })
                app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        if (args.Contains("--capture"))
            return Task.Factory.StartNew(() => RunCapture(args.Contains("--user-config"),
                    args.Contains("--toggle-only"), args.Contains("--wgc-release-twice"),
                    args.Contains("--source-cycle")), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        if (args.Contains("--focus"))
            return FocusQa.Run();
        int dialogs = 0;
        var seenDialogs = new ConcurrentDictionary<nint, byte>();
        uint uiThread = GetCurrentThreadId();
        using var dismiss = new System.Threading.Timer(_ => EnumThreadWindows(uiThread, (window, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32770")
            {
                if (seenDialogs.TryAdd(window, 0))
                    Interlocked.Increment(ref dialogs);
                PostMessage(window, 0x10, 0, 0); // WM_CLOSE
            }
            return true;
        }, 0), null, 20, 20);
        bool passed = true;
        foreach (string mode in new[] { "replay", "recording" })
        {
            bool running = true;
            bool allowStart = true;
            bool allowStop = true;
            int saves = 0;
            var config = new Config { CaptureMode = mode, ReplayEnabled = true };
            var panel = new QuickPanelWindow(config, () => running, _ => true,
                _ => Task.FromResult(true), _ => Task.FromResult(true),
                enabled =>
                {
                    if (enabled ? allowStart : allowStop)
                        running = enabled;
                    config.ReplayEnabled = running;
                    return Task.FromResult(running);
                }, (_, _, _, _) => Task.FromResult(true), () => saves++, () => { },
                () => Task.CompletedTask);
            _ = new WindowInteropHelper(panel).EnsureHandle();
            panel.Refresh();
            var toggle = (ToggleButton)panel.FindName("CaptureToggle");
            var recordingAction = (Button)panel.FindName("RecordingActionButton");
            ButtonBase captureControl = mode == "recording" ? recordingAction : toggle;
            Check(recordingAction.Visibility == (mode == "recording" ? Visibility.Visible : Visibility.Collapsed) &&
                  ((Grid)panel.FindName("ReplayCaptureRow")).Visibility ==
                      (mode == "recording" ? Visibility.Collapsed : Visibility.Visible),
                $"{mode}: correct capture control is visible");
            int before = dialogs;
            ClickCapture(false);
            Check(!running && toggle.IsChecked == false && captureControl.IsEnabled,
                $"{mode}: stop leaves capture off and controls usable");
            Check(dialogs == before, $"{mode}: successful stop shows no settings error");
            if (mode == "recording")
            {
                Check(((TextBlock)panel.FindName("RecordingActionText")).Text == Localization.Text("L.Capture.StartRecording") &&
                      ((TextBlock)panel.FindName("RecordingActionHotkey")).Text == config.Hotkey,
                    "recording: stopped button shows Start recording and configured hotkey");
                if (args.Contains("--preview")) SaveCaptureCard(panel, "start", args.Contains("--ru"));
            }
            ClickCapture(true);
            Check(running && dialogs == before, $"{mode}: successful start shows no settings error");
            if (mode == "recording")
            {
                Check(((TextBlock)panel.FindName("RecordingActionText")).Text == Localization.Text("L.Capture.StopRecording"),
                    "recording: active button shows Stop recording");
                if (args.Contains("--preview")) SaveCaptureCard(panel, "stop", args.Contains("--ru"));
            }
            if (mode == "replay")
            {
                ((Button)panel.FindName("SaveReplayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(saves == 1 && running && dialogs == before, "replay: save uses save delegate without toggling capture");
            }
            allowStop = false;
            before = dialogs;
            ClickCapture(false);
            Check(running && toggle.IsChecked == true && dialogs == before + 1,
                $"{mode}: rejected stop still shows error and restores state");
            allowStop = true;
            running = false;
            config.ReplayEnabled = false;
            allowStart = false;
            panel.Refresh();
            before = dialogs;
            ClickCapture(true);
            Check(!running && toggle.IsChecked == false && dialogs == before + 1,
                $"{mode}: rejected start still shows error and restores state");
            panel.ClosePermanently();

            void ClickCapture(bool enabled)
            {
                if (mode == "replay") toggle.IsChecked = enabled;
                captureControl.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
        }
        Console.WriteLine($"QUICK_PANEL_QA {(passed ? "PASS" : "FAIL")}");
        return passed ? 0 : 1;

        void Check(bool condition, string message)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {message}");
            passed &= condition;
        }
    }

    private static void SaveCaptureCard(QuickPanelWindow panel, string state, bool russian)
    {
        panel.ShowActivated = false;
        panel.Left = -32000;
        panel.Top = -32000;
        if (!panel.IsVisible) panel.Show();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        var root = (FrameworkElement)panel.Content;
        root.Measure(new Size(panel.Width, panel.Height));
        root.Arrange(new Rect(0, 0, panel.Width, panel.Height));
        root.UpdateLayout();
        var card = (FrameworkElement)panel.FindName("CaptureCard");
        int width = (int)Math.Ceiling(card.ActualWidth);
        int height = (int)Math.Ceiling(card.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        string output = Path.GetFullPath(Path.Combine(".qa", "recording-action", $"{(russian ? "ru" : "en")}-{state}.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
        Console.WriteLine($"PREVIEW {output}");
    }

    private static int RunCapture(bool userConfig, bool toggleOnly, bool releaseTwice, bool sourceCycle)
    {
        string output = Path.GetFullPath(Path.Combine(".qa", "overlay-save", "capture"));
        Directory.CreateDirectory(output);
        foreach (string mode in new[] { "replay", "recording" })
        {
            for (int iteration = 0; iteration < (toggleOnly ? 12 : 5); iteration++)
            {
                var config = userConfig ? Config.Load().Clone() : new Config
                {
                    CaptureMode = mode, CaptureSource = "desktop", FrameRate = 30,
                    RecordingResolution = "720p", CaptureSystemAudio = false,
                    CaptureMicrophone = false, BufferSeconds = 10,
                    OutputDirectory = output, DiskReplayBufferEnabled = true,
                };
                config.CaptureMode = mode;
                config.OutputDirectory = output;
                Console.WriteLine($"CAPTURE starting {mode} iteration={iteration}");
                long logOffset = File.Exists(Log.Path) ? new FileInfo(Log.Path).Length : 0;
                using var engine = new ObsReplayEngine(config);
                engine.Start();
                if (sourceCycle)
                {
                    var switchSource = typeof(ObsReplayEngine).GetMethod("SwitchAutomaticCapture",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        switchSource.Invoke(engine, [true, "qa-game.exe"]);
                        Thread.Sleep(50);
                        switchSource.Invoke(engine, [false, ""]);
                        Thread.Sleep(50);
                    }
                }
                if (releaseTwice)
                {
                    Thread.Sleep(1000);
                    FlushLog();
                    if (!ReadLogSince(logOffset).Contains("method: WGC", StringComparison.Ordinal))
                        throw new InvalidOperationException("Repeated-release QA requires an active WGC capture.");
                    ObsTask stop = _ =>
                    {
                        Console.WriteLine("WGC release first");
                        winrt_capture_thread_stop();
                        Console.WriteLine("WGC release second");
                        winrt_capture_thread_stop();
                        Console.WriteLine("WGC repeated release PASS");
                    };
                    obs_queue_task(1, stop, 0, true);
                    GC.KeepAlive(stop);
                    engine.Dispose();
                    CheckShutdown(logOffset);
                    return 0;
                }
                Thread.Sleep(toggleOnly ? new[] { 0, 10, 50, 100, 250, 500 }[iteration % 6] : 1500);
                string path = mode == "replay" && !toggleOnly
                    ? engine.SaveReplayAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()
                    : engine.RecordingPath;
                Console.WriteLine($"CAPTURE stopping {mode} iteration={iteration}");
                engine.Dispose();
                CheckShutdown(logOffset);
                if (!toggleOnly && (!File.Exists(path) || new FileInfo(path).Length == 0))
                    throw new InvalidOperationException($"Missing saved {mode}: {path}");
                if (!toggleOnly)
                    CheckDecodedFrame(path);
                Console.WriteLine($"CAPTURE PASS {mode} iteration={iteration} path={path}");
            }
        }
        return 0;
    }

    private static void FlushLog() => typeof(Log).GetMethod("Flush",
        BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);

    private static void CheckDecodedFrame(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-nostdin", "-v", "error", "-sseof", "-0.5", "-i", path,
                     "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var pixels = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pixels);
        process.WaitForExit();
        if (process.ExitCode != 0 || pixels.Length != 160 * 90 * 3 ||
            pixels.ToArray().Average(value => (double)value) <= 2)
            throw new InvalidOperationException($"Saved file has no decodable desktop image: {path}; {errors.GetAwaiter().GetResult()}");
    }

    private static void CheckShutdown(long logOffset)
    {
        FlushLog();
        string shutdownLog = ReadLogSince(logOffset);
        if (shutdownLog.Contains("Double destroy", StringComparison.Ordinal) ||
            shutdownLog.Contains("source(s) were remaining", StringComparison.Ordinal))
            throw new InvalidOperationException("Capture sources survived explicit disposal or were destroyed twice.");
        if (GetModuleHandle("win-capture.dll") != 0)
            throw new InvalidOperationException("Windows capture module retained stale initialization state after shutdown.");
    }

    private static string ReadLogSince(long offset)
    {
        using var stream = new FileStream(Log.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = Math.Min(offset, stream.Length);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
