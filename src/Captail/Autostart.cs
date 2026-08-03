using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Captail;

/// <summary>Per-user startup through MSIX StartupTask or HKCU Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Captail";
    private const string StoreTaskId = "CaptailStartup";

    public static async Task<bool> IsEnabledAsync()
    {
        if (AppDistribution.IsMicrosoftStore)
        {
            StartupTask task = await StartupTask.GetAsync(StoreTaskId);
            return task.State is
                StartupTaskState.Enabled or
                StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(
            ReadCommand(key),
            ExpectedCommand(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasEntry()
    {
        if (AppDistribution.IsMicrosoftStore)
            return false;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return !string.IsNullOrWhiteSpace(ReadCommand(key));
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (AppDistribution.IsMicrosoftStore)
        {
            StartupTask task = await StartupTask.GetAsync(StoreTaskId);
            if (!enabled)
            {
                if (task.State is
                    StartupTaskState.Enabled or
                    StartupTaskState.EnabledByPolicy)
                {
                    task.Disable();
                }
                return;
            }

            StartupTaskState state = task.State is
                StartupTaskState.Enabled or
                StartupTaskState.EnabledByPolicy
                    ? task.State
                    : await task.RequestEnableAsync();
            if (state is not
                (StartupTaskState.Enabled or
                 StartupTaskState.EnabledByPolicy))
            {
                throw new InvalidOperationException(
                    Localization.Text("L.Error.AutostartDenied"));
            }
            return;
        }

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
