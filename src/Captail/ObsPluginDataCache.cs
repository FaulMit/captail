using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Captail;

internal static class ObsPluginDataCache
{
    private const string CacheDirectoryName = "obs-plugin-cache";

    public static string Prepare(string packagedDataRoot)
    {
        string sourceRoot = Path.Combine(packagedDataRoot, "obs-plugins");
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);

        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captail",
            CacheDirectoryName);
        Directory.CreateDirectory(cacheRoot);

        string cacheKey = ComputeTreeHash(sourceRoot)[..20].ToLowerInvariant();
        string destinationRoot = Path.Combine(cacheRoot, cacheKey);
        string destinationPlugins = Path.Combine(destinationRoot, "obs-plugins");
        if (!IsExactCopy(sourceRoot, destinationPlugins))
        {
            if (Directory.Exists(destinationRoot) && !TryDelete(destinationRoot))
            {
                destinationRoot = Path.Combine(
                    cacheRoot,
                    $"{cacheKey}-{Guid.NewGuid():N}");
                destinationPlugins = Path.Combine(destinationRoot, "obs-plugins");
            }

            CopyAtomically(sourceRoot, destinationRoot, destinationPlugins);
        }

        CleanupStaleCaches(cacheRoot, destinationRoot);
        return destinationRoot;
    }

    private static void CopyAtomically(
        string sourceRoot,
        string destinationRoot,
        string destinationPlugins)
    {
        string stagingRoot = Path.Combine(
            Path.GetDirectoryName(destinationRoot)!,
            $".staging-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string stagingPlugins = Path.Combine(stagingRoot, "obs-plugins");
        try
        {
            CopyDirectory(sourceRoot, stagingPlugins);
            try
            {
                Directory.Move(stagingRoot, destinationRoot);
            }
            catch (IOException) when (IsExactCopy(sourceRoot, destinationPlugins))
            {
                TryDelete(stagingRoot);
            }
        }
        finally
        {
            TryDelete(stagingRoot);
        }

        if (!IsExactCopy(sourceRoot, destinationPlugins))
        {
            throw new IOException(
                "OBS plugin data cache could not be prepared safely.");
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (string sourceDirectory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceDirectory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceFile);
            string destinationFile = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
        }
    }

    private static bool IsExactCopy(string sourceRoot, string destinationRoot)
    {
        try
        {
            if (!Directory.Exists(destinationRoot))
                return false;

            string[] sourceFiles = RelativeFiles(sourceRoot);
            string[] destinationFiles = RelativeFiles(destinationRoot);
            if (!sourceFiles.SequenceEqual(
                    destinationFiles,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (string relative in sourceFiles)
            {
                string source = Path.Combine(sourceRoot, relative);
                string destination = Path.Combine(destinationRoot, relative);
                var sourceInfo = new FileInfo(source);
                var destinationInfo = new FileInfo(destination);
                if (sourceInfo.Length != destinationInfo.Length ||
                    !FileHashesMatch(source, destination))
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool FileHashesMatch(string first, string second)
    {
        byte[] firstHash = HashFile(first);
        byte[] secondHash = HashFile(second);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static string ComputeTreeHash(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string relative in RelativeFiles(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative.Replace('\\', '/') + "\0"));
            hash.AppendData(HashFile(Path.Combine(root, relative)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private static void CleanupStaleCaches(string cacheRoot, string activeRoot)
    {
        foreach (string directory in Directory.EnumerateDirectories(cacheRoot))
        {
            if (string.Equals(
                    directory,
                    activeRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDelete(directory);
        }
    }

    private static bool TryDelete(string directory)
    {
        if (!Directory.Exists(directory))
            return true;

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
