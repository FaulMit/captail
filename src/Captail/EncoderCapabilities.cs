namespace Captail;

public sealed record CodecCapability(
    string Codec,
    string EncoderId,
    string Family,
    string FamilyDisplayName);

public sealed class EncoderCapabilities
{
    private readonly Dictionary<string, IReadOnlyList<CodecCapability>> _codecs;

    public string AdapterName { get; }
    public string? ProbeError { get; }

    public EncoderCapabilities(
        string adapterName,
        IEnumerable<CodecCapability> codecs,
        string? probeError = null)
    {
        AdapterName = string.IsNullOrWhiteSpace(adapterName)
            ? Localization.Text("L.Gpu.Generic")
            : adapterName;
        ProbeError = probeError;
        _codecs = codecs
            .GroupBy(capability => capability.Codec, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CodecCapability>)group.ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool Supports(string codec) =>
        _codecs.TryGetValue(codec, out IReadOnlyList<CodecCapability>? encoders) &&
        encoders.Count > 0;

    public IReadOnlyList<CodecCapability> Candidates(string codec) =>
        _codecs.TryGetValue(codec, out IReadOnlyList<CodecCapability>? encoders)
            ? encoders
            : [];

    public CodecCapability? Preferred(string codec) =>
        Candidates(codec).FirstOrDefault();

    public string? FallbackCodec()
    {
        foreach (string codec in new[] { "h264", "hevc", "av1" })
        {
            if (Supports(codec))
                return codec;
        }
        return null;
    }

    public static EncoderCapabilities Failed(string error) =>
        new(Localization.Text("L.Gpu.Unknown"), [], error);

    public static EncoderCapabilities Preview() =>
        new(
            Localization.Text("L.Gpu.Preview"),
            [
                new("h264", "preview_h264", "hw", Localization.Text("L.Gpu.HardwareEncoder")),
                new("hevc", "preview_hevc", "hw", Localization.Text("L.Gpu.HardwareEncoder")),
                new("av1", "preview_av1", "hw", Localization.Text("L.Gpu.HardwareEncoder")),
            ]);
}

internal static class EncoderCatalog
{
    private static readonly CodecCapability[] All =
    [
        new("h264", "obs_nvenc_h264_tex", "nvenc", "NVIDIA NVENC"),
        new("hevc", "obs_nvenc_hevc_tex", "nvenc", "NVIDIA NVENC"),
        new("av1", "obs_nvenc_av1_tex", "nvenc", "NVIDIA NVENC"),
        new("h264", "h264_texture_amf", "amf", "AMD AMF"),
        new("hevc", "h265_texture_amf", "amf", "AMD AMF"),
        new("av1", "av1_texture_amf", "amf", "AMD AMF"),
        new("h264", "obs_qsv11_v2", "qsv", "Intel Quick Sync"),
        new("hevc", "obs_qsv11_hevc", "qsv", "Intel Quick Sync"),
        new("av1", "obs_qsv11_av1", "qsv", "Intel Quick Sync"),
    ];

    internal static IEnumerable<CodecCapability> Available(
        ISet<string> registeredEncoderIds,
        string adapterName)
    {
        string preferredFamily = adapterName.Contains(
            "NVIDIA",
            StringComparison.OrdinalIgnoreCase)
            ? "nvenc"
            : adapterName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
              adapterName.Contains("RADEON", StringComparison.OrdinalIgnoreCase)
                ? "amf"
                : adapterName.Contains("INTEL", StringComparison.OrdinalIgnoreCase)
                    ? "qsv"
                    : "";

        return All
            .Where(capability => registeredEncoderIds.Contains(capability.EncoderId))
            .OrderBy(capability =>
                string.Equals(
                    capability.Family,
                    preferredFamily,
                    StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1);
    }

    internal static CodecCapability? Find(string encoderId) =>
        All.FirstOrDefault(capability =>
            string.Equals(
                capability.EncoderId,
                encoderId,
                StringComparison.OrdinalIgnoreCase));
}
