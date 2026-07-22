using Microsoft.Win32;

namespace Captail;

/// <summary>Per-user startup through HKCU\...\Run; administrator rights are not required.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Captail";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(
            ReadCommand(key),
            ExpectedCommand(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return !string.IsNullOrWhiteSpace(ReadCommand(key));
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(
                ValueName,
                ExpectedCommand(),
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string? ReadCommand(RegistryKey? key) =>
        key?.GetValue(
            ValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private static string ExpectedCommand()
    {
        string executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException(
                Localization.Text("L.App.ExecutablePathError"));
        return $"\"{executablePath}\" --background";
    }
}
