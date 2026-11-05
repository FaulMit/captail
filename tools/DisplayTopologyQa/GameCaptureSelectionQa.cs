using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Captail;
using Captail.Interop;

internal static class GameCaptureSelectionQa
{
    [DllImport("obs.dll")] private static extern nint obs_source_get_settings(nint source);
    [DllImport("obs.dll")] private static extern nint obs_data_get_string(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("obs.dll")] private static extern long obs_data_get_int(nint data,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    internal static void Run()
    {
        foreach (string source in new[] { "game", "desktop" })
        {
            var foreground = new CaptureInterop.ForegroundAppInfo("ShareX.exe", @"C:\Apps\ShareX.exe", true)
            {
                Window = 123, Title = "ShareX", WindowClass = "WindowsForms10.Window.8.app.0.abc",
            };
            var config = new Config
            {
                CaptureSource = source, FrameRate = 30, RecordingResolution = "720p",
                CaptureSystemAudio = false, CaptureMicrophone = false,
                OutputDirectory = Path.Combine(Path.GetTempPath(), "CaptailGameSelectionQa"),
            };
            Directory.CreateDirectory(config.OutputDirectory);
            using var engine = new ObsReplayEngine(config, true, CaptureInterop.EnumerateMonitors,
                _ => new DisplayColorState.HdrState(false, false), () => foreground);
            engine.Start();
            nint game = Field<nint>(engine, "_gameVideoSource");
            nint output = Field<nint>(engine, "_output");
            engine.RefreshCaptureState();
            Check(Read(game, "capture_mode") == "window", $"{source}: native OBS uses explicit window selection");
            Check(Read(game, "window") == "", $"{source}: fullscreen ShareX never becomes native target");
            Check(engine.ActiveGameExecutable == "" && !engine.IsGameHooked,
                $"{source}: ShareX never becomes detected game");
            Check(!engine.IsGameCaptureDetectorActive, $"{source}: GameOnly detector stays dormant for ShareX");

            foreground = foreground with
            {
                Executable = "CaptailQaUnknownGame.exe", Title = "New: Game #1", WindowClass = "UnityWndClass",
            };
            engine.RefreshCaptureState();
            Check(Read(game, "window") == "New#3A Game #221:UnityWndClass:CaptailQaUnknownGame.exe",
                $"{source}: unknown foreground game reaches native target with OBS escaping");
            nint settings = obs_source_get_settings(game);
            try { Check(obs_data_get_int(settings, "priority") == 2, "Native matching requires same executable"); }
            finally { ObsNative.obs_data_release(settings); }

            // A title that changes every frame must not reset an established hook.
            foreground = foreground with { Title = "New Game FPS 144" };
            engine.RefreshCaptureState();
            Check(Read(game, "window").StartsWith("New#3A Game #221:"), "Same window does not retarget on title changes");
            foreground = foreground with { Window = 456, Executable = "CaptailQaOtherGame.exe", Title = "Other Game" };
            engine.RefreshCaptureState();
            Check(Read(game, "window") == "Other Game:UnityWndClass:CaptailQaOtherGame.exe",
                "Focus change replaces previous game's native target");

            foreground = foreground with { Executable = "ShareX.exe", WindowClass = "WindowsForms10.Window" };
            engine.RefreshCaptureState();
            Check(Read(game, "window") == "", "Alt+Tab to ShareX clears game target");
            foreground = foreground with { Executable = "UnknownTool.exe", WindowClass = "HwndWrapper[Tool.exe]" };
            engine.RefreshCaptureState();
            Check(Read(game, "window") == "", "Unknown fullscreen desktop UI cannot become target");
            Check(engine.ActiveGameExecutable == "", "No stale game remains after focus loss");
            Check(Field<nint>(engine, "_gameVideoSource") == game && Field<nint>(engine, "_output") == output,
                "Target changes preserve OBS source and replay output");
        }
        Console.WriteLine("GAME_SELECTION_NATIVE_QA PASS");
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static string Read(nint source, string key)
    {
        nint settings = obs_source_get_settings(source);
        try { return Marshal.PtrToStringUTF8(obs_data_get_string(settings, key)) ?? ""; }
        finally { ObsNative.obs_data_release(settings); }
    }

    private static void Check(bool passed, string message)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {message}");
        if (!passed) throw new InvalidOperationException(message);
    }
}
