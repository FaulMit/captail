using System.Diagnostics;

namespace Captail;

internal static class ExternalLinkLauncher
{
    internal static async Task OpenAsync(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("External link must be an HTTP(S) URL.", nameof(value));
        }

        // ShellExecute is reliable for both unpackaged and MSIX WPF processes;
        // Process.Start can return successfully while silently doing nothing in
        // a packaged process.
        try
        {
            nint result = ShellExecuteW(
                0,
                "open",
                uri.AbsoluteUri,
                null,
                null,
                ShowNormal);
            if (result.ToInt64() > 32)
            {
                Log.Write($"External link opened through ShellExecute: {uri.AbsoluteUri}");
                return;
            }
            Log.Write($"ShellExecute returned {result}: {uri.AbsoluteUri}");
        }
        catch (Exception exception)
        {
            Log.Write($"Shell link launch failed: {exception.Message}");
        }

        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                Log.Write($"External link opened: {uri.AbsoluteUri}");
                return;
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Windows URI launcher failed: {exception.Message}");
        }

        // LaunchUriAsync can report false in unpackaged builds or when the
        // default-browser association is unavailable. Let Explorer resolve the
        // URL as final fallback.
        var fallback = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        fallback.ArgumentList.Add(uri.AbsoluteUri);
        if (Process.Start(fallback) is not null)
        {
            Log.Write($"External link opened through Explorer: {uri.AbsoluteUri}");
            return;
        }

        throw new InvalidOperationException("Windows could not open the default browser.");
    }

    private const int ShowNormal = 1;

    [System.Runtime.InteropServices.DllImport(
        "shell32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern nint ShellExecuteW(
        nint window,
        string operation,
        string file,
        string? parameters,
        string? directory,
        int showCommand);
}
