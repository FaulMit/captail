using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Captail;
using Captail.Interop;
using Localization = Captail.Localization;

internal static class Program
{
    private sealed class QaApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string _output = "";
    private static int _passed;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: InstantScreenshotQa <CaptailObsBridge.dll> <output-root> [D3D11-fixture.exe]");
            return 2;
        }
        string bridge = Path.GetFullPath(args[0]);
        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, (name, _, _) =>
            name == "CaptailObsBridge.dll" ? NativeLibrary.Load(bridge) : 0);
        _output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(_output);
        var app = new QaApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Captail;component/Languages/Strings.en.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Captail;component/Themes/Theme.xaml"),
        });
        ThemeManager.ApplyAccent("mint");
        int exitCode = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(app, args.Length > 2 ? args[2] : null);
                Console.WriteLine($"INSTANT_SCREENSHOT_QA PASS ({_passed} checks)");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray())
                {
                    if (window is SettingsWindow)
                        Set(window, "_allowClose", true);
                    window.Close();
                }
                // This harness never starts App's OBS lifecycle or activation server.
                // Exit directly after closing its windows rather than invoking production shutdown.
                Environment.Exit(exitCode);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync(App app, string? fixture)
    {
        var config = new Config
        {
            ReplayEnabled = false,
            InstantScreenshotsEnabled = true,
            OutputDirectory = _output,
            Hotkey = "F20", ToggleReplayHotkey = "F21", QuickPanelHotkey = "F22",
            ScreenshotHotkey = "Ctrl+Shift+F8",
        };
        Config clone = config.Clone();
        Check(clone.InstantScreenshotsEnabled && clone.ScreenshotHotkey == config.ScreenshotHotkey &&
            config.PipelineEquals(new Config { Hotkey = "F20", ToggleReplayHotkey = "F21", QuickPanelHotkey = "F22" }),
            "screenshot settings clone without changing recording pipeline");
        Config roundTrip = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(clone))!;
        Check(roundTrip.InstantScreenshotsEnabled && roundTrip.ScreenshotHotkey == clone.ScreenshotHotkey,
            "screenshot settings JSON round trip");
        Check(!JsonSerializer.Deserialize<Config>("{}")!.InstantScreenshotsEnabled,
            "old configurations keep screenshots disabled");
        var invalid = config.Clone();
        invalid.ScreenshotHotkey = config.Hotkey;
        invalid.Normalize();
        Check(HotkeyManager.AreDistinct(invalid.ScreenshotHotkey, config.Hotkey),
            "normalize conflicting screenshot binding");
        invalid.MonitorIndex = 63;
        try { InstantScreenshot.ResolveMonitor(invalid); throw new Exception("Missing monitor silently fell back."); }
        catch (InvalidOperationException) { Pass("missing monitor fails without capturing another display"); }

        using var hotkeys = new HotkeyManager(config.Hotkey, config.ToggleReplayHotkey,
            config.QuickPanelHotkey, config.ScreenshotHotkey);
        int events = 0;
        hotkeys.ScreenshotRequested += () => events++;
        void Key(uint modifiers, bool down) => typeof(HotkeyManager)
            .GetMethod("ProcessLowLevelKey", Private)!.Invoke(hotkeys, [0x77u, modifiers, down]);
        Key(6, true); Key(6, true); Key(6, false);
        Check(events == 1, "held screenshot key fires once");
        Key(0, true); Key(6, true); Key(6, false);
        Check(events == 1, "modifier mismatch and held-key modifier changes do not trigger");
        Key(6, true); Key(6, false);
        Check(events == 2, "next press fires again");
        hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey, config.QuickPanelHotkey);
        Key(6, true); Key(6, false);
        Check(events == 2, "disabled screenshot binding releases registration and hook");
        using (var owner = new HotkeyManager("F17", "F18", "F19", "Ctrl+Shift+F7"))
        {
            hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey, config.QuickPanelHotkey, config.ScreenshotHotkey);
            try { hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey, config.QuickPanelHotkey, "Ctrl+Shift+F7");
                throw new Exception("Occupied shortcut was accepted."); }
            catch (InvalidOperationException) { Pass("occupied shortcut rejected"); }
            Key(6, true); Key(6, false);
            Check(events == 3, "failed rebind restores previous screenshot binding");
        }

        Set(app, "_config", config);
        Set(app, "_hotkeys", hotkeys);
        Call(app, "SubscribeHotkeys");
        var monitors = CaptureInterop.EnumerateMonitors();
        Check(monitors.Count > 0, "physical monitors available");
        foreach (var monitor in monitors)
        {
            config.MonitorIndex = monitor.Index;
            var timer = Stopwatch.StartNew();
            string path = await InstantScreenshot.SaveAsync(config);
            BitmapSource image = ReadPng(path);
            Check(image.PixelWidth == monitor.Width && image.PixelHeight == monitor.Height,
                $"monitor {monitor.Index + 1} native PNG dimensions {image.PixelWidth}x{image.PixelHeight} ({timer.ElapsedMilliseconds} ms)");
            Check(!File.Exists(path + ".tmp"), "atomic PNG publication removes temporary file");
            await CheckMonitorPixelsAsync(config, monitor);
        }

        config.MonitorIndex = monitors[0].Index;
        int count = CountPng();
        SendScreenshotKey();
        await WaitUntilAsync(() => CountPng() == count + 1 && (int)Get(app, "_takingScreenshot")! == 0);
        await Task.Delay(120);
        Check(CountPng() == count + 1, "real global shortcut saves exactly one PNG with replay off");
        hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey, config.QuickPanelHotkey);
        config.InstantScreenshotsEnabled = false;
        SendScreenshotKey();
        await Task.Delay(300);
        Check(CountPng() == count + 1, "disabled shortcut produces no PNG");
        config.InstantScreenshotsEnabled = true;
        hotkeys.Rebind(config.Hotkey, config.ToggleReplayHotkey, config.QuickPanelHotkey, config.ScreenshotHotkey);
        count = CountPng();
        config.ScreenshotHotkey = "Ctrl+Shift+F7";
        Call(app, "ApplyHotkeys", config);
        SendScreenshotKey();
        await Task.Delay(300);
        Check(CountPng() == count, "old shortcut stops after rebind");
        SendScreenshotKey(0x76);
        await WaitUntilAsync(() => CountPng() == count + 1 && (int)Get(app, "_takingScreenshot")! == 0);
        Pass("new global shortcut captures after rebind");
        config.ScreenshotHotkey = "Ctrl+Shift+F8";
        Call(app, "ApplyHotkeys", config);

        count = CountPng();
        config.OutputDirectory = Path.Combine(_output, "blocked-output");
        File.WriteAllText(config.OutputDirectory, "This file deliberately blocks creating a screenshot directory.");
        SendScreenshotKey();
        await WaitUntilAsync(() => ((TextBlock)((OverlayNotificationWindow)Get(app, "_overlayNotification")!)
            .FindName("TitleText")).Text == "Screenshot failed" && (int)Get(app, "_takingScreenshot")! == 0);
        Check(CountPng() == count, "save failure reports error without publishing a partial PNG");
        config.OutputDirectory = _output;
        SendScreenshotKey();
        await WaitUntilAsync(() => CountPng() == count + 1 && (int)Get(app, "_takingScreenshot")! == 0);
        Pass("next screenshot recovers after save failure");

        if (fixture is not null)
        {
            foreach (string mode in new[] { "", "--exclusive", "--hdr-buffer" })
            {
                using Process game = Process.Start(new ProcessStartInfo(Path.GetFullPath(fixture), "--screenshot-qa " + mode)
                    { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
                try
                {
                    await WaitUntilAsync(() => { game.Refresh(); return game.MainWindowHandle != 0; });
                    await Task.Delay(700);
                    Check(!game.HasExited && GetForegroundWindow() == game.MainWindowHandle,
                        "D3D11 fixture owns fullscreen foreground");
                    nint foreground = GetForegroundWindow();
                    count = CountPng();
                    SendScreenshotKey();
                    await WaitUntilAsync(() => CountPng() == count + 1 && (int)Get(app, "_takingScreenshot")! == 0);
                    Check(GetForegroundWindow() == foreground, "screenshot notification preserves fullscreen foreground");
                    BitmapSource first = ReadPng(LatestPng());
                    byte[] pixel = Pixel(first, first.PixelWidth / 2, first.PixelHeight / 2);
                    string label = mode.Length == 0 ? "borderless" : mode;
                    Check(pixel.Take(3).Max() > 20, $"D3D11 {label} screenshot contains game frame");
                    await Task.Delay(150);
                    count = CountPng();
                    SendScreenshotKey();
                    await WaitUntilAsync(() => CountPng() == count + 1 && (int)Get(app, "_takingScreenshot")! == 0);
                    byte[] next = Pixel(ReadPng(LatestPng()), first.PixelWidth / 2, first.PixelHeight / 2);
                    Check(!pixel.SequenceEqual(next), "successive game screenshots capture new frames");
                }
                finally
                {
                    if (!game.HasExited)
                    {
                        game.CloseMainWindow();
                        await WaitUntilAsync(() => game.HasExited);
                    }
                }
            }
        }
        await CheckSettingsAsync(config);
    }

    private static async Task CheckSettingsAsync(Config config)
    {
        Config? applied = null;
        var settings = new SettingsWindow(config, false, () => {}, _ => Task.FromResult(true),
            (_, _, _, _) => Task.FromResult(true), (_, _) => Task.FromResult(true),
            (candidate, _) => { applied = candidate.Clone(); return Task.FromResult(true); },
            EncoderCapabilities.Preview(), AdvancedProcessAudioAvailability.SourceUnavailable,
            (_, _) => Task.FromResult<UpdateRelease?>(null), (_, _, _) => Task.CompletedTask);
        try
        {
            await (Task)typeof(SettingsWindow).GetMethod("LoadDeviceListsAsync", Private)!.Invoke(settings, null)!;
            Call(settings, "ShowSettings");
            Call(settings, "RefreshSettingsDirtyState");
            Check(!(bool)Get(settings, "_settingsDirty")!,
                "old configuration opens screenshot settings without false unsaved changes");
            var enabled = (ToggleButton)settings.FindName("InstantScreenshotsBox");
            var monitor = (ComboBox)settings.FindName("MonitorBox");
            Check(enabled.IsChecked == true && monitor.Items.Count > 0 &&
                ((Button)settings.FindName("ScreenshotHotkeyButton")).Content.ToString() == config.ScreenshotHotkey,
                "settings load screenshot switch, shared Video monitor and binding");
            if (monitor.Items.Count > 1)
            {
                monitor.SelectedIndex = 1;
                Config pending = (Config)typeof(SettingsWindow).GetMethod("CreatePendingConfig", Private)!.Invoke(settings, null)!;
                int selected = int.Parse(((ComboBoxItem)monitor.SelectedItem).Tag.ToString()!);
                Check(pending.MonitorIndex == selected && InstantScreenshot.ResolveMonitor(pending).Index == selected &&
                    config.MonitorIndex != selected, "staged Video monitor selection controls screenshots without changing saved config");
                Call(settings, "DiscardSettings_Click", settings, new RoutedEventArgs());
                Call(settings, "ShowSettings");
            }
            enabled.IsChecked = false;
            Call(settings, "RefreshSettingsDirtyState");
            Check((bool)Get(settings, "_settingsDirty")! && config.InstantScreenshotsEnabled,
                "screenshot edits mark settings dirty without mutating saved config");
            Call(settings, "DiscardSettings_Click", settings, new RoutedEventArgs());
            Check(enabled.IsChecked == true, "discard restores screenshot switch");
            Call(settings, "ShowSettings");
            enabled.IsChecked = false;
            Call(settings, "Apply_Click", settings, new RoutedEventArgs());
            await WaitUntilAsync(() => applied is not null);
            Check(applied!.InstantScreenshotsEnabled == false && applied.ScreenshotHotkey == config.ScreenshotHotkey,
                "Done applies screenshot settings");
            Call(settings, "ShowSettings");
            var row = (FrameworkElement)settings.FindName("ScreenshotHotkeyButton");
            Localization.SetLanguage("ru");
            settings.Show();
            await Task.Delay(400);
            Call(settings, "OpenSettingsAt", row, (Control)row);
            await Task.Delay(100);
            settings.UpdateLayout();
            var render = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(settings);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
            using var stream = File.Create(Path.Combine(_output, "settings-screenshots.png")); encoder.Save(stream);
            Pass("settings rendered for visual inspection");
        }
        finally { Set(settings, "_allowClose", true); settings.Close(); }
    }

    private static async Task CheckMonitorPixelsAsync(Config config, CaptureInterop.MonitorInfo monitor)
    {
        var pattern = new Grid();
        pattern.RowDefinitions.Add(new RowDefinition()); pattern.RowDefinitions.Add(new RowDefinition());
        pattern.ColumnDefinitions.Add(new ColumnDefinition()); pattern.ColumnDefinitions.Add(new ColumnDefinition());
        Brush[] colors = [Brushes.Red, Brushes.Lime, Brushes.Blue, Brushes.White];
        for (int index = 0; index < colors.Length; index++)
        {
            var cell = new Border { Background = colors[index] };
            Grid.SetRow(cell, index / 2); Grid.SetColumn(cell, index % 2); pattern.Children.Add(cell);
        }
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Width = 256, Height = 256, Content = pattern,
        };
        try
        {
            window.Show();
            Check(SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1),
                monitor.Left + 64, monitor.Top + 64, 256, 256, 0x0010), "place pixel reference on selected physical monitor");
            await Task.Delay(200);
            var snapshot = config.Clone();
            BitmapSource image = ReadPng(await InstantScreenshot.SaveAsync(snapshot));
            byte[] red = Pixel(image, 128, 128), green = Pixel(image, 256, 128);
            byte[] blue = Pixel(image, 128, 256), white = Pixel(image, 256, 256);
            Check(red[2] > red[0] + 50 && red[2] > red[1] + 50 &&
                green[1] > green[0] + 50 && green[1] > green[2] + 50 &&
                blue[0] > blue[1] + 50 && blue[0] > blue[2] + 50 && white.Take(3).Min() > 150,
                $"monitor {monitor.Index + 1} correct color quadrants and orientation through shared Video selection");
        }
        finally { window.Close(); }
    }

    private static void SendScreenshotKey(ushort virtualKey = 0x77)
    {
        Input Key(ushort vk, bool up = false) => new() { Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = up ? 2u : 0u } } };
        Input[] inputs = [Key(0x11), Key(0x10), Key(virtualKey), Key(virtualKey, true), Key(0x10, true), Key(0x11, true)];
        Check(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "Windows key injection accepted");
    }
    private static int CountPng() => Directory.Exists(Path.Combine(_output, "Screenshots"))
        ? Directory.GetFiles(Path.Combine(_output, "Screenshots"), "*.png").Length : 0;
    private static string LatestPng() => Directory.GetFiles(Path.Combine(_output, "Screenshots"), "*.png")
        .OrderBy(File.GetLastWriteTimeUtc).Last();
    private static BitmapSource ReadPng(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("QA condition timed out.");
            await Task.Delay(30);
        }
    }
    private static Type Owner(object instance) => instance is App ? typeof(App) : instance.GetType();
    private static object? Get(object instance, string name) => Owner(instance).GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => Owner(instance).GetField(name, Private)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] args) => Owner(instance).GetMethod(name, Private)!.Invoke(instance, args);
    private static void Check(bool passed, string name) { if (!passed) throw new Exception(name); Pass(name); }
    private static void Pass(string name) { _passed++; Console.WriteLine("PASS " + name); }
}
