using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Captail;

public sealed class Config
{
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
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config config =
                    JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ??
                    new Config();
                config.Language = NormalizeLanguage(config.Language);
                return config;
            }
        }
        catch
        {
            // A damaged config must not prevent startup.
        }

        var defaultConfig = new Config();
        defaultConfig.Save();
        return defaultConfig;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NormalizeLanguage(string? language) =>
        string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";
}
