namespace Captail;

public sealed record EncoderTuningOptions
{
    public string RateControl { get; set; } = "cbr";
    public int ConstantQuality { get; set; } = 22;
    public string NvencPreset { get; set; } = "auto";
    public string AmfPreset { get; set; } = "auto";
    public string QsvTargetUsage { get; set; } = "auto";
    /// <summary>0 preserves Captail's automatic keyframe interval.</summary>
    public int KeyframeIntervalSeconds { get; set; }
    /// <summary>-1 preserves the automatic, codec-safe B-frame count.</summary>
    public int BFrames { get; set; } = -1;
    public string NvencMultipass { get; set; } = "disabled";
    public bool NvencLookAhead { get; set; }
    public string NvencAdaptiveQuantization { get; set; } = "auto";

    public void Normalize()
    {
        RateControl = Allowed(RateControl, ["cbr", "cqp"], "cbr");
        ConstantQuality = Math.Clamp(ConstantQuality, 1, 51);
        NvencPreset = Allowed(NvencPreset, ["auto", "p1", "p2", "p3", "p4", "p5", "p6", "p7"], "auto");
        AmfPreset = Allowed(AmfPreset, ["auto", "speed", "balanced", "quality"], "auto");
        QsvTargetUsage = Allowed(QsvTargetUsage, ["auto", "tu1", "tu2", "tu3", "tu4", "tu5", "tu6", "tu7"], "auto");
        KeyframeIntervalSeconds = Math.Clamp(KeyframeIntervalSeconds, 0, 10);
        BFrames = Math.Clamp(BFrames, -1, 4);
        NvencMultipass = Allowed(NvencMultipass, ["disabled", "qres", "fullres"], "disabled");
        NvencAdaptiveQuantization = Allowed(NvencAdaptiveQuantization, ["auto", "on", "off"], "auto");
    }

    private static string Allowed(string? value, string[] choices, string fallback)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        return choices.Contains(normalized, StringComparer.Ordinal) ? normalized : fallback;
    }

    internal int EffectiveKeyframeInterval(int replaySizeLimitMb, bool diskReplay = false) =>
        replaySizeLimitMb > 0 ? 1 : diskReplay ? 2 : KeyframeIntervalSeconds > 0 ? KeyframeIntervalSeconds : 2;

    internal int EffectiveBFrames(string family, string codec, int automaticCount) =>
        family == "nvenc" && codec == "hevc"
            ? 0
            : BFrames < 0 ? automaticCount : BFrames;
}
