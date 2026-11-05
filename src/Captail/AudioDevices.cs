using NAudio.CoreAudioApi;

namespace Captail;

internal static class AudioDevices
{
    internal static IReadOnlyList<(string Id, string Name)> ListRenderDevices() =>
        List(DataFlow.Render);

    internal static IReadOnlyList<(string Id, string Name)> ListCaptureDevices() =>
        List(DataFlow.Capture);

    private static IReadOnlyList<(string Id, string Name)> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => (device.ID, device.FriendlyName))
            .ToList();
    }
}

internal sealed class AudioPeakReader : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;
    private string? _renderId;
    private string? _captureId;

    internal float? ReadRenderPeak(string deviceId) =>
        ReadPeak(ref _renderDevice, ref _renderId, DataFlow.Render, deviceId);

    internal float? ReadCapturePeak(string deviceId) =>
        ReadPeak(ref _captureDevice, ref _captureId, DataFlow.Capture, deviceId);

    private float? ReadPeak(ref MMDevice? device, ref string? currentId,
        DataFlow flow, string deviceId)
    {
        if (device is null || !string.Equals(currentId, deviceId,
                StringComparison.OrdinalIgnoreCase))
        {
            device?.Dispose();
            device = null;
            currentId = deviceId;
            try
            {
                device = string.IsNullOrEmpty(deviceId)
                    ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                    : _enumerator.GetDevice(deviceId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        try
        {
            return Math.Clamp(device.AudioMeterInformation.MasterPeakValue, 0f, 1f);
        }
        catch (Exception)
        {
            device.Dispose();
            device = null;
            return null;
        }
    }

    public void Dispose()
    {
        _renderDevice?.Dispose();
        _captureDevice?.Dispose();
        _enumerator.Dispose();
    }
}
