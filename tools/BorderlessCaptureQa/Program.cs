using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Captail;
using Windows.Security.Authorization.AppCapabilityAccess;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    [STAThread]
    private static int Main(string[] args)
    {
        if (!args.Contains("--probe"))
        {
            try { SelfTestAsync().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                BorderlessCaptureAccess.Result result = await BorderlessCaptureAccess.ProbeAsync();
                uint length = 0;
                string? package = null;
                if (GetCurrentPackageFullName(ref length, null) == 122)
                {
                    var buffer = new StringBuilder((int)length);
                    if (GetCurrentPackageFullName(ref length, buffer) == 0)
                        package = buffer.ToString();
                }
                string json = JsonSerializer.Serialize(new
                {
                    Windows = Environment.OSVersion.Version.ToString(),
                    PackageIdentity = package,
                    result.Supported,
                    Status = result.Status?.ToString(),
                    result.Ready,
                    result.Error,
                    TimestampUtc = DateTime.UtcNow,
                }, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                int outputIndex = Array.IndexOf(args, "--output");
                if (outputIndex >= 0)
                    File.WriteAllText(args[outputIndex + 1], json);
                app.Shutdown(result.Error is null ? 0 : 1);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task SelfTestAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
            Console.WriteLine("PASS: " + message);
        }
        TimeSpan timeout = TimeSpan.FromSeconds(1);
        int requests = 0;
        var unsupported = await BorderlessCaptureAccess.ProbeAsync(() => false, () =>
        {
            requests++;
            return Task.FromResult(AppCapabilityAccessStatus.Allowed);
        }, timeout);
        Check(!unsupported.Supported && !unsupported.Ready && unsupported.Error is null && requests == 0,
            "Unsupported APIs never request permission or enable WGC");
        foreach (var status in Enum.GetValues<AppCapabilityAccessStatus>().Append((AppCapabilityAccessStatus)999))
        {
            var result = await BorderlessCaptureAccess.ProbeAsync(() => true, () => Task.FromResult(status), timeout);
            Check(result.Supported && result.Status == status && result.Ready == (status == AppCapabilityAccessStatus.Allowed),
                $"Access status {status} enables WGC only for Allowed");
        }
        var supportError = await BorderlessCaptureAccess.ProbeAsync(
            () => throw new InvalidOperationException("metadata unavailable"),
            () => throw new InvalidOperationException("must not request"), timeout);
        Check(!supportError.Ready && supportError.Error!.Contains("metadata unavailable"),
            "Metadata failure disables WGC with a diagnostic reason");
        var requestError = await BorderlessCaptureAccess.ProbeAsync(() => true,
            () => Task.FromException<AppCapabilityAccessStatus>(new COMException("request rejected")), timeout);
        Check(!requestError.Ready && requestError.Error!.Contains("request rejected"),
            "Permission request failure disables WGC with a diagnostic reason");
        var neverCompletes = new TaskCompletionSource<AppCapabilityAccessStatus>();
        var timedOut = await BorderlessCaptureAccess.ProbeAsync(() => true, () => neverCompletes.Task,
            TimeSpan.FromMilliseconds(20));
        Check(!timedOut.Ready && timedOut.Error!.Contains(nameof(TimeoutException)),
            "Permission request timeout disables WGC");
        Console.WriteLine($"BORDERLESS_CAPTURE_QA PASS: {assertions} assertions");
    }
}
