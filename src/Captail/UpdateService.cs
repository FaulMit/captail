using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Captail;

internal sealed record UpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Digest);

internal sealed record UpdateRelease(
    Version Version,
    string Tag,
    Uri ReleasePage,
    bool Prerelease,
    UpdateAsset SetupAsset,
    UpdateAsset PortableAsset,
    UpdateAsset? ChecksumsAsset);

internal enum UpdatePackageKind
{
    Installer,
    Portable,
}

internal sealed record PreparedUpdate(
    UpdatePackageKind Kind,
    Version Version,
    string PackagePath,
    string? PortablePayloadDirectory);

internal sealed class UpdateService
{
    internal const string RepositoryUrl =
        "https://github.com/FaulMit/captail";

    internal const string FeatureRequestUrl =
        "https://github.com/FaulMit/captail/issues/new?template=feature_request.yml";

    private const string ReleasesApiUrl =
        "https://api.github.com/repos/FaulMit/captail/releases?per_page=10";
    private const long MaximumAssetBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Regex ReleaseTagPattern = new(
        @"^v?(?<version>\d+\.\d+\.\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        @"^[0-9a-f]{64}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    internal static Version CurrentVersion { get; } =
        NormalizeVersion(
            Assembly.GetEntryAssembly()?.GetName().Version ??
            typeof(UpdateService).Assembly.GetName().Version ??
            new Version(0, 0, 0));

    internal static string CurrentVersionText =>
        FormatVersion(CurrentVersion);

    private static readonly HttpClient Client = CreateClient();

    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private UpdateRelease? _cachedRelease;
    private DateTime _lastCheckUtc;

    internal async Task<UpdateRelease?> CheckAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (AppDistribution.IsMicrosoftStore)
            return null;

        if (!force &&
            _lastCheckUtc != default &&
            DateTime.UtcNow - _lastCheckUtc < CacheDuration)
        {
            return _cachedRelease;
        }

        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            if (!force &&
                _lastCheckUtc != default &&
                DateTime.UtcNow - _lastCheckUtc < CacheDuration)
            {
                return _cachedRelease;
            }

            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            checkCts.CancelAfter(TimeSpan.FromSeconds(15));
            CancellationToken checkToken = checkCts.Token;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ReleasesApiUrl);
            request.Headers.Accept.ParseAdd(
                "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                checkToken);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(
                checkToken);
            List<GitHubRelease>? releases =
                await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
                    stream,
                    cancellationToken: checkToken);

            bool includePrereleases = CurrentVersion.Major == 0;
            _cachedRelease = releases?
                .Where(release => !release.Draft)
                .Where(release => includePrereleases || !release.Prerelease)
                .Select(TryMapRelease)
                .Where(release => release is not null)
                .Cast<UpdateRelease>()
                .Where(release => release.Version > CurrentVersion)
                .OrderByDescending(release => release.Version)
                .FirstOrDefault();
            _lastCheckUtc = DateTime.UtcNow;
            return _cachedRelease;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    internal async Task<PreparedUpdate> PrepareAsync(
        UpdateRelease release,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        EnsureSelfUpdateAllowed();

        bool installed = IsInstalledBuild();
        UpdateAsset package = installed
            ? release.SetupAsset
            : release.PortableAsset;
        ValidateAsset(package);

        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Captail",
            "Updates",
            FormatVersion(release.Version));
        Directory.CreateDirectory(updateDirectory);

        string packagePath = Path.Combine(updateDirectory, package.Name);
        string expectedHash = await ResolveExpectedHashAsync(
            package,
            release.ChecksumsAsset,
            cancellationToken);
        await DownloadVerifiedAsync(
            package,
            packagePath,
            expectedHash,
            progress,
            cancellationToken);

        if (installed)
        {
            return new PreparedUpdate(
                UpdatePackageKind.Installer,
                release.Version,
                packagePath,
                null);
        }

        string extractionDirectory = Path.Combine(
            updateDirectory,
            "portable");
        if (Directory.Exists(extractionDirectory))
            Directory.Delete(extractionDirectory, recursive: true);
        Directory.CreateDirectory(extractionDirectory);
        ExtractPortablePackage(packagePath, extractionDirectory);

        string payloadDirectory = Path.Combine(
            extractionDirectory,
            $"Captail-{FormatVersion(release.Version)}");
        if (!File.Exists(Path.Combine(payloadDirectory, "Captail.exe")) ||
            !File.Exists(Path.Combine(payloadDirectory, "obs.dll")))
        {
            throw new InvalidDataException(
                "Portable update does not contain required Captail files.");
        }

        return new PreparedUpdate(
            UpdatePackageKind.Portable,
            release.Version,
            packagePath,
            payloadDirectory);
    }

    internal static void Launch(PreparedUpdate update)
    {
        EnsureSelfUpdateAllowed();

        if (update.Kind == UpdatePackageKind.Installer)
        {
            if (Process.Start(new ProcessStartInfo
            {
                FileName = update.PackagePath,
                UseShellExecute = true,
            }) is null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the Captail installer.");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(update.PortablePayloadDirectory))
        {
            throw new InvalidOperationException(
                "Portable update payload is unavailable.");
        }

        string targetDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string executablePath =
            Environment.ProcessPath ??
            Path.Combine(targetDirectory, "Captail.exe");
        string updaterDirectory = Path.GetDirectoryName(update.PackagePath) ??
            throw new InvalidOperationException(
                "Update directory is unavailable.");
        string scriptPath = Path.Combine(
            updaterDirectory,
            "apply-portable-update.ps1");
        string logPath = Path.Combine(
            updaterDirectory,
            "portable-update.log");

        File.WriteAllText(
            scriptPath,
            BuildPortableUpdateScript(
                Environment.ProcessId,
                update.PortablePayloadDirectory,
                targetDirectory,
                executablePath,
                logPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-WindowStyle",
                    "Hidden",
                    "-File",
                    scriptPath,
                },
        }) is null)
        {
            throw new InvalidOperationException(
                "Windows did not start the Portable update helper.");
        }
    }

    private static void EnsureSelfUpdateAllowed()
    {
        if (AppDistribution.IsMicrosoftStore)
        {
            throw new InvalidOperationException(
                "Updates are managed by Microsoft Store.");
        }
    }

    private static UpdateRelease? TryMapRelease(GitHubRelease release)
    {
        Match match = ReleaseTagPattern.Match(release.TagName ?? "");
        if (!match.Success ||
            !Version.TryParse(match.Groups["version"].Value, out Version? parsed))
        {
            return null;
        }

        Version version = NormalizeVersion(parsed);
        string versionText = FormatVersion(version);
        UpdateAsset? setup = FindAsset(
            release.Assets,
            $"Captail-{versionText}-Setup-win-x64.exe");
        UpdateAsset? portable = FindAsset(
            release.Assets,
            $"Captail-{versionText}-Portable-win-x64.zip");
        if (setup is null ||
            portable is null ||
            !TryCreateGitHubUri(release.HtmlUrl, out Uri releasePage))
        {
            return null;
        }

        UpdateAsset? checksums = FindAsset(
            release.Assets,
            "SHA256SUMS.txt");
        return new UpdateRelease(
            version,
            $"v{versionText}",
            releasePage,
            release.Prerelease,
            setup,
            portable,
            checksums);
    }

    private static UpdateAsset? FindAsset(
        IEnumerable<GitHubAsset>? assets,
        string expectedName)
    {
        GitHubAsset? asset = assets?.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Name,
                expectedName,
                StringComparison.Ordinal));
        if (asset is null ||
            !TryCreateGitHubUri(asset.BrowserDownloadUrl, out Uri uri))
        {
            return null;
        }

        return new UpdateAsset(
            expectedName,
            uri,
            asset.Size,
            asset.Digest);
    }

    private static bool TryCreateGitHubUri(string? value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(
                parsed.Host,
                "github.com",
                StringComparison.OrdinalIgnoreCase))
        {
            uri = null!;
            return false;
        }
        uri = parsed;
        return true;
    }

    private static void ValidateAsset(UpdateAsset asset)
    {
        if (asset.Size <= 0 || asset.Size > MaximumAssetBytes)
        {
            throw new InvalidDataException(
                $"Unexpected update size: {asset.Size} bytes.");
        }
        if (Path.GetFileName(asset.Name) != asset.Name)
        {
            throw new InvalidDataException(
                "Update asset has an invalid name.");
        }
    }

    private static async Task<string> ResolveExpectedHashAsync(
        UpdateAsset package,
        UpdateAsset? checksums,
        CancellationToken cancellationToken)
    {
        const string prefix = "sha256:";
        if (package.Digest?.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            string digest = package.Digest[prefix.Length..];
            if (Sha256Pattern.IsMatch(digest))
                return digest.ToLowerInvariant();
        }

        if (checksums is null)
        {
            throw new InvalidDataException(
                "Release does not provide a SHA-256 digest.");
        }
        ValidateAsset(checksums);
        if (checksums.Size > 64 * 1024)
        {
            throw new InvalidDataException(
                "Checksum file is unexpectedly large.");
        }

        string content = await Client.GetStringAsync(
            checksums.DownloadUri,
            cancellationToken);
        foreach (string line in content.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            string suffix = $"  {package.Name}";
            if (!line.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            string digest = line[..^suffix.Length].Trim();
            if (Sha256Pattern.IsMatch(digest))
                return digest.ToLowerInvariant();
        }

        throw new InvalidDataException(
            $"SHA-256 digest for {package.Name} was not found.");
    }

    private static async Task DownloadVerifiedAsync(
        UpdateAsset asset,
        string destinationPath,
        string expectedHash,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string temporaryPath = destinationPath + ".download";
        try
        {
            if (File.Exists(destinationPath) &&
                new FileInfo(destinationPath).Length == asset.Size)
            {
                byte[] cachedHash;
                await using (var cachedFile = new FileStream(
                                 destinationPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 128 * 1024,
                                 FileOptions.Asynchronous |
                                 FileOptions.SequentialScan))
                {
                    cachedHash = await SHA256.HashDataAsync(
                        cachedFile,
                        cancellationToken);
                }

                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(
                            Convert.ToHexString(cachedHash)
                                .ToLowerInvariant()),
                        Encoding.ASCII.GetBytes(expectedHash)))
                {
                    progress?.Report(100);
                    return;
                }
                File.Delete(destinationPath);
            }

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            using HttpResponseMessage response = await Client.GetAsync(
                asset.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaximumAssetBytes ||
                contentLength is > 0 && contentLength != asset.Size)
            {
                throw new InvalidDataException(
                    "Downloaded update size does not match release metadata.");
            }

            await using (Stream source =
                         await response.Content.ReadAsStreamAsync(
                             cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                using IncrementalHash hash =
                    IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                byte[] buffer = new byte[128 * 1024];
                long total = 0;
                int lastProgress = -1;
                while (true)
                {
                    int read = await source.ReadAsync(
                        buffer,
                        cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > asset.Size || total > MaximumAssetBytes)
                    {
                        throw new InvalidDataException(
                            "Downloaded update exceeds expected size.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);

                    int percent = (int)Math.Clamp(
                        total * 100L / asset.Size,
                        0,
                        100);
                    if (percent != lastProgress)
                    {
                        progress?.Report(percent);
                        lastProgress = percent;
                    }
                }
                await destination.FlushAsync(cancellationToken);

                if (total != asset.Size)
                {
                    throw new InvalidDataException(
                        "Downloaded update is incomplete.");
                }
                string actualHash =
                    Convert.ToHexString(hash.GetHashAndReset())
                        .ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(actualHash),
                        Encoding.ASCII.GetBytes(expectedHash)))
                {
                    throw new InvalidDataException(
                        "Downloaded update failed SHA-256 verification.");
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(100);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve original update error.
            }
            throw;
        }
    }

    private static void ExtractPortablePackage(
        string archivePath,
        string destinationDirectory)
    {
        string destinationRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        long totalSize = 0;
        int entryCount = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            entryCount++;
            totalSize += entry.Length;
            if (entryCount > 20_000 ||
                totalSize > 2L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "Portable update archive is unexpectedly large.");
            }

            string targetPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, entry.FullName));
            if (!targetPath.StartsWith(
                    destinationRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Portable update contains an unsafe path.");
            }
        }

        ZipFile.ExtractToDirectory(
            archivePath,
            destinationDirectory,
            overwriteFiles: true);
    }

    private static string BuildPortableUpdateScript(
        int processId,
        string payloadDirectory,
        string targetDirectory,
        string executablePath,
        string logPath)
    {
        static string Quote(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            try {
              $deadline = [DateTime]::UtcNow.AddSeconds(60)
              while (Get-Process -Id {{processId}} -ErrorAction SilentlyContinue) {
                if ([DateTime]::UtcNow -ge $deadline) {
                  throw 'Captail did not stop before portable update.'
                }
                Start-Sleep -Milliseconds 250
              }
              Get-ChildItem -LiteralPath {{Quote(payloadDirectory)}} |
                Copy-Item -Destination {{Quote(targetDirectory)}} -Recurse -Force
              Start-Process -FilePath {{Quote(executablePath)}}
            }
            catch {
              $_ | Out-File -LiteralPath {{Quote(logPath)}} -Encoding utf8
              Add-Type -AssemblyName PresentationFramework
              [System.Windows.MessageBox]::Show(
                "Captail update failed.`n`n$($_.Exception.Message)",
                'Captail',
                'OK',
                'Error') | Out-Null
              exit 1
            }
            """;
    }

    private static bool IsInstalledBuild()
    {
        string applicationDirectory = Path.GetFullPath(
            AppContext.BaseDirectory);
        string installedDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Captail"));
        if (string.Equals(
                applicationDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                installedDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Directory.EnumerateFiles(
                applicationDirectory,
                "unins*.exe",
                SearchOption.TopDirectoryOnly)
            .Any();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Captail/{CurrentVersionText}");
        return client;
    }

    private static Version NormalizeVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    internal static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
