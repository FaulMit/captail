using Captail;

string root = Path.Combine(Path.GetTempPath(), "CaptailReplayLibraryQa-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string[] saved =
    [
        "Replay_saved.mp4",
        "Game/Recording_saved.mkv",
        "Game/segment_user_video.mp4",
        ".captail-buffer-backup/Replay_saved.webm",
    ];
    string[] working =
    [
        ".captail-buffer/session/segment_2026-10-16_00-37-33.tmp_2.mp4",
        ".captail-buffer/session/segment_2026-10-16_00-37-33.tmp_3.mp4",
        ".captail-buffer/session/segment.tmp.mkv",
        ".captail-buffer/session/segment.mp4",
        "Game/.CAPTAIL-BUFFER/old-session/segment.tmp_2.mkv",
        "Game/Replay_pending.tmp.mp4",
        "Game/.Replay_saved.replacement.mp4",
    ];
    DateTime baseline = DateTime.UtcNow.AddMinutes(-10);
    int index = 0;
    foreach (string relative in saved.Concat(working))
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, baseline.AddSeconds(index++));
    }

    // Metadata is irrelevant to discovery; keep this fixture independent of FFmpeg.
    var library = new ReplayLibrary(new FfmpegAdapter(Path.Combine(root, "missing-ffmpeg")));
    int checks = 0;
    foreach (int limit in new[] { 1, saved.Length, int.MaxValue })
    {
        IReadOnlyList<ReplayClip> clips = await library.GetRecentAsync(root, limit);
        string[] expected = saved.Reverse().Take(limit)
            .Select(path => Path.GetFullPath(Path.Combine(root, path))).ToArray();
        if (!clips.Select(clip => clip.Path).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Internal buffer leaked into recent replays (limit={limit}): " +
                string.Join(", ", clips.Select(clip => Path.GetRelativePath(root, clip.Path))));
        checks++;
    }
    if (working.Any(relative => !File.Exists(Path.Combine(root, relative))))
        throw new InvalidOperationException("Library discovery changed working files.");
    checks++;
    if (args.Length > 0)
    {
        IReadOnlyList<ReplayClip> actual = await library.GetRecentAsync(args[0], int.MaxValue);
        if (actual.Any(clip => Path.GetRelativePath(args[0], clip.Path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains(".captail-buffer", StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Disk buffer leaked into the actual replay library.");
        }
        Console.WriteLine($"ACTUAL_LIBRARY PASS ({actual.Count} saved clips)");
        checks++;
    }
    Console.WriteLine($"REPLAY_LIBRARY_QA PASS ({checks} checks)");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("REPLAY_LIBRARY_QA FAIL: " + exception.Message);
    return 1;
}
finally
{
    Directory.Delete(root, recursive: true);
}
