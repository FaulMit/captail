using Captail;

if (args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("Usage: ExportMatrixQa <source> <ffmpeg-directory> <output-directory> [--full-h264]");
    return 2;
}

string source = Path.GetFullPath(args[0]);
string runtime = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var ffmpeg = new FfmpegAdapter(runtime);
IReadOnlyList<AudioTrackInfo> tracks = await ffmpeg.ReadAudioTracksAsync(source);
if (tracks.Count < 2)
    throw new InvalidOperationException("Matrix source needs at least two audio tracks.");

if (args.Length == 4)
{
    if (args[3] != "--full-h264")
        throw new ArgumentException("Unknown QA mode.", nameof(args));
    TimeSpan sourceDuration = await ffmpeg.ReadDurationAsync(source);
    TimeSpan end = TimeSpan.FromSeconds(Math.Min(17, sourceDuration.TotalSeconds - 0.05));
    int[] selected = tracks.Select(track => track.StreamIndex).ToArray();
    int failures = 0;
    foreach (bool merge in new[] { false, true })
    {
        string destination = Path.Combine(output, $"full-h264-merge-{merge}.mp4");
        try
        {
            await ffmpeg.TranscodeAsync(
                source,
                destination,
                TimeSpan.Zero,
                end,
                selected,
                new VideoOutputSettings(VideoCodec: "h264", MergeAudioTracks: merge));
            VideoStreamInfo? video = await ffmpeg.ReadVideoInfoAsync(destination);
            IReadOnlyList<AudioTrackInfo> audio = await ffmpeg.ReadAudioTracksAsync(destination);
            TimeSpan duration = await ffmpeg.ReadDurationAsync(destination);
            if (video?.Codec != "h264" ||
                audio.Count != (merge ? 1 : tracks.Count) ||
                Math.Abs((duration - end).TotalSeconds) > 0.2)
            {
                throw new InvalidOperationException(
                    $"Unexpected output: video={video?.Codec}, audio={audio.Count}, duration={duration}");
            }
            Console.WriteLine($"PASS full HEVC-to-H.264 merge={merge} duration={duration.TotalSeconds:0.000} bytes={new FileInfo(destination).Length}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"FAIL full HEVC-to-H.264 merge={merge}: {exception.Message.Split('\n')[0]}");
        }
    }
    return failures == 0 ? 0 : 1;
}

var cases = new List<(string Extension, string Video, string? Audio, bool Merge, int? Width, int? Height, int? BitRate)>
{
    (".mp4", "h264", null, false, null, null, null),
    (".mp4", "h264", null, true, null, null, null),
};
foreach ((string extension, string[] videos, string?[] audios) in new[]
         {
             (".mp4", new[] { "h264", "hevc", "av1" }, new string?[] { null, "aac" }),
             (".mkv", new[] { "h264", "hevc", "av1", "vp9" }, new string?[] { null, "aac", "opus" }),
             (".mov", new[] { "h264", "hevc" }, new string?[] { null, "aac" }),
             (".webm", new[] { "vp9", "av1" }, new string?[] { "opus" }),
         })
{
    foreach (string video in videos)
    foreach (string? audio in audios)
    foreach (bool merge in new[] { false, true })
        cases.Add((extension, video, audio, merge, 320, 180, 1_000));
}

int failures = 0;
int number = 0;
int[] streamIndices = tracks.Select(track => track.StreamIndex).ToArray();
foreach (var test in cases)
{
    number++;
    string destination = Path.Combine(output, $"case-{number:00}{test.Extension}");
    string expectedAudio = test.Audio ?? "aac";
    int expectedTracks = test.Merge ? 1 : tracks.Count;
    try
    {
        await ffmpeg.TranscodeAsync(
            source,
            destination,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500),
            streamIndices,
            new VideoOutputSettings(
                VideoCodec: test.Video,
                AudioCodec: test.Audio,
                Width: test.Width,
                Height: test.Height,
                VideoBitRateKbps: test.BitRate,
                MergeAudioTracks: test.Merge));
        VideoStreamInfo? video = await ffmpeg.ReadVideoInfoAsync(destination);
        IReadOnlyList<AudioTrackInfo> audio = await ffmpeg.ReadAudioTracksAsync(destination);
        if (video?.Codec != test.Video ||
            audio.Count != expectedTracks ||
            audio.Any(track => track.Codec != expectedAudio) ||
            new FileInfo(destination).Length == 0)
        {
            throw new InvalidOperationException(
                $"Unexpected output: video={video?.Codec}, audio={string.Join(',', audio.Select(track => track.Codec))}");
        }
        Console.WriteLine($"PASS {number:00} {test.Extension} {test.Video}/{test.Audio ?? "copy"} merge={test.Merge} size={test.Width?.ToString() ?? "source"}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {number:00} {test.Extension} {test.Video}/{test.Audio ?? "copy"} merge={test.Merge} size={test.Width?.ToString() ?? "source"}: {exception.Message.Split('\n')[0]}");
    }
}

Console.WriteLine($"EXPORT_MATRIX {(failures == 0 ? "PASS" : "FAIL")}: {cases.Count - failures}/{cases.Count}");
return failures == 0 ? 0 : 1;
