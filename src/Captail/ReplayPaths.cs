using System.IO;

namespace Captail;

public static class ReplayPaths
{
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    public static string CaptureDirectory(Config config, string? gameExecutable)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.OrganizeReplaysByGame ||
            string.IsNullOrWhiteSpace(gameExecutable))
        {
            return config.OutputDirectory;
        }

        string name = Path.GetFileNameWithoutExtension(gameExecutable);
        string safeName = SanitizeFolderName(name);
        return Path.Combine(config.OutputDirectory, safeName);
    }

    public static string RouteSavedReplay(
        Config config,
        string savedPath,
        string? gameExecutable)
    {
        ArgumentNullException.ThrowIfNull(config);
        string source = Path.GetFullPath(savedPath);
        string destinationDirectory = Path.GetFullPath(
            CaptureDirectory(config, gameExecutable));
        Directory.CreateDirectory(destinationDirectory);

        string destination = Path.Combine(
            destinationDirectory,
            Path.GetFileName(source));
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return source;

        destination = AvailableDestination(destination);
        File.Move(source, destination);
        return destination;
    }

    public static string SanitizeFolderName(string? value)
    {
        string name = value?.Trim() ?? "";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim(' ', '.');
        if (name.Length > 80)
            name = name[..80].TrimEnd(' ', '.');
        if (name.Length == 0 || ReservedNames.Contains(name))
            return "Game";
        return name;
    }

    private static string AvailableDestination(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = Path.Combine(
                directory,
                $"{name}_{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            directory,
            $"{name}_{Guid.NewGuid():N}{extension}");
    }
}
