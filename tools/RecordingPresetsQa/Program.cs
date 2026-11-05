using Captail;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private sealed class QaApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    private static int _passed;
    private static readonly List<SettingsWindow> Windows = [];

    [DllImport("obs.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint obs_data_get_string(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long obs_data_get_int(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool obs_data_get_bool(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var app = new QaApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
            foreach (string resource in new[] { "Languages/Strings.en.xaml", "Themes/Theme.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Captail;component/{resource}"),
                });
            ThemeManager.ApplyAccent("mint");
            Run("old configuration stays standard", () =>
            {
                Config old = JsonSerializer.Deserialize<Config>("{\"Codec\":\"h264\",\"FrameRate\":60}")!;
                old.Normalize();
                Check(!old.AdvancedSettingsEnabled && old.EncoderTuning == new EncoderTuningOptions());
                Check(!old.DiskReplayBufferEnabled);
            });
            Run("profiles round-trip advanced mode and isolate mutable values", () =>
            {
                var config = new Config { AdvancedSettingsEnabled = true };
                config.EncoderTuning.NvencPreset = "p5";
                config.ProcessAudioRoutes.Add(new() { Executable = "game.exe", Track = 2 });
                var preset = new RecordingPreset { Name = "Gameplay", Settings = RecordingSettingsSnapshot.Capture(config) };
                config.RecordingPresets.Add(preset);
                config.SelectedRecordingPresetId = preset.Id;
                Config copy = config.Clone();
                copy.RecordingPresets[0].Settings.EncoderTuning.NvencPreset = "p7";
                copy.RecordingPresets[0].Settings.ProcessAudioRoutes[0].Track = 6;
                Check(preset.Settings.EncoderTuning.NvencPreset == "p5" && preset.Settings.ProcessAudioRoutes[0].Track == 2);
                Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
                loaded.Normalize();
                Check(loaded.SelectedRecordingPresetId == preset.Id && loaded.RecordingPresets.Count == 1);
                loaded.RecordingPresets[0].Settings.ApplyTo(loaded);
                Check(loaded.AdvancedSettingsEnabled && loaded.EncoderTuning.NvencPreset == "p5");
            });
            Run("profile scope preserves lifecycle and personal preferences", () =>
            {
                var target = new Config { CaptureMode = "recording", ReplayEnabled = false, OutputDirectory = @"C:\CaptailQa", AccentColor = "rose", Hotkey = "Ctrl+Alt+F8" };
                RecordingSettingsSnapshot.Capture(new Config { Codec = "hevc", FrameRate = 30 }).ApplyTo(target);
                Check(target.CaptureMode == "recording" && !target.ReplayEnabled && target.OutputDirectory == @"C:\CaptailQa" && target.AccentColor == "rose" && target.Hotkey == "Ctrl+Alt+F8");
                Check(target.Codec == "hevc" && target.FrameRate == 30);
            });
            Run("profile metadata and disclosure do not restart capture", () =>
            {
                var baseline = new Config();
                Config modified = baseline.Clone();
                modified.AdvancedSettingsEnabled = true;
                modified.RecordingPresets.Add(new() { Name = "Saved", Settings = RecordingSettingsSnapshot.Capture(modified) });
                modified.Normalize();
                Check(baseline.PipelineEquals(modified));
                modified.EncoderTuning.ConstantQuality = 18;
                Check(!baseline.PipelineEquals(modified));
            });
            Run("malformed profile data normalizes safely", () =>
            {
                var config = new Config
                {
                    EncoderTuning = new() { NvencPreset = "invalid", ConstantQuality = 999, BFrames = 99 },
                    RecordingPresets = [new() { Name = " valid ", Settings = null! }, new() { Name = "valid" }, new() { Id = "bad", Name = "Bad ID" }],
                    SelectedRecordingPresetId = "missing",
                };
                config.Normalize();
                Check(config.RecordingPresets.Count == 1 && config.RecordingPresets[0].Name == "valid" && config.SelectedRecordingPresetId == "");
                Check(config.EncoderTuning.NvencPreset == "auto" && config.EncoderTuning.ConstantQuality == 51 && config.EncoderTuning.BFrames == 4);
            });
            Run("encoder safety limits survive manual overrides", () =>
            {
                var options = new EncoderTuningOptions { KeyframeIntervalSeconds = 8, BFrames = 4 };
                Check(options.EffectiveKeyframeInterval(500) == 1 && options.EffectiveKeyframeInterval(0) == 8);
                Check(options.EffectiveBFrames("nvenc", "hevc", 2) == 0 && options.EffectiveBFrames("qsv", "h264", 2) == 4);
                Check(options.EffectiveKeyframeInterval(0, true) == 2);
            });
            Run("disk replay state survives presets and respects pipeline scope", () =>
            {
                var baseline = new Config();
                Config disk = baseline.Clone();
                disk.DiskReplayBufferEnabled = true;
                Check(!baseline.PipelineEquals(disk));
                var preset = RecordingSettingsSnapshot.Capture(disk);
                preset = JsonSerializer.Deserialize<RecordingSettingsSnapshot>(JsonSerializer.Serialize(preset))!;
                preset.ApplyTo(baseline);
                Check(baseline.DiskReplayBufferEnabled && baseline.PipelineEquals(disk));
                baseline.CaptureMode = disk.CaptureMode = "recording";
                baseline.DiskReplayBufferEnabled = false;
                Check(baseline.PipelineEquals(disk));
                Check(ReplayLibrary.IsInternalWorkingFile(@"C:\clips\.captail-buffer\session\segment_1.tmp.mp4"));
                var window = MakeWindow(new Config { AdvancedSettingsEnabled = true, DiskReplayBufferEnabled = true });
                Check(Control<StackPanel>(window, "DiskReplayBufferRow").Visibility == Visibility.Visible);
                Check(Pending(window).DiskReplayBufferEnabled);
                Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked = false;
                Call(window, "AdvancedSettings_Click", window, new RoutedEventArgs());
                Check(Control<StackPanel>(window, "DiskReplayBufferRow").Visibility == Visibility.Collapsed && Pending(window).DiskReplayBufferEnabled);
            });
            Run("disk sessions isolate live owners, prune size and recover abandoned files", DiskSessionContracts);
            Run("real settings controls support CRUD, disclosure and discard", SettingsControls);
            Run("empty preset library and custom buffer minutes survive validation and disclosure", CustomBufferMinutes);
            Run("encoder-family controls preserve independent presets", EncoderFamilyControls);
            Run("production encoder writes native OBS settings for all families", NativeEncoderSettings);
            Run("all localized settings windows render without missing resources", () => RenderLayouts(args));
            string? captureRoot = args.FirstOrDefault(arg => arg.StartsWith("--capture="))?["--capture=".Length..];
            if (captureRoot is not null)
                Run("production encoders record playable CBR and CQP files", () => CaptureSmoke(captureRoot));
            string? diskRoot = args.FirstOrDefault(arg => arg.StartsWith("--disk="))?["--disk=".Length..];
            if (diskRoot is not null)
                Run("production disk replay rotates, saves all tracks, resets and cleans up", () => DiskReplaySmoke(diskRoot, args.Contains("--disk-fast")));
            Console.WriteLine($"RECORDING_PRESETS_QA PASS: {_passed} checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            foreach (SettingsWindow window in Windows)
            {
                Call(window, "SetSettingsDirty", false, false);
                window.Close();
            }
        }
    }

    private static SettingsWindow MakeWindow(Config? config = null, string family = "nvenc")
    {
        var capabilities = new EncoderCapabilities("QA adapter",
            new[] { "h264", "hevc", "av1" }.Select(codec => new CodecCapability(codec, "qa_encoder", family, family)));
        Config actual = config ?? new Config { ReplayEnabled = false };
        var window = new SettingsWindow(actual, false, () => { },
            _ => Task.FromResult(true), (_, _, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true), (candidate, _) => { actual.CopyFrom(candidate); return Task.FromResult(true); }, capabilities,
            AdvancedProcessAudioAvailability.Available, (_, _) => Task.FromResult<UpdateRelease?>(null),
            (_, _, _) => Task.CompletedTask);
        Windows.Add(window);
        Call(window, "ShowSettings");
        return window;
    }

    private static void SettingsControls()
    {
        var saved = new Config { ReplayEnabled = false };
        SettingsWindow window = MakeWindow(saved);
        Check(Control<StackPanel>(window, "AdvancedVideoSettings").Visibility == Visibility.Collapsed);
        Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked = true;
        Call(window, "AdvancedSettings_Click", window, new RoutedEventArgs());
        Select(window, "RateControlBox", "cqp");
        Select(window, "NvencPresetBox", "p5");
        Control<Slider>(window, "ConstantQualitySlider").Value = 19;
        Select(window, "AudioBitrateBox", "320");
        Control<TextBox>(window, "RecordingPresetNameBox").Text = "Gameplay";
        Call(window, "SaveRecordingPreset_Click", window, new RoutedEventArgs());
        Config pending = Pending(window);
        Check(saved.RecordingPresets.Count == 0 && pending.RecordingPresets.Count == 1);
        string id = pending.SelectedRecordingPresetId;
        Select(window, "RecordingPresetBox", "");
        Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked = false;
        Check(!Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked!.Value);
        Select(window, "RecordingPresetBox", id);
        pending = Pending(window);
        Check(pending.AdvancedSettingsEnabled && pending.EncoderTuning.ConstantQuality == 19 && pending.AudioBitrateKbps == 320);
        Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked = false;
        Call(window, "AdvancedSettings_Click", window, new RoutedEventArgs());
        Check(Control<StackPanel>(window, "AdvancedVideoSettings").Visibility == Visibility.Collapsed && Pending(window).EncoderTuning.NvencPreset == "p5");
        Control<TextBox>(window, "RecordingPresetNameBox").Text = "Renamed";
        Call(window, "UpdateRecordingPreset_Click", window, new RoutedEventArgs());
        Check(Pending(window).RecordingPresets[0].Name == "Renamed" && !Pending(window).RecordingPresets[0].Settings.AdvancedSettingsEnabled);
        Select(window, "RecordingPresetBox", "");
        Select(window, "RecordingPresetBox", id);
        Check(!Pending(window).AdvancedSettingsEnabled && Pending(window).EncoderTuning.ConstantQuality == 19);
        Call(window, "Apply_Click", window, new RoutedEventArgs());
        Check(saved.RecordingPresets.Count == 1 && saved.RecordingPresets[0].Name == "Renamed");
        Config persisted = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(saved))!;
        persisted.Normalize();
        Check(persisted.RecordingPresets[0].Settings.EncoderTuning.ConstantQuality == 19 && !persisted.RecordingPresets[0].Settings.AdvancedSettingsEnabled);
        Call(window, "ShowSettings");
        Call(window, "DeleteRecordingPreset_Click", window, new RoutedEventArgs());
        Check(Pending(window).RecordingPresets.Count == 0 && Pending(window).SelectedRecordingPresetId == "");
        Call(window, "DiscardSettings_Click", window, new RoutedEventArgs());
        Check(Pending(window).RecordingPresets.Count == 1 && !Pending(window).AdvancedSettingsEnabled);
        Call(window, "ShowSettings");
        Call(window, "DeleteRecordingPreset_Click", window, new RoutedEventArgs());
        Call(window, "Apply_Click", window, new RoutedEventArgs());
        Check(saved.RecordingPresets.Count == 0 && JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(saved))!.RecordingPresets.Count == 0);
    }

    private static void CustomBufferMinutes()
    {
        var config = new Config { ReplayEnabled = false, AdvancedSettingsEnabled = true,
            BufferSeconds = 75, SelectedRecordingPresetId = "balanced" };
        config.Normalize();
        Check(config.RecordingPresets.Count == 0 && config.SelectedRecordingPresetId == "" && config.BufferSeconds == 75);
        var window = MakeWindow(config);
        var picker = Control<ComboBox>(window, "RecordingPresetBox");
        Check(picker.Items.Count == 1 && !picker.IsEnabled);
        Check(Control<Border>(window, "StandardBufferLengthControl").Visibility == Visibility.Collapsed);
        Check(Control<StackPanel>(window, "CustomBufferLengthControl").Visibility == Visibility.Visible);
        TextBox minutes = Control<TextBox>(window, "CustomBufferMinutesBox");
        Check(decimal.Parse(minutes.Text, System.Globalization.CultureInfo.CurrentCulture) == 1.25m);
        foreach (string text in new[] { "2.5", "2,5" })
        {
            minutes.Text = text;
            Check(Pending(window).BufferSeconds == 150 && Control<TextBlock>(window, "CustomBufferErrorText").Visibility == Visibility.Collapsed);
        }
        Control<TextBox>(window, "RecordingPresetNameBox").Text = "Balanced";
        Call(window, "SaveRecordingPreset_Click", window, new RoutedEventArgs());
        Check(Pending(window).RecordingPresets.Count == 1 && picker.IsEnabled);
        Check(Pending(window).RecordingPresets[0].Settings.BufferSeconds == 150);
        foreach (string invalid in new[] { "", "0", "-1", "1441", "NaN", "0.0001", "1,2.3", "999999999" })
        {
            minutes.Text = invalid;
            Check(Control<TextBlock>(window, "CustomBufferErrorText").Visibility == Visibility.Visible);
            Call(window, "Apply_Click", window, new RoutedEventArgs());
            Call(window, "SaveRecordingPreset_Click", window, new RoutedEventArgs());
            Check(config.BufferSeconds == 75 && config.RecordingPresets.Count == 0 && Pending(window).RecordingPresets.Count == 1);
        }
        minutes.Text = "1440";
        Check(Pending(window).BufferSeconds == 86400);
        minutes.Text = "0.25";
        Check(Pending(window).BufferSeconds == 15);
        minutes.Text = "2.5";
        Control<ToggleButton>(window, "AdvancedSettingsBox").IsChecked = false;
        Call(window, "AdvancedSettings_Click", window, new RoutedEventArgs());
        Check(Pending(window).BufferSeconds == 150 && Control<TextBlock>(window, "CustomBufferSummaryText").Visibility == Visibility.Visible);
        Call(window, "UpdateRecordingPreset_Click", window, new RoutedEventArgs());
        Call(window, "Apply_Click", window, new RoutedEventArgs());
        Check(config.BufferSeconds == 150 && config.RecordingPresets[0].Settings.BufferSeconds == 150);
        Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
        loaded.Normalize();
        Check(loaded.BufferSeconds == 150 && !loaded.AdvancedSettingsEnabled);
        Call(window, "ShowSettings");
        Control<UniformGrid>(window, "BufferOptions").Children.OfType<RadioButton>().Single(button => (string)button.Tag == "60").IsChecked = true;
        Check(Pending(window).BufferSeconds == 60 && Control<TextBlock>(window, "CustomBufferSummaryText").Visibility == Visibility.Collapsed);
        Call(window, "DiscardSettings_Click", window, new RoutedEventArgs());
        Check(Pending(window).BufferSeconds == 150);
    }

    private static void EncoderFamilyControls()
    {
        foreach (string family in new[] { "nvenc", "amf", "qsv" })
        {
            SettingsWindow window = MakeWindow(new Config { AdvancedSettingsEnabled = true, ReplayEnabled = false }, family);
            string expected = family switch { "amf" => "AmfPresetBox", "qsv" => "QsvPresetBox", _ => "NvencPresetBox" };
            Check(Control<ComboBox>(window, expected).Visibility == Visibility.Visible);
            Check((Control<StackPanel>(window, "NvencAdvancedSettings").Visibility == Visibility.Visible) == (family == "nvenc"));
        }
    }

    private static void RenderLayouts(string[] args)
    {
        string? output = args.FirstOrDefault(arg => arg.StartsWith("--screenshots="))?["--screenshots=".Length..];
        foreach (string language in Captail.Localization.SupportedLanguages.Select(item => item.Code))
        {
            Captail.Localization.SetLanguage(language);
            SettingsWindow window = MakeWindow(new Config { AdvancedSettingsEnabled = true, ReplayEnabled = false, DiskReplayBufferEnabled = true,
                EncoderTuning = new() { RateControl = "cqp" } });
            window.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            window.Measure(new Size(404, 720));
            window.Arrange(new Rect(0, 0, 404, 720));
            window.UpdateLayout();
            Check(Control<Border>(window, "RecordingPresetsGroup").ActualWidth > 300);
            Border advancedGroup = Control<Border>(window, "AdvancedSettingsGroup");
            var settingsList = (Panel)advancedGroup.Parent;
            Check(settingsList.Children[^1] == advancedGroup);
            Check(Control<ToggleButton>(window, "AdvancedSettingsBox").ActualWidth > 0);
            foreach (string helpName in new[] { "RecordingPresets", "AdvancedSettings", "RateControl", "ConstantQuality",
                "EncoderPreset", "Keyframes", "BFrames", "Multipass", "LookAhead", "AdaptiveQuantization", "AudioBitrate", "DiskReplayBuffer" })
            {
                var tip = (ToolTip)Control<Button>(window, $"{helpName}HelpButton").ToolTip;
                var contents = (StackPanel)tip.Content;
                Check(contents.Children.OfType<TextBlock>().All(text => !string.IsNullOrWhiteSpace(text.Text)));
            }
            if (output is not null && language is "en" or "ru" or "de")
            {
                Directory.CreateDirectory(output);
                SaveImage("presets");
                ScrollViewer scroll = Control<ScrollViewer>(window, "SettingsScrollViewer");
                foreach (string section in new[] { "DiskReplayBufferRow", "AdvancedVideoSettings", "AdvancedAudioSettings", "AdvancedSettingsGroup" })
                {
                    Point position = Control<FrameworkElement>(window, section).TranslatePoint(new Point(0, 0), scroll);
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + position.Y - 12);
                    window.UpdateLayout();
                    SaveImage(section);
                    if (section == "AdvancedVideoSettings")
                        SaveHelp("EncoderPresetHelpButton", "encoder-help");
                    if (section == "AdvancedSettingsGroup")
                        SaveHelp("AdvancedSettingsHelpButton", "advanced-help");
                    if (section == "DiskReplayBufferRow")
                        SaveHelp("DiskReplayBufferHelpButton", "disk-help");
                }
                void SaveImage(string section)
                {
                    var bitmap = new RenderTargetBitmap(404, 720, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(Control<Border>(window, "WindowFrame"));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using FileStream stream = File.Create(Path.Combine(output, $"{section}-{language}.png"));
                    encoder.Save(stream);
                }
                void SaveHelp(string name, string file)
                {
                    Button button = Control<Button>(window, name);
                    var tip = (ToolTip)button.ToolTip;
                    tip.PlacementTarget = button;
                    tip.IsOpen = true;
                    var waitFrame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(200),
                    };
                    timer.Tick += (_, _) => { timer.Stop(); waitFrame.Continue = false; };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(waitFrame);
                    tip.UpdateLayout();
                    Check(tip.ActualWidth > 200 && tip.ActualHeight > 30);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth),
                        (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(tip);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using FileStream stream = File.Create(Path.Combine(output, $"{file}-{language}.png"));
                    encoder.Save(stream);
                    tip.IsOpen = false;
                }
            }
            window.Hide();
        }
    }

    private static void NativeEncoderSettings()
    {
        foreach (string family in new[] { "nvenc", "amf", "qsv" })
        foreach (string rate in new[] { "cbr", "cqp" })
        {
            var config = new Config
            {
                Codec = "hevc", BitrateMbps = 100, MaxReplaySizeMb = 500,
                EncoderTuning = new()
                {
                    RateControl = rate, ConstantQuality = 19, KeyframeIntervalSeconds = 8,
                    BFrames = 3, NvencPreset = "p5", AmfPreset = "balanced", QsvTargetUsage = "tu2",
                    NvencMultipass = "qres", NvencLookAhead = true, NvencAdaptiveQuantization = "off",
                },
            };
            var engine = new ObsReplayEngine(config, false, () => []);
            nint settings = ObsNative.obs_data_create();
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                object profile = typeof(ObsReplayEngine).GetMethod("SelectLoadProfile", flags)!.Invoke(engine, null)!;
                typeof(ObsReplayEngine).GetMethod("ConfigureEncoderSettings", flags)!.Invoke(engine,
                    [settings, new CodecCapability("hevc", "qa", family, family), profile]);
                string Read(string key) => Marshal.PtrToStringUTF8(obs_data_get_string(settings, key)) ?? "";
                Check(Read("rate_control") == rate.ToUpperInvariant());
                Check(obs_data_get_int(settings, "keyint_sec") == 1);
                Check(obs_data_get_int(settings, "bitrate") == (family == "qsv" ? 65_000 : 100_000));
                Check(obs_data_get_int(settings, family == "qsv" ? "bframes" : "bf") == (family == "nvenc" ? 0 : 3));
                if (rate == "cqp")
                {
                    Check(obs_data_get_int(settings, "cqp") == 19);
                    if (family == "qsv")
                        Check(new[] { "qpi", "qpp", "qpb" }.All(key => obs_data_get_int(settings, key) == 19));
                }
                if (family == "nvenc")
                    Check(Read("preset") == "p5" && Read("multipass") == "qres" && obs_data_get_bool(settings, "lookahead") && !obs_data_get_bool(settings, "adaptive_quantization"));
                else
                    Check(family == "amf" ? Read("preset") == "balanced" : Read("target_usage") == "TU2");
            }
            finally { ObsNative.obs_data_release(settings); }
        }
    }

    private static void CaptureSmoke(string root)
    {
        Directory.CreateDirectory(root);
        string fixturePath = Path.GetFullPath(Path.Combine(root, "encoder-fixture.mp4"));
        var fixtureStart = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30",
            "-t", "5", "-c:v", "mpeg4", "-q:v", "4", "-y", fixturePath })
            fixtureStart.ArgumentList.Add(argument);
        using (var fixture = System.Diagnostics.Process.Start(fixtureStart)!)
        {
            string error = fixture.StandardError.ReadToEnd();
            fixture.WaitForExit();
            if (fixture.ExitCode != 0) throw new InvalidOperationException(error);
        }
        foreach ((string codec, string rate) in new[] { ("h264", "cbr"), ("h264", "cqp"), ("hevc", "cqp"), ("av1", "cqp") })
        {
            var config = new Config
            {
                CaptureMode = "recording", CaptureSource = "desktop", RecordingResolution = "720p",
                FrameRate = 30, BitrateMbps = 10, Codec = codec, CaptureSystemAudio = false,
                CaptureMicrophone = false, AudioBitrateKbps = 320,
                OutputDirectory = Path.GetFullPath(Path.Combine(root, $"{codec}-{rate}")),
                AdvancedSettingsEnabled = true,
                EncoderTuning = new()
                {
                    RateControl = rate, ConstantQuality = 22, NvencPreset = "p5", KeyframeIntervalSeconds = 1,
                    NvencMultipass = "qres", NvencLookAhead = true, NvencAdaptiveQuantization = "off",
                    BFrames = 3,
                },
            };
            config.Normalize();
            string path;
            using (var engine = new ObsReplayEngine(config, windowsGraphicsCaptureReady: false))
            {
                engine.Start();
                path = engine.RecordingPath;
                Console.WriteLine($"CAPTURE {codec}/{rate}: {engine.ActiveEncoder}");
                // A deterministic OBS source isolates encoder/output QA from fullscreen,
                // DRM, game-hook ownership, and Desktop Duplication availability.
                nint settings = ObsNative.obs_data_create();
                nint source;
                try
                {
                    ObsNative.obs_data_set_bool(settings, "is_local_file", true);
                    ObsNative.obs_data_set_string(settings, "local_file", fixturePath);
                    ObsNative.obs_data_set_bool(settings, "looping", true);
                    source = ObsNative.obs_source_create("ffmpeg_source", "Encoder QA fixture", settings, 0);
                }
                finally { ObsNative.obs_data_release(settings); }
                Check(source != 0);
                ObsNative.obs_set_output_source(0, source);
                ObsNative.obs_source_release(source);
                Thread.Sleep(2500);
            }
            Check(File.Exists(path) && new FileInfo(path).Length > 1000);
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-v", "error", "-xerror", "-i", path, "-f", "null", "-" })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Decode failed for {path}: {errors}");
            var pixelsStart = new System.Diagnostics.ProcessStartInfo(start.FileName)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-v", "error", "-sseof", "-0.5", "-i", path,
                "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" })
                pixelsStart.ArgumentList.Add(argument);
            using var pixelsProcess = System.Diagnostics.Process.Start(pixelsStart)!;
            Task<string> pixelErrors = pixelsProcess.StandardError.ReadToEndAsync();
            using var pixels = new MemoryStream();
            pixelsProcess.StandardOutput.BaseStream.CopyTo(pixels);
            pixelsProcess.WaitForExit();
            if (pixelsProcess.ExitCode != 0 || pixels.Length != 160 * 90 * 3 || pixels.ToArray().Average(value => (double)value) < 2)
                throw new InvalidOperationException($"Encoded fixture contains no pixels: {path}, {pixelErrors.GetAwaiter().GetResult()}");
        }
    }

    private static void DiskReplaySmoke(string root, bool fast)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Captail.Localization.SetLanguage("en");
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string fixturePath = Path.Combine(root, "fixture.mp4");
        RunFfmpeg(["-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30", "-t", "5", "-c:v", "mpeg4", "-q:v", "4", "-y", fixturePath]);
        foreach ((string codec, string audio) in new[] { ("h264", "aac"), ("hevc", "opus"), ("av1", "aac") })
        {
            var config = new Config
            {
                DiskReplayBufferEnabled = true, BufferSeconds = 7, RecordingResolution = "720p",
                FrameRate = 30, BitrateMbps = 10, Codec = codec, AudioCodec = audio,
                CaptureSystemAudio = true, CaptureMicrophone = true, SeparateAudioTracks = true,
                OutputDirectory = Path.Combine(root, $"{codec}-{audio}-{Guid.NewGuid():N}"),
                EncoderTuning = new() { KeyframeIntervalSeconds = 8 },
            };
            config.Normalize();
            string session;
            using (var engine = new ObsReplayEngine(config, windowsGraphicsCaptureReady: false))
            {
                engine.Start();
                // Isolate native output QA from automatic game/desktop arbitration when
                // SaveReplay refreshes capture. Production encoders, muxer and save flow remain real.
                typeof(ObsReplayEngine).GetField("<IsAutomaticCapture>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, false);
                nint settings = ObsNative.obs_data_create();
                nint source;
                try
                {
                    ObsNative.obs_data_set_bool(settings, "is_local_file", true);
                    ObsNative.obs_data_set_string(settings, "local_file", fixturePath);
                    ObsNative.obs_data_set_bool(settings, "looping", true);
                    source = ObsNative.obs_source_create("ffmpeg_source", "Disk Replay QA fixture", settings, 0);
                }
                finally { ObsNative.obs_data_release(settings); }
                Check(source != 0);
                ObsNative.obs_set_output_source(0, source);
                ObsNative.obs_source_release(source);
                Thread.Sleep(21000);
                session = Directory.GetDirectories(Path.Combine(config.OutputDirectory, ".captail-buffer")).Single();
                Check(Directory.GetFiles(session, "segment_*").Length is >= 3 and <= 7);
                Console.WriteLine($"DISK {codec}/{audio}: {engine.ActiveEncoder}, cached={Directory.GetFiles(session, "segment_*").Length}");
                string first = SaveAndAdvance();
                var ffmpeg = new FfmpegAdapter();
                double firstSeconds = ffmpeg.ReadDurationAsync(first).GetAwaiter().GetResult().TotalSeconds;
                Console.WriteLine($"DISK first duration={firstSeconds:0.000}, bytes={new FileInfo(first).Length}");
                Check(firstSeconds is >= 5 and <= 10);
                Validate(first);
                Thread.Sleep(4500);
                string second = SaveAndAdvance();
                double secondSeconds = ffmpeg.ReadDurationAsync(second).GetAwaiter().GetResult().TotalSeconds;
                Console.WriteLine($"DISK second duration={secondSeconds:0.000}");
                Check(secondSeconds is >= 3 and <= 9);
                Validate(second);
                using (var cancel = new CancellationTokenSource())
                {
                    ReplaySaveOperation operation = engine.BeginSaveReplay(cancel.Token);
                    cancel.Cancel();
                    try { operation.Completion.GetAwaiter().GetResult(); throw new InvalidOperationException("Save ignored cancellation."); }
                    catch (OperationCanceledException) { }
                }
                Thread.Sleep(2500);
                Validate(SaveAndAdvance());
                if (codec == "h264")
                {
                    typeof(ObsReplayEngine).GetMethod("StopGameReplayBufferForIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, null);
                    Check(!engine.IsActive && engine.AvailableReplaySeconds == 0);
                    typeof(ObsReplayEngine).GetMethod("ResumeOrStartGameReplayBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, null);
                    Thread.Sleep(4500);
                    string resumed = SaveAndAdvance();
                    Validate(resumed);
                    Check(ffmpeg.ReadDurationAsync(resumed).GetAwaiter().GetResult().TotalSeconds is >= 3 and <= 8);
                    Console.WriteLine("DISK idle stop/restart uses fresh files and window");
                }

                string SaveAndAdvance()
                {
                    ReplaySaveOperation operation = engine.BeginSaveReplay();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                    while (!engine.HasSaveSnapshotStarted(operation) && !operation.Completion.IsCompleted && DateTime.UtcNow < deadline)
                        Thread.Sleep(15);
                    Check(engine.HasSaveSnapshotStarted(operation));
                    engine.ResetReplayWindow();
                    return operation.Completion.GetAwaiter().GetResult();
                }
                void Validate(string path)
                {
                    Check(File.Exists(path) && new FileInfo(path).Length > 1000);
                    var tracks = ffmpeg.ReadAudioTracksAsync(path).GetAwaiter().GetResult();
                    Check(tracks.Count == ObsReplayEngine.AudioTrackCount(config));
                    Check(tracks.Select(track => track.Title).SequenceEqual(
                        Enumerable.Range(1, tracks.Count).Select(track => ObsReplayEngine.BuildAudioTrackName(config, track))));
                    RunFfmpeg(["-xerror", "-i", path, "-map", "0", "-f", "null", "-"]);
                }
            }
            Check(!Directory.Exists(session));
            if (fast) break;
        }
        static void RunFfmpeg(string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add("error");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Disk replay FFmpeg QA timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
    }

    private static void DiskSessionContracts()
    {
        string root = Path.GetFullPath(Path.Combine(".qa", "disk-contracts", Guid.NewGuid().ToString("N")));
        string cache = Path.Combine(root, ".captail-buffer");
        string abandoned = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, ".owner"), "");
        File.WriteAllText(Path.Combine(abandoned, "segment_orphan.tmp.mp4"), "owned cache");
        File.WriteAllText(Path.Combine(root, "user.mp4"), "preserve user media");
        var config = new Config { OutputDirectory = root, BufferSeconds = 15, MaxReplaySizeMb = 1 };
        string session;
        using (var buffer = new DiskReplayBuffer(config))
        {
            session = buffer.DirectoryPath;
            Check(!Directory.Exists(abandoned));
            using (var secondOwner = new DiskReplayBuffer(config))
                Check(Directory.Exists(session));
            string current = buffer.BeginWindow();
            for (int index = 1; index <= 10; index++)
            {
                using (var file = new FileStream(current, FileMode.Create)) file.SetLength(600 * 1024);
                string next = Path.Combine(session, $"segment_{index}.tmp.mp4");
                buffer.FileChanged(next, index * 2);
                current = next;
            }
            Check(buffer.BufferedBytes <= 1024 * 1024 && buffer.GetAvailableSeconds(20) <= 2);
            Check(SpinWait.SpinUntil(() => Directory.GetFiles(session, "segment_*").Length <= 1, 4000));
            using var cancel = new CancellationTokenSource();
            var operation = buffer.BeginSave(() => true, cancel.Token);
            try { buffer.BeginSave(() => true, CancellationToken.None); throw new InvalidOperationException("Concurrent snapshot allowed."); }
            catch (InvalidOperationException exception) when (exception.Message == Captail.Localization.Text("L.Engine.SavePending")) { }
            cancel.Cancel();
            try { operation.Completion.GetAwaiter().GetResult(); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            try { buffer.BeginSave(() => false, CancellationToken.None); throw new InvalidOperationException("Rejected split ignored."); }
            catch (InvalidOperationException exception) when (exception.Message == Captail.Localization.Text("L.Engine.SaveRejected")) { }
            buffer.AdvanceWindow();
            Check(buffer.GetAvailableSeconds(20) == 0);
        }
        Check(!Directory.Exists(session));
        Check(File.ReadAllText(Path.Combine(root, "user.mp4")) == "preserve user media");
    }

    private static Config Pending(SettingsWindow window) => (Config)Call(window, "CreatePendingConfig")!;
    private static T Control<T>(SettingsWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Select(SettingsWindow window, string name, string tag)
    {
        ComboBox box = Control<ComboBox>(window, name);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == tag);
    }
    private static object? Call(SettingsWindow window, string name, params object[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Recording preset assertion failed.");
    }
    private static void Run(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
