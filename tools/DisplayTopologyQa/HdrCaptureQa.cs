using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Captail;
using Captail.Interop;

internal static class HdrCaptureQa
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("obs.dll")] private static extern nint obs_source_get_settings(nint source);
    [DllImport("obs.dll")] private static extern nint obs_data_get_string(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool obs_data_get_bool(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll")]
    private static extern int obs_source_get_color_space(nint source, nuint count, [In] int[] preferred);

    internal static void Run()
    {
        var monitors = CaptureInterop.EnumerateMonitors();
        foreach (var monitor in monitors)
        {
            bool? enabled = DisplayColorState.IsHdrEnabled(monitor.Handle);
            Check(enabled.HasValue, $"Windows returns current HDR state for display {monitor.Index + 1}: {enabled}");
        }
        Check(DisplayColorState.IsHdrEnabled(new nint(-123)) is null, "Missing display remains unknown, not SDR");
        foreach (string source in new[] { "desktop", "game" })
            RunSource(source, monitors.First());
        if (monitors.Count > 1)
            RunSource("desktop", monitors.Last());
        Console.WriteLine("HDR_CAPTURE_QA PASS");
    }

    private static void RunSource(string source, CaptureInterop.MonitorInfo monitor)
    {
        DisplayColorState.HdrState? hdr = new(true, true);
        var config = new Config
        {
            CaptureSource = source, FrameRate = 30, RecordingResolution = "720p",
            CaptureMode = "recording", CaptureSystemAudio = false, CaptureMicrophone = false,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "CaptailHdrQa"),
        };
        Directory.CreateDirectory(config.OutputDirectory);
        using var engine = new ObsReplayEngine(config, true, () => [monitor], _ => hdr);
        engine.Start();
        nint game = Field<nint>(engine, "_gameVideoSource");
        // Keep this probe isolated from real games, including ones already hooked by Captail.
        nint settings = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(settings, "capture_mode", "window");
            ObsNative.obs_data_set_string(settings, "window", "Captail HDR QA:no-such-window:no-such-process.exe");
            ObsNative.obs_source_update(game, settings);
        }
        finally { ObsNative.obs_data_release(settings); }
        Check(ReadString(game, "rgb10a2_space") == "2100pq", $"{source}: HDR display selects PQ for 10-bit game input");
        if (source == "desktop")
        {
            Check(!ReadBool(Field<nint>(engine, "_desktopVideoSource"), "force_sdr"),
                "Desktop keeps native HDR/SDR surface detection enabled");
            nint desktop = Field<nint>(engine, "_desktopVideoSource");
            bool liveHdr = DisplayColorState.IsHdrEnabled(monitor.Handle) == true;
            var ready = Stopwatch.StartNew();
            int color = -1;
            do
            {
                color = obs_source_get_color_space(desktop, 4, [0, 1, 2, 3]);
                if (ObsNative.obs_source_get_width(desktop) > 0 && (liveHdr ? color is 2 or 3 : color is 0 or 1))
                    break;
                Thread.Sleep(50);
            } while (ready.Elapsed < TimeSpan.FromSeconds(6));
            Check(ObsNative.obs_source_get_width(desktop) > 0 && (liveHdr ? color is 2 or 3 : color is 0 or 1),
                $"Native desktop surface matches Windows HDR={liveHdr}, source color space={color}");
        }
        nint output = Field<nint>(engine, "_output");
        for (int i = 0; i < 4; i++)
        {
            bool enabled = hdr?.Active != true;
            hdr = new(enabled, enabled);
            engine.RefreshCaptureState();
            Check(ReadString(game, "rgb10a2_space") == (enabled ? "2100pq" : "srgb"),
                $"{source}: live display HDR={hdr} updates native game settings");
            Check(Field<nint>(engine, "_gameVideoSource") == game, "Color changes retain game source");
            Check(Field<nint>(engine, "_output") == output, "Color changes retain recording output");
            Thread.Sleep(150);
        }
        hdr = null;
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "2100pq", "Transient Windows query failure retains current HDR interpretation");
        hdr = new(false, false);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb", "Recovered Windows SDR state applies after failed query");
        hdr = new(false, true);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb",
            "HDR output cannot release SDR while Windows Use HDR is off");
        hdr = new(true, true);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "2100pq", "Windows Use HDR off/on releases SDR");
        hdr = new(true, false);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb", "Inactive HDR output locks SDR");
        hdr = new(true, true);
        for (int i = 0; i < 8; i++) engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb",
            "Active HDR returning without Windows Use HDR off/on retains SDR");
        hdr = null;
        engine.RefreshCaptureState();
        hdr = new(true, true);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb", "Query recovery cannot release SDR lock");
        hdr = new(false, false);
        engine.RefreshCaptureState();
        hdr = new(true, false);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "srgb", "Windows HDR enable waits for active HDR output");
        hdr = new(true, true);
        engine.RefreshCaptureState();
        Check(ReadString(game, "rgb10a2_space") == "2100pq", "Pending Windows HDR enable applies when output is ready");
        Check(Field<nint>(engine, "_output") == output, "SDR lock and release retain recording output");
        hdr = new(true, false);
        engine.RefreshCaptureState();
        hdr = new(true, true);
        engine.RefreshCaptureState();
        if (source == "desktop")
        {
            MethodInfo changeSource = typeof(ObsReplayEngine).GetMethod("SwitchAutomaticCapture", Private)!;
            changeSource.Invoke(engine, [true, "CaptailHdrQa.exe"]);
            Check(ReadString(game, "rgb10a2_space") == "srgb", "Desktop to Game source switch retains SDR lock");
            changeSource.Invoke(engine, [false, ""]);
            Check(ReadString(game, "rgb10a2_space") == "srgb", "Game to Desktop source switch retains SDR lock");
        }
        Check(ReadString(game, "rgb10a2_space") == "srgb", "Session ends locked to SDR; next session selects HDR afresh");
        // Source updates must merge, rather than reverting capture selection or other options.
        Check(ReadString(game, "capture_mode") == "window", "Color update retains existing game selection");
        if (source == "desktop")
        {
            int frames = engine.EncodedFrameCount;
            var wait = Stopwatch.StartNew();
            while (engine.EncodedFrameCount <= frames && wait.Elapsed < TimeSpan.FromSeconds(4))
                Thread.Sleep(50);
            Check(engine.EncodedFrameCount > frames, "Encoded frames progress after HDR transitions");
        }
        string file = engine.RecordingPath;
        engine.Dispose();
        if (source == "desktop")
        {
            Check(File.Exists(file) && new FileInfo(file).Length > 1000, "Recording survives live HDR policy changes");
            Program.CheckDecodedFrame(file);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static string ReadString(nint source, string key)
    {
        nint settings = obs_source_get_settings(source);
        try { return Marshal.PtrToStringUTF8(obs_data_get_string(settings, key)) ?? ""; }
        finally { ObsNative.obs_data_release(settings); }
    }
    private static bool ReadBool(nint source, string key)
    {
        nint settings = obs_source_get_settings(source);
        try { return obs_data_get_bool(settings, key); }
        finally { ObsNative.obs_data_release(settings); }
    }
    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
