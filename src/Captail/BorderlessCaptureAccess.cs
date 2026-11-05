using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Captail;

internal static class BorderlessCaptureAccess
{
    internal readonly record struct Result(
        bool Supported,
        AppCapabilityAccessStatus? Status,
        string? Error)
    {
        internal bool Ready => Supported && Status == AppCapabilityAccessStatus.Allowed;
    }

    internal static bool IsSupported() =>
        ApiInformation.IsPropertyPresent(
            "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired") &&
        ApiInformation.IsMethodPresent(
            "Windows.Graphics.Capture.GraphicsCaptureAccess", "RequestAccessAsync");

    internal static Task<Result> ProbeAsync() => ProbeAsync(
        IsSupported,
        () => GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask(),
        TimeSpan.FromSeconds(15));

    internal static async Task<Result> ProbeAsync(
        Func<bool> supportsBorderless,
        Func<Task<AppCapabilityAccessStatus>> requestAccess,
        TimeSpan timeout)
    {
        bool supported = false;
        try
        {
            supported = supportsBorderless();
            if (!supported)
                return new(false, null, null);

            AppCapabilityAccessStatus status = await requestAccess().WaitAsync(timeout);
            return new(true, status, null);
        }
        catch (Exception exception)
        {
            return new(supported, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}
