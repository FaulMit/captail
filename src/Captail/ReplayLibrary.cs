using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Captail;

public sealed record ReplayClip(
    string Path,
    string Name,
    string? Collection,
    DateTime SavedAt,
    long SizeBytes,
    TimeSpan Duration,
    string? ThumbnailPath);

public sealed class ReplayLibrary
{
    private static readonly HashSet<string> VideoExtensions = new(
        [".mp4", ".mkv", ".mov", ".webm"],
        StringComparer.OrdinalIgnoreCase);
    private readonly FfmpegAdapter _ffmpeg;
    private readonly string _thumbnailDirectory;

    public ReplayLibrary(FfmpegAdapter ffmpeg)
    {
        _ffmpeg = ffmpeg;
        _thumbnailDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captail",
            "thumbnails");
    }

    public async Task<IReadOnlyList<ReplayClip>> GetRecentAsync(
        string rootDirectory,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || !Directory.Exists(rootDirectory))
            return [];

        string root = NormalizeRoot(rootDirectory);
        List<FileInfo> files;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            files = Directory.EnumerateFiles(root, "*", options)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(limit)
                .ToList();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Replay library scan failed: {exception.Message}");
            return [];
        }

        var clips = new ReplayClip[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (index, token) =>
            {
                clips[index] = await LoadClipAsync(root, files[index], token);
            });
        return clips;
    }

    public void Reveal(string rootDirectory, ReplayClip clip)
    {
        string path = ValidateClipPath(rootDirectory, clip.Path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Replay no longer exists.", path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    public void DeleteToRecycleBin(string rootDirectory, ReplayClip clip)
    {
        string path = ValidateClipPath(rootDirectory, clip.Path);
        if (!File.Exists(path))
            return;
        FileSystem.DeleteFile(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
        DeleteThumbnail(clip.ThumbnailPath);
    }

    public async Task<string> TrimAsync(
        string rootDirectory,
        ReplayClip clip,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        if (!File.Exists(source))
            throw new FileNotFoundException("Replay no longer exists.", source);
        if (start < TimeSpan.Zero || end > clip.Duration + TimeSpan.FromMilliseconds(250) || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start));

        string directory = Path.GetDirectoryName(source)!;
        string baseName = Path.GetFileNameWithoutExtension(source);
        string extension = Path.GetExtension(source);
        string destination = UniquePath(
            directory,
            $"{baseName}_trimmed_{DateTime.Now:HH-mm-ss}",
            extension);
        await _ffmpeg.TrimCopyAsync(
            source,
            destination,
            start,
            end,
            audioStreamIndices,
            cancellationToken);
        return destination;
    }

    public async Task<string> TrimOverwriteAsync(
        string rootDirectory,
        ReplayClip clip,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<int>? audioStreamIndices = null,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        if (!File.Exists(source))
            throw new FileNotFoundException("Replay no longer exists.", source);
        if (start < TimeSpan.Zero ||
            end > clip.Duration + TimeSpan.FromMilliseconds(250) ||
            end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        await _ffmpeg.TrimOverwriteAsync(
            source,
            start,
            end,
            audioStreamIndices,
            cancellationToken);
        return source;
    }

    public Task<IReadOnlyList<AudioTrackInfo>> GetAudioTracksAsync(
        string rootDirectory,
        ReplayClip clip,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        return _ffmpeg.ReadAudioTracksAsync(source, cancellationToken);
    }

    public Task<VideoStreamInfo?> GetVideoInfoAsync(
        string rootDirectory,
        ReplayClip clip,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        return _ffmpeg.ReadVideoInfoAsync(source, cancellationToken);
    }

    public async Task<string?> GetAudioWaveformAsync(
        string rootDirectory,
        ReplayClip clip,
        AudioTrackInfo track,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        if (!_ffmpeg.IsAvailable || clip.Duration <= TimeSpan.Zero)
            return null;

        string identity =
            $"{source}|{clip.SizeBytes}|{clip.SavedAt.ToUniversalTime().Ticks}|" +
            $"audio:{track.StreamIndex}";
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string path = Path.Combine(_thumbnailDirectory, $"{hash}_waveform.png");
        if (!File.Exists(path))
        {
            await _ffmpeg.CreateWaveformAsync(
                source,
                path,
                track.StreamIndex,
                1200,
                42,
                cancellationToken);
        }
        return path;
    }

    public async Task<IReadOnlyList<string>> GetTimelineThumbnailsAsync(
        string rootDirectory,
        ReplayClip clip,
        int count,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        if (!_ffmpeg.IsAvailable || count <= 0 || clip.Duration <= TimeSpan.Zero)
            return [];

        string identity = $"{source}|{clip.SizeBytes}|{clip.SavedAt.ToUniversalTime().Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var thumbnails = new List<string>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(
                _thumbnailDirectory,
                $"{hash}_timeline_{index}.jpg");
            if (!File.Exists(path))
            {
                double fraction = (index + 0.5) / count;
                await _ffmpeg.CreateThumbnailAtAsync(
                    source,
                    path,
                    TimeSpan.FromSeconds(clip.Duration.TotalSeconds * fraction),
                    cancellationToken);
            }
            thumbnails.Add(path);
        }
        return thumbnails;
    }

    public async Task<string?> GetPreviewThumbnailAsync(
        string rootDirectory,
        ReplayClip clip,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        string source = ValidateClipPath(rootDirectory, clip.Path);
        if (!_ffmpeg.IsAvailable || clip.Duration <= TimeSpan.Zero)
            return clip.ThumbnailPath;

        long bucket = Math.Max(0, (long)(position.TotalMilliseconds / 100) * 100);
        string identity = $"{source}|{clip.SizeBytes}|{clip.SavedAt.ToUniversalTime().Ticks}|{bucket}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string path = Path.Combine(_thumbnailDirectory, $"{hash}_preview.jpg");
        if (!File.Exists(path))
        {
            await _ffmpeg.CreateThumbnailAtAsync(
                source,
                path,
                TimeSpan.FromMilliseconds(bucket),
                cancellationToken);
        }
        return path;
    }

    private async Task<ReplayClip> LoadClipAsync(
        string rootDirectory,
        FileInfo file,
        CancellationToken cancellationToken)
    {
        TimeSpan duration = TimeSpan.Zero;
        string? thumbnail = null;
        if (_ffmpeg.IsAvailable)
        {
            try
            {
                duration = await _ffmpeg.ReadDurationAsync(file.FullName, cancellationToken);
                thumbnail = ThumbnailPath(file);
                if (!File.Exists(thumbnail))
                    await _ffmpeg.CreateThumbnailAsync(file.FullName, thumbnail, duration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.Write($"Replay metadata failed ({file.Name}): {exception.Message}");
                thumbnail = null;
            }
        }

        string? collection = Path.GetRelativePath(rootDirectory, file.DirectoryName!);
        if (collection == ".")
            collection = null;
        return new ReplayClip(
            file.FullName,
            file.Name,
            collection,
            file.LastWriteTime,
            file.Length,
            duration,
            thumbnail);
    }

    private string ThumbnailPath(FileInfo file)
    {
        string key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_thumbnailDirectory, Convert.ToHexString(hash) + ".jpg");
    }

    private static string ValidateClipPath(string rootDirectory, string clipPath)
    {
        string root = NormalizeRoot(rootDirectory);
        string path = Path.GetFullPath(clipPath);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replay path is outside configured library.");
        if (!VideoExtensions.Contains(Path.GetExtension(path)))
            throw new InvalidOperationException("Unsupported replay file type.");
        return path;
    }

    private static string NormalizeRoot(string rootDirectory) =>
        Path.GetFullPath(rootDirectory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string UniquePath(string directory, string baseName, string extension)
    {
        string candidate = Path.Combine(directory, baseName + extension);
        for (int suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
        return candidate;
    }

    private static void DeleteThumbnail(string? thumbnailPath)
    {
        try
        {
            if (thumbnailPath is not null && File.Exists(thumbnailPath))
                File.Delete(thumbnailPath);
        }
        catch (IOException)
        {
            // Cache cleanup is best effort.
        }
    }
}
