using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Captail;

public sealed class Config
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string Language { get; set; } = "en";
    public int BufferSeconds { get; set; } = 300;
    /// <summary>0 = duration-only limit.</summary>
    public int MaxReplaySizeMb { get; set; }
    public int FrameRate { get; set; } = 60;
    /// <summary>0 = adaptive bitrate based on codec and load.</summary>
    public int BitrateMbps { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
    public string ToggleReplayHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool ReplayEnabled { get; set; } = true;

    /// <summary>"av1", "hevc", or "h264". OBS selects an available encoder for the requested format.</summary>
    public string Codec { get; set; } = "h264";

    public int MonitorIndex { get; set; }
    /// <summary>"source", "720p", "1080p", "1440p", or "2160p".</summary>
    public string RecordingResolution { get; set; } = "source";
    /// <summary>"desktop" or "game".</summary>
    public string CaptureSource { get; set; } = "desktop";
    /// <summary>Full path to the selected game executable; PID is resolved when the pipeline starts.</summary>
    public string GameExecutablePath { get; set; } = "";

    public bool CaptureSystemAudio { get; set; } = true;
    public int SystemAudioVolume { get; set; } = 100;
    /// <summary>Render-device ID used for loopback; empty selects the Windows default device.</summary>
    public string SystemAudioDeviceId { get; set; } = "";
    public bool CaptureMicrophone { get; set; }
    public int MicrophoneVolume { get; set; } = 100;
    public int MicrophoneBoostDb { get; set; }
    /// <summary>Microphone device ID; empty selects the Windows default microphone.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>"aac" for fragmented MP4 or "opus" for MKV.</summary>
    public string AudioCodec { get; set; } = "aac";
    /// <summary>
    /// true stores system audio and microphone on separate tracks;
    /// false mixes both sources into one track.
    /// </summary>
    public bool SeparateAudioTracks { get; set; }

    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captail");
    [JsonIgnore]
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captail", "config.json");
    public static Config Load()
    {
        if (TryLoad(ConfigPath, out Config? config) && config is not null)
            return config;

        string backupPath = ConfigPath + ".bak";
        if (TryLoad(backupPath, out config) && config is not null)
        {
            try
            {
                config.Save();
            }
            catch (Exception exception)
            {
                Log.Write($"Config backup restore failed: {exception.Message}");
            }
            return config;
        }

        var defaultConfig = new Config();
        defaultConfig.Save();
        return defaultConfig;
    }

    private static bool TryLoad(string path, out Config? config)
    {
        config = null;
        if (!File.Exists(path))
            return false;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
            config?.Normalize();
            return config is not null;
        }
        catch (Exception exception)
        {
            Log.Write($"Config load failed ({Path.GetFileName(path)}): {exception.Message}");
            return false;
        }
    }

    public void Save()
    {
        Normalize();
        string directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $"config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        string backupPath = ConfigPath + ".bak";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(this, SerializerOptions));
            if (File.Exists(ConfigPath))
                File.Replace(temporaryPath, ConfigPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, ConfigPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Config Clone()
    {
        var clone = new Config();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(Config source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Language = source.Language;
        BufferSeconds = source.BufferSeconds;
        MaxReplaySizeMb = source.MaxReplaySizeMb;
        FrameRate = source.FrameRate;
        BitrateMbps = source.BitrateMbps;
        Hotkey = source.Hotkey;
        ToggleReplayHotkey = source.ToggleReplayHotkey;
        ReplayEnabled = source.ReplayEnabled;
        Codec = source.Codec;
        MonitorIndex = source.MonitorIndex;
        RecordingResolution = source.RecordingResolution;
        CaptureSource = source.CaptureSource;
        GameExecutablePath = source.GameExecutablePath;
        CaptureSystemAudio = source.CaptureSystemAudio;
        SystemAudioVolume = source.SystemAudioVolume;
        SystemAudioDeviceId = source.SystemAudioDeviceId;
        CaptureMicrophone = source.CaptureMicrophone;
        MicrophoneVolume = source.MicrophoneVolume;
        MicrophoneBoostDb = source.MicrophoneBoostDb;
        MicrophoneDeviceId = source.MicrophoneDeviceId;
        AudioBitrateKbps = source.AudioBitrateKbps;
        AudioCodec = source.AudioCodec;
        SeparateAudioTracks = source.SeparateAudioTracks;
        OutputDirectory = source.OutputDirectory;
        Normalize();
    }

    public bool PipelineEquals(Config other) =>
        BufferSeconds == other.BufferSeconds &&
        MaxReplaySizeMb == other.MaxReplaySizeMb &&
        FrameRate == other.FrameRate &&
        BitrateMbps == other.BitrateMbps &&
        string.Equals(Codec, other.Codec, StringComparison.Ordinal) &&
        MonitorIndex == other.MonitorIndex &&
        string.Equals(RecordingResolution, other.RecordingResolution, StringComparison.Ordinal) &&
        string.Equals(CaptureSource, other.CaptureSource, StringComparison.Ordinal) &&
        string.Equals(GameExecutablePath, other.GameExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        CaptureSystemAudio == other.CaptureSystemAudio &&
        SystemAudioVolume == other.SystemAudioVolume &&
        string.Equals(SystemAudioDeviceId, other.SystemAudioDeviceId, StringComparison.Ordinal) &&
        CaptureMicrophone == other.CaptureMicrophone &&
        MicrophoneVolume == other.MicrophoneVolume &&
        MicrophoneBoostDb == other.MicrophoneBoostDb &&
        string.Equals(MicrophoneDeviceId, other.MicrophoneDeviceId, StringComparison.Ordinal) &&
        AudioBitrateKbps == other.AudioBitrateKbps &&
        string.Equals(AudioCodec, other.AudioCodec, StringComparison.Ordinal) &&
        SeparateAudioTracks == other.SeparateAudioTracks &&
        string.Equals(OutputDirectory, other.OutputDirectory, StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        Language = NormalizeLanguage(Language);
        BufferSeconds = AllowedValue(BufferSeconds, [15, 30, 60, 120, 300, 600, 900], 300);
        MaxReplaySizeMb = AllowedValue(MaxReplaySizeMb, [0, 250, 500, 1000, 2000, 5000, 10000], 0);
        FrameRate = AllowedValue(FrameRate, [30, 60, 120, 144, 240], 60);
        BitrateMbps = AllowedValue(BitrateMbps, [0, 10, 20, 50, 80], 0);
        Hotkey = NormalizeHotkey(Hotkey, "Ctrl+Shift+F10");
        ToggleReplayHotkey = NormalizeHotkey(ToggleReplayHotkey, "Ctrl+Shift+F9");
        if (!HotkeyManager.IsValid(Hotkey))
            Hotkey = "Ctrl+Shift+F10";
        if (!HotkeyManager.IsValid(ToggleReplayHotkey) ||
            !HotkeyManager.AreDistinct(Hotkey, ToggleReplayHotkey))
        {
            ToggleReplayHotkey = "Ctrl+Shift+F9";
        }
        Codec = AllowedText(Codec, ["h264", "hevc", "av1"], "h264");
        MonitorIndex = Math.Clamp(MonitorIndex, 0, 63);
        RecordingResolution = AllowedText(
            RecordingResolution,
            ["source", "720p", "1080p", "1440p", "2160p"],
            "source");
        CaptureSource = AllowedText(CaptureSource, ["desktop", "game"], "desktop");
        GameExecutablePath = NormalizePath(GameExecutablePath, allowEmpty: true);
        SystemAudioVolume = Math.Clamp(SystemAudioVolume, 0, 100);
        SystemAudioDeviceId = NormalizeIdentifier(SystemAudioDeviceId);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0, 100);
        MicrophoneBoostDb = Math.Clamp(MicrophoneBoostDb, 0, 20);
        MicrophoneDeviceId = NormalizeIdentifier(MicrophoneDeviceId);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 512);
        AudioCodec = AllowedText(AudioCodec, ["aac", "opus"], "aac");
        OutputDirectory = NormalizePath(OutputDirectory, allowEmpty: false);
    }

    private static int AllowedValue(int value, int[] allowed, int fallback) =>
        allowed.Contains(value) ? value : fallback;

    private static string AllowedText(
        string? value,
        string[] allowed,
        string fallback)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        return allowed.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : fallback;
    }

    private static string NormalizeHotkey(string? value, string fallback)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= 64 ? normalized : fallback;
    }

    private static string NormalizeIdentifier(string? value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Length <= 1024 ? normalized : "";
    }

    private static string NormalizePath(string? value, bool allowEmpty)
    {
        string fallback = allowEmpty
            ? ""
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "Captail");
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0)
            return fallback;
        if (normalized.Length > 1024 || normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return fallback;
        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return fallback;
        }
    }

    private static string NormalizeLanguage(string? language) =>
        string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";
}
