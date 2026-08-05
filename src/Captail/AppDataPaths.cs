using System.IO;
using Windows.Storage;

namespace Captail;

internal static class AppDataPaths
{
    private static readonly string LegacyLocalRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Captail");
    private static readonly string LegacyRoamingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Captail");

    private static string StateRoot => AppDistribution.IsMicrosoftStore
        ? ApplicationData.Current.LocalFolder.Path
        : LegacyRoamingRoot;

    private static string CacheRoot => AppDistribution.IsMicrosoftStore
        ? ApplicationData.Current.LocalCacheFolder.Path
        : LegacyLocalRoot;

    internal static string ConfigFile => Path.Combine(StateRoot, "config.json");
    internal static string LogFile => Path.Combine(CacheRoot, "log.txt");
    internal static string ObsConfigDirectory => Path.Combine(CacheRoot, "obs");
    internal static string ObsPluginCacheDirectory =>
        Path.Combine(CacheRoot, "obs-plugin-cache");
    internal static string ThumbnailDirectory =>
        Path.Combine(CacheRoot, "thumbnails");

    internal static void PrepareStoreData()
    {
        if (!AppDistribution.IsMicrosoftStore)
            return;

        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(CacheRoot);
        MigrateLegacyFile(
            Path.Combine(LegacyRoamingRoot, "config.json"),
            ConfigFile);
        MigrateLegacyFile(
            Path.Combine(LegacyRoamingRoot, "config.json.bak"),
            ConfigFile + ".bak");

        TryDeleteLegacyFile(Path.Combine(LegacyLocalRoot, "log.txt"));
        foreach (string directory in new[]
                 {
                     "obs",
                     "obs-plugin-cache",
                     "thumbnails",
                 })
        {
            TryDeleteLegacyDirectory(Path.Combine(LegacyLocalRoot, directory));
        }

        TryDeleteIfEmpty(LegacyRoamingRoot);
        TryDeleteIfEmpty(LegacyLocalRoot);
    }

    private static void MigrateLegacyFile(string source, string destination)
    {
        if (!File.Exists(source))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
                File.Move(source, destination);
            else
                File.Delete(source);
        }
        catch (IOException)
        {
            // A previous process can briefly retain its settings file.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep legacy data intact when migration is not permitted.
        }
    }

    private static void TryDeleteLegacyFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteLegacyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
