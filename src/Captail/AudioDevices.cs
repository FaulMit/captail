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
