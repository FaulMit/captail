using Microsoft.Win32;

namespace InstantReplay;

/// <summary>Автозапуск через HKCU\...\Run — без прав администратора.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Captail";
    private const string LegacyValueName = "InstantReplay";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return HasCommand(key, ValueName) || HasCommand(key, LegacyValueName);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string executablePath = Environment.ProcessPath ??
                throw new InvalidOperationException(
                    Localization.Text("L.App.ExecutablePathError"));
            key.SetValue(
                ValueName,
                $"\"{executablePath}\" --background",
                RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }

    private static bool HasCommand(RegistryKey? key, string valueName) =>
        key?.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) is string command &&
        !string.IsNullOrWhiteSpace(command);
}
