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
    double FrameRate);

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
            if (stream.TryGetProperty("tags", out JsonElement tags) &&
                tags.TryGetProperty("title", out JsonElement titleElement))
            {
                title = titleElement.GetString();
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
                "-show_entries", "stream=codec_name,width,height,avg_frame_rate,r_frame_rate",
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
        return new VideoStreamInfo(
            codec,
            width,
            height,
            ParseFrameRate(frameRateText));
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
        CancellationToken cancellationToken = default)
    {
        if (start < TimeSpan.Zero || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start));

        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp" +
            Path.GetExtension(destinationPath);
        try
        {
            var arguments = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-t", (end - start).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-map", "0:v:0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
            };
            if (audioStreamIndices is null)
            {
                arguments.AddRange(["-map", "0:a?"]);
            }
            else
            {
                foreach (int streamIndex in audioStreamIndices.Distinct())
                    arguments.AddRange(["-map", $"0:{streamIndex}?"]);
            }
            if (Path.GetExtension(destinationPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(destinationPath).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-movflags", "+faststart"]);
            }
            arguments.AddRange(["-y", temporaryPath]);
            await RunAsync(
                _ffmpegPath,
                arguments,
                TimeSpan.FromMinutes(5),
                cancellationToken);

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("FFmpeg produced an empty clip.");
            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task TrimOverwriteAsync(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
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
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(
                replacementPath,
                sourcePath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(replacementPath))
                File.Delete(replacementPath);
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
