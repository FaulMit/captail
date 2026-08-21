using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Captail;

internal static class BugReportInfo
{
    internal static string BuildUrl(
        Config config,
        EncoderCapabilities capabilities)
    {
        var fields = new Dictionary<string, string>
        {
            ["template"] = "bug_report.yml",
            ["version"] = UpdateService.CurrentVersionText,
            ["package"] = PackageName(),
            ["windows"] = WindowsVersion(),
            ["gpu"] = GpuAndDriver(capabilities.AdapterName),
            ["recording"] = RecordingConfiguration(config),
            ["logs"] = DiagnosticLogExporter.CreateExcerpt(),
        };

        string query = string.Join(
            "&",
            fields.Select(field =>
                $"{Uri.EscapeDataString(field.Key)}=" +
                Uri.EscapeDataString(field.Value)));
        return $"{UpdateService.RepositoryUrl}/issues/new?{query}";
    }

    private static string PackageName()
    {
        if (AppDistribution.IsMicrosoftStore)
            return "Microsoft Store";

        string executable = Environment.ProcessPath ?? "";
        string normalized = executable.Replace('/', '\\');
        if (normalized.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase))
        {
            return "Built from source";
        }

        string installerRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Captail");
        return normalized.StartsWith(
            installerRoot.TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase)
            ? "Installer"
            : "Portable";
    }

    private static string WindowsVersion()
    {
        Version version = Environment.OSVersion.Version;
        string product = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{product}, build {version.Build} " +
               $"({RuntimeInformation.OSArchitecture})";
    }

    private static string GpuAndDriver(string adapterName)
    {
        string name = string.IsNullOrWhiteSpace(adapterName)
            ? "Unknown GPU"
            : adapterName.Trim();
        string? driver = FindDisplayDriverVersion(name);
        return string.IsNullOrWhiteSpace(driver)
            ? name
            : $"{name}, driver {driver}";
    }

    private static string? FindDisplayDriverVersion(string adapterName)
    {
        const string displayClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? classKey = baseKey.OpenSubKey(displayClass);
            if (classKey is null)
                return null;

            string normalizedAdapter = NormalizeHardwareName(adapterName);
            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = classKey.OpenSubKey(subKeyName);
                string? description = deviceKey?.GetValue("DriverDesc") as string;
                string? version = deviceKey?.GetValue("DriverVersion") as string;
                if (string.IsNullOrWhiteSpace(description) ||
                    string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                string normalizedDescription = NormalizeHardwareName(description);
                if (normalizedAdapter.Contains(
                        normalizedDescription,
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedDescription.Contains(
                        normalizedAdapter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return version.Trim();
                }
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Could not read display driver version: {exception.Message}");
        }
        return null;
    }

    private static string NormalizeHardwareName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit));

    private static string RecordingConfiguration(Config config)
    {
        string source = config.CaptureSource == "game"
            ? "Game Capture"
            : "Desktop (automatic game switching)";
        string bitrate = config.BitrateMbps == 0
            ? "adaptive bitrate"
            : $"{config.BitrateMbps} Mbps";
        string buffer = FormatDuration(config.BufferSeconds);
        if (config.MaxReplaySizeMb > 0)
            buffer += $", {FormatMegabytes(config.MaxReplaySizeMb)} size limit";

        return string.Join(
            ", ",
            source,
            FormatCodec(config.Codec),
            FormatResolution(config.RecordingResolution),
            $"{config.FrameRate} FPS",
            bitrate,
            $"{buffer} buffer",
            FormatAudio(config));
    }

    internal static string FormatAudio(Config config)
    {
        var sources = new List<string>();
        bool advanced = string.Equals(
            config.AudioRoutingMode,
            "advanced",
            StringComparison.OrdinalIgnoreCase);
        if (advanced)
        {
            sources.Add($"process audio ({config.ProcessAudioRoutes.Count} rules)");
        }
        else if (config.CaptureSystemAudio)
        {
            sources.Add(
                $"{(config.CaptureSource == "game" ? "game" : "system")} audio " +
                $"{config.SystemAudioVolume}%");
        }
        if (config.CaptureMicrophone)
        {
            string boost = config.MicrophoneBoostDb > 0
                ? $", +{config.MicrophoneBoostDb} dB"
                : "";
            sources.Add($"microphone {config.MicrophoneVolume}%{boost}");
        }

        if (sources.Count == 0)
            return "no audio";

        string trackMode = advanced
            ? $"{ObsReplayEngine.AudioTrackCount(config)} tracks"
            : config.SeparateAudioTracks && sources.Count > 1
                ? "separate tracks"
                : "mixed track";
        return $"{string.Join(" + ", sources)}; {trackMode}; " +
               $"{config.AudioCodec.ToUpperInvariant()} " +
               $"{config.AudioBitrateKbps} kbps";
    }

    private static string FormatCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "H.265 (HEVC)",
        "av1" => "AV1",
        _ => codec.ToUpperInvariant(),
    };

    private static string FormatResolution(string resolution) =>
        string.Equals(resolution, "source", StringComparison.OrdinalIgnoreCase)
            ? "Source resolution"
            : resolution;

    private static string FormatDuration(int seconds) => seconds % 60 == 0
        ? $"{seconds / 60} min"
        : $"{seconds} sec";

    private static string FormatMegabytes(int megabytes) => megabytes >= 1024
        ? $"{megabytes / 1024d:0.#} GB"
        : $"{megabytes} MB";
}
