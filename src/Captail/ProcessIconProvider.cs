using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Captail;

internal static class ProcessIconProvider
{
    private const int MaxCachedIcons = 512;
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static Task<ImageSource?> GetAsync(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return Task.FromResult<ImageSource?>(null);
        }

        Lazy<Task<ImageSource?>> loader = Cache.GetOrAdd(
            normalizedPath,
            path => new Lazy<Task<ImageSource?>>(
                () => Task.Run(() => Load(path)),
                LazyThreadSafetyMode.ExecutionAndPublication));
        TrimCache(normalizedPath);
        return loader.Value;
    }

    private static ImageSource? Load(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath))
                return null;

            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
                return null;

            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static void TrimCache(string currentPath)
    {
        if (Cache.Count <= MaxCachedIcons)
            return;

        foreach ((string path, Lazy<Task<ImageSource?>> loader) in Cache)
        {
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) ||
                !loader.IsValueCreated ||
                !loader.Value.IsCompleted)
            {
                continue;
            }
            Cache.TryRemove(path, out _);
            if (Cache.Count <= MaxCachedIcons)
                break;
        }
    }
}
