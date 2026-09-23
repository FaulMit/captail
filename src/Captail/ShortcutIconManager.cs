using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace Captail;

internal static class ShortcutIconManager
{
    private const uint ShellChangeNotifyUpdateItem = 0x00002000;
    private const uint ShellChangeNotifyPathW = 0x0005;

    internal static void Apply(string? accentName)
    {
        if (AppDistribution.IsMicrosoftStore)
            return;

        try
        {
            string iconPath = ExtractIcon(accentName);
            foreach (string shortcutPath in FindShortcutPaths())
                UpdateShortcut(shortcutPath, iconPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            COMException or TargetInvocationException)
        {
            Log.Write($"Could not update shortcut icon: {exception.Message}");
        }
    }

    private static string ExtractIcon(string? accentName)
    {
        string assetName = ThemeManager.IconAssetName(accentName);
        string directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Captail",
            "icons");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, assetName);

        using Stream source = Application.GetResourceStream(
            new Uri($"Assets/{assetName}", UriKind.Relative)).Stream;
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        byte[] iconBytes = memory.ToArray();
        if (!File.Exists(destination) ||
            !File.ReadAllBytes(destination).AsSpan().SequenceEqual(iconBytes))
        {
            File.WriteAllBytes(destination, iconBytes);
        }

        return destination;
    }

    private static IEnumerable<string> FindShortcutPaths()
    {
        var directories = new[]
        {
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "Captail"),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "Internet Explorer",
                "Quick Launch",
                "User Pinned",
                "TaskBar"),
        };

        foreach (string directory in directories.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (string path in Directory.EnumerateFiles(
                         directory,
                         "*.lnk",
                         SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static void UpdateShortcut(string shortcutPath, string iconPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shell,
                new object[] { shortcutPath },
                CultureInfo.InvariantCulture);
            if (shortcut is null)
                return;

            Type shortcutType = shortcut.GetType();
            string? targetPath = shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                shortcut,
                args: null,
                CultureInfo.InvariantCulture) as string;
            if (!TargetsCurrentExecutable(targetPath))
                return;

            string desiredIcon = $"{iconPath},0";
            string? currentIcon = shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.GetProperty,
                binder: null,
                shortcut,
                args: null,
                CultureInfo.InvariantCulture) as string;
            if (string.Equals(
                    currentIcon,
                    desiredIcon,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            shortcutType.InvokeMember(
                "IconLocation",
                BindingFlags.SetProperty,
                binder: null,
                shortcut,
                new object[] { desiredIcon },
                CultureInfo.InvariantCulture);
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                shortcut,
                args: null,
                CultureInfo.InvariantCulture);
            SHChangeNotify(
                ShellChangeNotifyUpdateItem,
                ShellChangeNotifyPathW,
                shortcutPath,
                IntPtr.Zero);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static bool TargetsCurrentExecutable(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) ||
            string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(targetPath),
                Path.GetFullPath(Environment.ProcessPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        string item1,
        IntPtr item2);
}
