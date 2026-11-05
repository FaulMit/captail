using System.Text.Json;

namespace Captail;

public sealed class RecordingPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public RecordingSettingsSnapshot Settings { get; set; } = new();

    public RecordingPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Settings = (Settings ?? new()).Clone(),
    };

    internal static List<RecordingPreset> NormalizeLibrary(IEnumerable<RecordingPreset>? presets)
    {
        var result = new List<RecordingPreset>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecordingPreset? preset in presets ?? [])
        {
            if (preset is null || !Guid.TryParseExact(preset.Id, "N", out Guid id))
                continue;
            string name = preset.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60 ||
                !ids.Add(id.ToString("N")) || !names.Add(name))
                continue;
            var normalized = new Config();
            (preset.Settings ?? new()).ApplyTo(normalized);
            normalized.Normalize();
            result.Add(new RecordingPreset
            {
                Id = id.ToString("N"),
                Name = name,
                Settings = RecordingSettingsSnapshot.Capture(normalized),
            });
            if (result.Count == 100)
                break;
        }
        return result;
    }

    internal static bool LibraryEquals(IEnumerable<RecordingPreset> left, IEnumerable<RecordingPreset> right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

}

/// <summary>Only recording preferences. Never captures lifecycle, hotkeys, folders or appearance.</summary>
public sealed class RecordingSettingsSnapshot
{
    public bool AdvancedSettingsEnabled { get; set; }
    public EncoderTuningOptions EncoderTuning { get; set; } = new();
    public int BufferSeconds { get; set; } = 300;
    public int MaxReplaySizeMb { get; set; }
    public bool DiskReplayBufferEnabled { get; set; }
    public int FrameRate { get; set; } = 60;
    public int BitrateMbps { get; set; }
    public string Codec { get; set; } = "h264";
    public string RecordingResolution { get; set; } = "source";
    public string CaptureSource { get; set; } = "desktop";
    public int MonitorIndex { get; set; }
    public bool CaptureSystemAudio { get; set; } = true;
    public int SystemAudioVolume { get; set; } = 100;
    public string SystemAudioDeviceId { get; set; } = "";
    public bool CaptureMicrophone { get; set; }
    public int MicrophoneVolume { get; set; } = 100;
    public int MicrophoneBoostDb { get; set; }
    public string MicrophoneDeviceId { get; set; } = "";
    public int AudioBitrateKbps { get; set; } = 192;
    public string AudioCodec { get; set; } = "aac";
    public bool SeparateAudioTracks { get; set; }
    public string AudioRoutingMode { get; set; } = "simple";
    public List<ProcessAudioRoute> ProcessAudioRoutes { get; set; } = [];
    public int AdvancedMicrophoneTrack { get; set; } = 1;

    public static RecordingSettingsSnapshot Capture(Config config) => new()
    {
        AdvancedSettingsEnabled = config.AdvancedSettingsEnabled,
        EncoderTuning = config.EncoderTuning with { },
        BufferSeconds = config.BufferSeconds,
        MaxReplaySizeMb = config.MaxReplaySizeMb,
        DiskReplayBufferEnabled = config.DiskReplayBufferEnabled,
        FrameRate = config.FrameRate,
        BitrateMbps = config.BitrateMbps,
        Codec = config.Codec,
        RecordingResolution = config.RecordingResolution,
        CaptureSource = config.CaptureSource,
        MonitorIndex = config.MonitorIndex,
        CaptureSystemAudio = config.CaptureSystemAudio,
        SystemAudioVolume = config.SystemAudioVolume,
        SystemAudioDeviceId = config.SystemAudioDeviceId,
        CaptureMicrophone = config.CaptureMicrophone,
        MicrophoneVolume = config.MicrophoneVolume,
        MicrophoneBoostDb = config.MicrophoneBoostDb,
        MicrophoneDeviceId = config.MicrophoneDeviceId,
        AudioBitrateKbps = config.AudioBitrateKbps,
        AudioCodec = config.AudioCodec,
        SeparateAudioTracks = config.SeparateAudioTracks,
        AudioRoutingMode = config.AudioRoutingMode,
        ProcessAudioRoutes = CloneRoutes(config.ProcessAudioRoutes),
        AdvancedMicrophoneTrack = config.AdvancedMicrophoneTrack,
    };

    public void ApplyTo(Config config)
    {
        config.AdvancedSettingsEnabled = AdvancedSettingsEnabled;
        config.EncoderTuning = (EncoderTuning ?? new()) with { };
        config.BufferSeconds = BufferSeconds;
        config.MaxReplaySizeMb = MaxReplaySizeMb;
        config.DiskReplayBufferEnabled = DiskReplayBufferEnabled;
        config.FrameRate = FrameRate;
        config.BitrateMbps = BitrateMbps;
        config.Codec = Codec;
        config.RecordingResolution = RecordingResolution;
        config.CaptureSource = CaptureSource;
        config.MonitorIndex = MonitorIndex;
        config.CaptureSystemAudio = CaptureSystemAudio;
        config.SystemAudioVolume = SystemAudioVolume;
        config.SystemAudioDeviceId = SystemAudioDeviceId;
        config.CaptureMicrophone = CaptureMicrophone;
        config.MicrophoneVolume = MicrophoneVolume;
        config.MicrophoneBoostDb = MicrophoneBoostDb;
        config.MicrophoneDeviceId = MicrophoneDeviceId;
        config.AudioBitrateKbps = AudioBitrateKbps;
        config.AudioCodec = AudioCodec;
        config.SeparateAudioTracks = SeparateAudioTracks;
        config.AudioRoutingMode = AudioRoutingMode;
        config.ProcessAudioRoutes = CloneRoutes(ProcessAudioRoutes);
        config.AdvancedMicrophoneTrack = AdvancedMicrophoneTrack;
    }

    public RecordingSettingsSnapshot Clone()
    {
        var config = new Config();
        ApplyTo(config);
        return Capture(config);
    }

    internal bool Matches(Config config) =>
        string.Equals(JsonSerializer.Serialize(this), JsonSerializer.Serialize(Capture(config)), StringComparison.Ordinal);

    private static List<ProcessAudioRoute> CloneRoutes(IEnumerable<ProcessAudioRoute>? routes) =>
        (routes ?? []).Where(route => route is not null).Select(route => new ProcessAudioRoute
        {
            Executable = route.Executable, Track = route.Track, Enabled = route.Enabled,
        }).ToList();
}
