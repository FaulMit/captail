namespace Captail;

internal sealed record AudioRoutingFormatCapabilities(
    string AudioCodec,
    string Container,
    int MaxTracks)
{
    internal static AudioRoutingFormatCapabilities For(string? audioCodec) =>
        string.Equals(audioCodec, "opus", StringComparison.OrdinalIgnoreCase)
            ? new AudioRoutingFormatCapabilities("opus", "MKV", 6)
            : new AudioRoutingFormatCapabilities("aac", "MP4", 6);
}
