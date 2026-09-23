using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Captail;

public sealed record AudioTrackInfo(
    int StreamIndex,
    int Ordinal,
    string Codec,
    string? Title,
    int Channels);

public sealed record VideoStreamInfo(
    string Codec,
    int Width,
    int Height,
    double FrameRate,
    int BitRateKbps = 0);

public sealed record VideoOutputSettings(
    string? VideoCodec = null,
    string? AudioCodec = null,
    int? Width = null,
    int? Height = null,
    int? VideoBitRateKbps = null,
    bool MergeAudioTracks = false)
{
    public bool HasEncodingChanges =>
        VideoCodec is not null ||
        AudioCodec is not null ||
        Width is not null ||
        Height is not null ||
        VideoBitRateKbps is not null ||
        MergeAudioTracks;
}

public sealed class FfmpegAdapter
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public FfmpegAdapter(string? runtimeDirectory = null)
    {
        string root = runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        _ffmpegPath = Path.Combine(root, "ffmpeg.exe");
        _ffprobePath = Path.Combine(root, "ffprobe.exe");
    }

    public bool IsAvailable => File.Exists(_ffmpegPath) && File.Exists(_ffprobePath);

    public async Task<TimeSpan> ReadDurationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string output = await RunAsync(
            _ffprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", path],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        if (document.RootElement.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationElement) &&
            double.TryParse(
                durationElement.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds) &&
            double.IsFinite(seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.Zero;
    }

    public async Task<IReadOnlyList<AudioTrackInfo>> ReadAudioTracksAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string output = await RunAsync(
            _ffprobePath,
            ["-v", "error", "-show_streams", "-of", "json", path],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out JsonElement streams))
            return [];

        var tracks = new List<AudioTrackInfo>();
        foreach (JsonElement stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out JsonElement type) ||
                !string.Equals(type.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int streamIndex = stream.TryGetProperty("index", out JsonElement index)
                ? index.GetInt32()
                : tracks.Count;
            string codec = stream.TryGetProperty("codec_name", out JsonElement codecName)
                ? codecName.GetString() ?? "audio"
                : "audio";
            int channels = stream.TryGetProperty("channels", out JsonElement channelCount)
                ? channelCount.GetInt32()
                : 0;
            string? title = null;
            if (stream.TryGetProperty("tags", out JsonElement tags))
            {
                foreach (string tagName in new[] { "title", "name", "handler_name" })
                {
                    if (!tags.TryGetProperty(tagName, out JsonElement titleElement))
                        continue;
                    string? candidate = titleElement.GetString();
                    if (string.IsNullOrWhiteSpace(candidate) ||
                        candidate.Equals("SoundHandler", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    title = candidate;
                    break;
                }
            }
            tracks.Add(new AudioTrackInfo(
                streamIndex,
                tracks.Count,
                codec,
                title,
                channels));
        }
        return tracks;
    }

    public async Task<VideoStreamInfo?> ReadVideoInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string output = await RunAsync(
            _ffprobePath,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries",
                "stream=codec_name,width,height,avg_frame_rate,r_frame_rate,bit_rate:format=bit_rate",
                "-of", "json",
                path,
            ],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) ||
            streams.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement stream = streams[0];
        string codec = stream.TryGetProperty("codec_name", out JsonElement codecName)
            ? codecName.GetString() ?? "video"
            : "video";
        int width = stream.TryGetProperty("width", out JsonElement widthElement)
            ? widthElement.GetInt32()
            : 0;
        int height = stream.TryGetProperty("height", out JsonElement heightElement)
            ? heightElement.GetInt32()
            : 0;
        string? frameRateText =
            stream.TryGetProperty("avg_frame_rate", out JsonElement averageRate)
                ? averageRate.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(frameRateText) || frameRateText == "0/0")
        {
            frameRateText = stream.TryGetProperty("r_frame_rate", out JsonElement rawRate)
                ? rawRate.GetString()
                : null;
        }
        int bitRateKbps = ReadBitRateKbps(stream);
        if (bitRateKbps == 0 &&
            document.RootElement.TryGetProperty("format", out JsonElement format))
        {
            bitRateKbps = ReadBitRateKbps(format);
        }
        return new VideoStreamInfo(
            codec,
            width,
            height,
            ParseFrameRate(frameRateText),
            bitRateKbps);
    }

    public async Task CreateThumbnailAsync(
        string sourcePath,
        string destinationPath,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        double seekSeconds = Math.Clamp(duration.TotalSeconds * 0.2, 0, 5);
        await CreateThumbnailAtAsync(
            sourcePath,
            destinationPath,
            TimeSpan.FromSeconds(seekSeconds),
            cancellationToken);
    }

    public async Task CreateThumbnailAtAsync(
        string sourcePath,
        string destinationPath,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await RunAsync(
            _ffmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-ss", Math.Max(0, position.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-frames:v", "1",
                "-vf", "scale=320:-2:flags=lanczos",
                "-q:v", "4",
                "-y", destinationPath,
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public async Task CreatePreviewProxyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // AV1 captures produced by older drivers can be accepted by FFmpeg
            // but rejected by libmpv/dav1d. A lightweight 60 FPS H.264 proxy
            // keeps preview/navigation usable without changing source clip.
            await RunAsync(
                _ffmpegPath,
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-i", sourcePath,
                    "-map", "0:v:0",
                    "-map", "0:a:0?",
                    "-vf", "fps=60",
                    "-c:v", "h264_mf",
                    "-b:v", "12M",
                    "-g", "120",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-b:a", "160k",
                    "-movflags", "+faststart",
                    "-f", "mp4",
                    "-y", temporaryPath,
                ],
                TimeSpan.FromMinutes(10),
                cancellationToken);

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("FFmpeg produced an empty preview proxy.");
            await MoveFileWithRetryAsync(temporaryPath, destinationPath, cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFile(temporaryPath);
        }
    }

    public async Task CreateWaveformAsync(
        string sourcePath,
        string destinationPath,
        int streamIndex,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string filter =
            $"[0:{streamIndex}]aformat=channel_layouts=stereo," +
            $"showwavespic=s={Math.Max(320, width)}x{Math.Max(24, height)}:" +
            "colors=0x53D7B7:draw=full:scale=sqrt[v]";
        await RunAsync(
            _ffmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", sourcePath,
                "-filter_complex", filter,
                "-map", "[v]",
                "-frames:v", "1",
                "-y", destinationPath,
            ],
            TimeSpan.FromSeconds(45),
            cancellationToken);
    }

    public async Task TrimCopyAsync(
        string sourcePath,
        string destinationPath,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
        bool mergeAudioTracks = false,
        CancellationToken cancellationToken = default)
    {
        if (start < TimeSpan.Zero || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start));

        string outputFormat = OutputFormatForPath(destinationPath);
        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            int[] selectedAudioStreams = audioStreamIndices?
                .Distinct()
                .ToArray() ?? [];
            bool mixSelectedAudio = mergeAudioTracks && selectedAudioStreams.Length > 1;
            var arguments = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-t", (end - start).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            };
            if (mixSelectedAudio)
            {
                string inputs = string.Concat(
                    selectedAudioStreams.Select(streamIndex => $"[0:{streamIndex}]"));
                arguments.AddRange(
                [
                    "-filter_complex",
                    $"{inputs}amix=inputs={selectedAudioStreams.Length}:" +
                    "duration=longest:dropout_transition=0:normalize=0," +
                    "alimiter=limit=0.95[mixed_audio]",
                    "-map", "0:v:0",
                    "-map", "[mixed_audio]",
                    "-c:v", "copy",
                ]);
                bool opusOutput = Path.GetExtension(destinationPath).Equals(
                    ".mkv",
                    StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(destinationPath).Equals(
                        ".webm",
                        StringComparison.OrdinalIgnoreCase);
                arguments.AddRange(
                [
                    "-c:a", opusOutput ? "libopus" : "aac",
                    "-b:a", opusOutput ? "192k" : "320k",
                    "-metadata:s:a:0", "title=Mixed audio",
                ]);
            }
            else
            {
                arguments.AddRange(["-map", "0:v:0", "-c", "copy"]);
                if (audioStreamIndices is null)
                {
                    arguments.AddRange(["-map", "0:a?"]);
                }
                else
                {
                    foreach (int streamIndex in selectedAudioStreams)
                        arguments.AddRange(["-map", $"0:{streamIndex}?"]);
                }
            }
            arguments.AddRange(["-avoid_negative_ts", "make_zero"]);
            if (Path.GetExtension(destinationPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(destinationPath).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-movflags", "+faststart"]);
            }
            arguments.AddRange(["-f", outputFormat, "-y", temporaryPath]);
            await RunAsync(
                _ffmpegPath,
                arguments,
                TimeSpan.FromMinutes(5),
                cancellationToken);

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("FFmpeg produced an empty clip.");
            await MoveFileWithRetryAsync(
                temporaryPath,
                destinationPath,
                cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFile(temporaryPath);
        }
    }

    public async Task TrimOverwriteAsync(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
        bool mergeAudioTracks = false,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string replacementPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(sourcePath)}." +
            $"{Guid.NewGuid():N}.replacement{Path.GetExtension(sourcePath)}");
        try
        {
            await TrimCopyAsync(
                sourcePath,
                replacementPath,
                start,
                end,
                audioStreamIndices,
                mergeAudioTracks,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ReplaceFileWithRetryAsync(
                replacementPath,
                sourcePath,
                cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFile(replacementPath);
        }
    }

    public async Task TranscodeAsync(
        string sourcePath,
        string destinationPath,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
        VideoOutputSettings? outputSettings = null,
        CancellationToken cancellationToken = default)
    {
        if (start < TimeSpan.Zero || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start));

        VideoOutputSettings settings = outputSettings ?? new VideoOutputSettings();
        string extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        string outputFormat = OutputFormatForPath(destinationPath);
        VideoStreamInfo? videoInfo = await ReadVideoInfoAsync(
            sourcePath,
            cancellationToken);
        string videoCodec = NormalizeVideoCodec(
            settings.VideoCodec ?? videoInfo?.Codec ?? "h264");
        string audioCodec = NormalizeAudioCodec(
            settings.AudioCodec ?? DefaultAudioCodec(extension));
        bool encodeVideo =
            settings.VideoCodec is not null ||
            settings.Width is not null ||
            settings.Height is not null ||
            settings.VideoBitRateKbps is not null;
        if (encodeVideo &&
            settings.VideoCodec is null &&
            videoCodec is not ("h264" or "hevc" or "av1" or "vp9"))
        {
            videoCodec = extension == ".webm" ? "vp9" : "h264";
        }
        ValidateOutputCodecs(extension, videoCodec, audioCodec, encodeVideo,
            settings.AudioCodec is not null || settings.MergeAudioTracks);
        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            int[] selectedAudioStreams = audioStreamIndices?
                .Distinct()
                .ToArray() ?? [];
            bool mixSelectedAudio =
                settings.MergeAudioTracks && selectedAudioStreams.Length > 1;
            var arguments = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-t", (end - start).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-map", "0:v:0",
            };
            if (mixSelectedAudio)
            {
                string inputs = string.Concat(
                    selectedAudioStreams.Select(streamIndex => $"[0:{streamIndex}]"));
                arguments.AddRange(
                [
                    "-filter_complex",
                    $"{inputs}amix=inputs={selectedAudioStreams.Length}:" +
                    "duration=longest:dropout_transition=0:normalize=0," +
                    "alimiter=limit=0.95[mixed_audio]",
                    "-map", "[mixed_audio]",
                    "-metadata:s:a:0", "title=Mixed audio",
                ]);
            }
            else if (audioStreamIndices is null)
            {
                arguments.AddRange(["-map", "0:a?"]);
            }
            else
            {
                foreach (int streamIndex in selectedAudioStreams)
                    arguments.AddRange(["-map", $"0:{streamIndex}?"]);
            }

            if (encodeVideo)
            {
                string videoFilter = settings is { Width: > 0, Height: > 0 }
                    ? $"scale=w={settings.Width}:h={settings.Height}:" +
                      "force_original_aspect_ratio=decrease," +
                      $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2," +
                      "format=yuv420p"
                    : "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p";
                arguments.AddRange(["-vf", videoFilter]);
                int bitrateKbps = settings.VideoBitRateKbps ??
                    (videoInfo?.BitRateKbps > 0
                        ? videoInfo.BitRateKbps
                        : CalculateH264BitrateKbps(videoInfo));
                AddVideoEncoderArguments(
                    arguments,
                    videoCodec,
                    Math.Clamp(bitrateKbps, 500, 100_000));
            }
            else
            {
                arguments.AddRange(["-c:v", "copy"]);
            }
            bool encodeAudio = settings.AudioCodec is not null || mixSelectedAudio;
            arguments.AddRange(encodeAudio
                ? AudioEncoderArguments(audioCodec)
                : ["-c:a", "copy"]);
            arguments.AddRange(["-map_metadata", "0", "-avoid_negative_ts", "make_zero"]);
            if (extension is ".mp4" or ".mov")
                arguments.AddRange(["-movflags", "+faststart"]);
            arguments.AddRange(["-f", outputFormat, "-y", temporaryPath]);

            if (encodeVideo && videoCodec == "hevc")
            {
                int encoderIndex = arguments.IndexOf("-c:v") + 1;
                InvalidOperationException? lastEncoderError = null;
                foreach (string encoder in new[]
                         { "hevc_mf", "hevc_nvenc", "hevc_amf", "hevc_qsv" })
                {
                    arguments[encoderIndex] = encoder;
                    try
                    {
                        await RunAsync(
                            _ffmpegPath,
                            arguments,
                            TimeSpan.FromHours(2),
                            cancellationToken);
                        lastEncoderError = null;
                        break;
                    }
                    catch (InvalidOperationException exception)
                        when (!cancellationToken.IsCancellationRequested &&
                              (exception.Message.Contains(
                                   encoder,
                                   StringComparison.OrdinalIgnoreCase) ||
                               exception.Message.Contains(
                                   "Unknown encoder",
                                   StringComparison.OrdinalIgnoreCase)))
                    {
                        lastEncoderError = exception;
                        Log.Write($"HEVC encoder {encoder} failed: {exception.Message}");
                        TryDeleteWorkingFile(temporaryPath);
                    }
                }
                if (lastEncoderError is not null)
                {
                    throw new InvalidOperationException(
                        "HEVC export failed with all available encoders. " +
                        lastEncoderError.Message,
                        lastEncoderError);
                }
            }
            else
            {
                await RunAsync(
                    _ffmpegPath,
                    arguments,
                    TimeSpan.FromHours(2),
                    cancellationToken);
            }
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("FFmpeg produced an empty export.");

            await RunFileOperationWithRetryAsync(
                () => File.Move(temporaryPath, destinationPath, overwrite: true),
                cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFile(temporaryPath);
        }
    }

    public async Task TranscodeOverwriteAsync(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices,
        VideoOutputSettings outputSettings,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string replacementPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(sourcePath)}." +
            $"{Guid.NewGuid():N}.replacement{Path.GetExtension(sourcePath)}");
        try
        {
            await TranscodeAsync(
                sourcePath,
                replacementPath,
                start,
                end,
                audioStreamIndices,
                outputSettings,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ReplaceFileWithRetryAsync(
                replacementPath,
                sourcePath,
                cancellationToken);
        }
        finally
        {
            TryDeleteWorkingFile(replacementPath);
        }
    }

    private static void AddVideoEncoderArguments(
        List<string> arguments,
        string codec,
        int bitrateKbps)
    {
        switch (codec)
        {
            case "h264":
                arguments.AddRange(
                [
                    "-c:v", "libopenh264",
                    "-profile:v", "high",
                    "-rc_mode", "bitrate",
                    "-b:v", $"{bitrateKbps}k",
                    "-maxrate", $"{bitrateKbps * 3 / 2}k",
                    "-bufsize", $"{bitrateKbps * 2}k",
                ]);
                break;
            case "hevc":
                arguments.AddRange(
                [
                    "-c:v", "hevc_mf",
                    "-b:v", $"{bitrateKbps}k",
                ]);
                break;
            case "av1":
                arguments.AddRange(
                [
                    "-c:v", "libsvtav1",
                    "-preset", "8",
                    "-b:v", $"{bitrateKbps}k",
                ]);
                break;
            case "vp9":
                arguments.AddRange(
                [
                    "-c:v", "libvpx-vp9",
                    "-b:v", $"{bitrateKbps}k",
                    "-deadline", "good",
                    "-cpu-used", "4",
                    "-row-mt", "1",
                ]);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported video codec '{codec}'.");
        }
    }

    private static string[] AudioEncoderArguments(string codec) => codec switch
    {
        "aac" => ["-c:a", "aac", "-b:a", "256k"],
        "opus" => ["-c:a", "libopus", "-b:a", "192k"],
        _ => throw new NotSupportedException(
            $"Unsupported audio codec '{codec}'."),
    };

    private static void ValidateOutputCodecs(
        string extension,
        string videoCodec,
        string audioCodec,
        bool encodeVideo,
        bool encodeAudio)
    {
        if (extension == ".webm" &&
            ((encodeVideo && videoCodec is not ("vp9" or "av1")) ||
             (encodeAudio && audioCodec != "opus")))
        {
            throw new NotSupportedException(
                "WebM export supports VP9 or AV1 video with Opus audio.");
        }
        if (extension is ".mp4" or ".mov" &&
            encodeAudio && audioCodec != "aac")
        {
            throw new NotSupportedException(
                "MP4 and MOV export support AAC audio.");
        }
        if (extension == ".mp4" &&
            encodeVideo && videoCodec is not ("h264" or "hevc" or "av1"))
        {
            throw new NotSupportedException(
                "MP4 export supports H.264, HEVC, or AV1 video.");
        }
        if (extension == ".mov" &&
            encodeVideo && videoCodec is not ("h264" or "hevc"))
        {
            throw new NotSupportedException(
                "MOV export supports H.264 or HEVC video.");
        }
    }

    private static string NormalizeVideoCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "h265" => "hevc",
            string value => value,
        };

    private static string NormalizeAudioCodec(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "libopus" => "opus",
            string value => value,
        };

    private static string DefaultAudioCodec(string extension) =>
        extension == ".webm" ? "opus" : "aac";

    private static int CalculateH264BitrateKbps(VideoStreamInfo? videoInfo)
    {
        if (videoInfo is null)
            return 12_000;

        double bitsPerSecond =
            videoInfo.Width * (double)videoInfo.Height *
            Math.Max(24, videoInfo.FrameRate) * 0.08;
        return (int)Math.Clamp(bitsPerSecond / 1000, 4_000, 60_000);
    }

    private static int ReadBitRateKbps(JsonElement element)
    {
        if (!element.TryGetProperty("bit_rate", out JsonElement bitRateElement) ||
            !long.TryParse(
                bitRateElement.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long bitsPerSecond) ||
            bitsPerSecond <= 0)
        {
            return 0;
        }
        return (int)Math.Clamp(bitsPerSecond / 1000, 1, int.MaxValue);
    }

    private static string OutputFormatForPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mkv" => "matroska",
            ".mp4" => "mp4",
            ".mov" => "mov",
            ".webm" => "webm",
            string extension => throw new NotSupportedException(
                $"Unsupported replay container '{extension}'."),
        };

    internal static Task MoveFileWithRetryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        RunFileOperationWithRetryAsync(
            () => File.Move(sourcePath, destinationPath),
            cancellationToken);

    private static Task ReplaceFileWithRetryAsync(
        string replacementPath,
        string sourcePath,
        CancellationToken cancellationToken) =>
        RunFileOperationWithRetryAsync(
            () => File.Replace(
                replacementPath,
                sourcePath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true),
            cancellationToken);

    private static async Task RunFileOperationWithRetryAsync(
        Action operation,
        CancellationToken cancellationToken)
    {
        const int attempts = 8;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(250, 40 * attempt)),
                    cancellationToken);
            }
        }
    }

    private static void TryDeleteWorkingFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException exception)
        {
            Log.Write($"Temporary replay cleanup deferred ({Path.GetFileName(path)}): {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Write($"Temporary replay cleanup denied ({Path.GetFileName(path)}): {exception.Message}");
        }
    }

    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("Bundled FFmpeg runtime is unavailable.", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            string output = await standardOutput;
            string error = await standardError;
            if (process.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(error)
                    ? $"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}."
                    : error.Trim();
                throw new InvalidOperationException(message);
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(executable)} timed out after {timeout.TotalSeconds:0} seconds.");
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process is already gone or access was revoked.
        }
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        string[] parts = value.Split('/', 2);
        if (!double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double numerator))
        {
            return 0;
        }
        if (parts.Length == 1)
            return double.IsFinite(numerator) ? Math.Max(0, numerator) : 0;
        if (!double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double denominator) || denominator == 0)
        {
            return 0;
        }
        double rate = numerator / denominator;
        return double.IsFinite(rate) ? Math.Max(0, rate) : 0;
    }
}
