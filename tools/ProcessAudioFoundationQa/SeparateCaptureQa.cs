using System.Reflection;
using System.IO;
using System.Text.Json;
using Captail;
using Captail.Interop;

namespace ProcessAudioFoundationQa;

internal static class SeparateCaptureQa
{
    internal static void Run(string output, bool legacy = false)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Captail;component/Languages/Strings.en.xaml"),
        });
        var config = new Config
        {
            CaptureMode = "recording", CaptureSource = "desktop",
            RecordingResolution = "720p", FrameRate = 30, BitrateMbps = 5,
            Codec = "h264", AudioCodec = "aac", CaptureSystemAudio = true,
            CaptureMicrophone = true, SeparateAudioTracks = true,
            OutputDirectory = output,
        };
        config.Normalize();
        string path;
        bool protectedRoute = false;
        using (var engine = new ObsReplayEngine(config, windowsGraphicsCaptureReady: false))
        {
            engine.Start();
            path = engine.RecordingPath;
            // Isolate audio routing from game hook recognition. Native sources,
            // mixers, gains, encoders, muxer and teardown are production paths.
            typeof(ObsReplayEngine).GetField("_activeGameExecutable",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(engine, "Issue53AudioSignal.exe");
            for (int poll = 0; poll < 55; poll++)
            {
                ProcessSnapshot snapshot = Task.Run(legacy ? ProcessSnapshot.Capture :
                        (Func<ProcessSnapshot>)ProcessSnapshot.CaptureWithAudio)
                    .GetAwaiter().GetResult();
                ProcessAudioReconcileResult result = engine.ReconcileProcessAudio(snapshot);
                if (result.FailedSources != 0 || result.RuntimeFailedSources != 0)
                    throw new InvalidOperationException($"Native capture failure: {result}");
                protectedRoute |= (bool)typeof(ObsReplayEngine).GetField("_separateMixerProtection",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                Thread.Sleep(200);
            }
        }
        if ((!legacy && !protectedRoute) || !File.Exists(path) || new FileInfo(path).Length < 1000)
            throw new InvalidOperationException("Protected three-track recording was not produced.");
        File.WriteAllText(Path.Combine(output, "capture.json"), JsonSerializer.Serialize(
            new { Path = path, ProtectedRoute = protectedRoute, ExpectedTracks = new[] { "System", "Game", "Microphone" } }));
        Console.WriteLine($"SEPARATE_CAPTURE PASS {path}");
    }
}
