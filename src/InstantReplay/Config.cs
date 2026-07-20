using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantReplay;

public sealed class Config
{
    public string Language { get; set; } = "en";
    public int BufferSeconds { get; set; } = 300;
    /// <summary>0 = ограничение только по длительности.</summary>
    public int MaxReplaySizeMb { get; set; }
    public int FrameRate { get; set; } = 60;
    /// <summary>0 = адаптивный битрейт по кодеку и нагрузке.</summary>
    public int BitrateMbps { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
    public string ToggleReplayHotkey { get; set; } = "Ctrl+Shift+F9";
    public bool ReplayEnabled { get; set; } = true;

    /// <summary>"av1", "hevc" или "h264". OBS выбирает доступный encoder нужного формата.</summary>
    public string Codec { get; set; } = "h264";

    public int MonitorIndex { get; set; }
    /// <summary>"source", "720p", "1080p", "1440p" или "2160p".</summary>
    public string RecordingResolution { get; set; } = "source";
    /// <summary>"desktop" или "game".</summary>
    public string CaptureSource { get; set; } = "desktop";
    /// <summary>Полный путь к EXE выбранной игры; PID определяется при запуске pipeline.</summary>
    public string GameExecutablePath { get; set; } = "";

    public bool CaptureSystemAudio { get; set; } = true;
    public int SystemAudioVolume { get; set; } = 100;
    /// <summary>ID render-устройства для loopback; пусто — дефолтное устройство Windows.</summary>
    public string SystemAudioDeviceId { get; set; } = "";
    public bool CaptureMicrophone { get; set; }
    public int MicrophoneVolume { get; set; } = 100;
    public int MicrophoneBoostDb { get; set; }
    /// <summary>ID устройства микрофона; пусто — дефолтный микрофон Windows.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>"aac" для fragmented MP4 или "opus" для MKV.</summary>
    public string AudioCodec { get; set; } = "aac";
    /// <summary>
    /// true — звук системы и микрофон сохраняются отдельными аудиодорожками;
    /// false — источники сводятся в одну дорожку.
    /// </summary>
    public bool SeparateAudioTracks { get; set; }

    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captail");
    [JsonIgnore]
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Captail", "config.json");
    [JsonIgnore]
    private static string LegacyConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InstantReplay", "config.json");

    public static Config Load()
    {
        foreach (string path in new[] { ConfigPath, LegacyConfigPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                Config config =
                    JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ??
                    new Config();
                config.Language = NormalizeLanguage(config.Language);
                if (!string.Equals(path, ConfigPath, StringComparison.OrdinalIgnoreCase))
                    config.Save();
                return config;
            }
            catch
            {
                // Пробуем следующий путь; повреждённый конфиг не роняет запуск.
            }
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
