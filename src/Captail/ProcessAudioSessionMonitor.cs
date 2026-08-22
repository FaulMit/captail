using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Captail;

internal sealed record ProcessAudioSessionSnapshot(
    string Executable,
    string DisplayName,
    string? ExecutablePath,
    float Peak,
    bool IsActive,
    int ProcessCount);

internal sealed record ProcessAudioSessionUpdate(
    IReadOnlyList<ProcessAudioSessionSnapshot> Sessions,
    bool IsAvailable);

internal static class ProcessAudioSessionDiscovery
{
    private static readonly ConcurrentDictionary<uint, CachedProcessMetadata>
        MetadataCache = [];

    internal static IReadOnlyList<ProcessAudioSessionSnapshot> Capture()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);
        AudioSessionManager manager = device.AudioSessionManager;
        try
        {
            manager.RefreshSessions();
            var sessions = new Dictionary<string, MutableSession>(
                StringComparer.OrdinalIgnoreCase);
            SessionCollection collection = manager.Sessions;
            for (int index = 0; index < collection.Count; index++)
            {
                using AudioSessionControl session = collection[index];
                if (session.IsSystemSoundsSession)
                    continue;

                uint processId;
                try
                {
                    processId = session.GetProcessID;
                }
                catch
                {
                    continue;
                }
                if (processId == 0 || processId == Environment.ProcessId)
                    continue;

                ProcessMetadata? metadata = ResolveProcessMetadata(processId);
                if (metadata is null)
                    continue;

                float peak;
                try
                {
                    peak = Math.Clamp(
                        session.AudioMeterInformation.MasterPeakValue,
                        0f,
                        1f);
                }
                catch
                {
                    peak = 0;
                }

                bool active =
                    session.State == AudioSessionState.AudioSessionStateActive ||
                    peak > 0.0005f;
                if (!sessions.TryGetValue(
                        metadata.Executable,
                        out MutableSession? combined))
                {
                    combined = new MutableSession(metadata);
                    sessions.Add(metadata.Executable, combined);
                }
                combined.Peak = Math.Max(combined.Peak, peak);
                combined.IsActive |= active;
                combined.ProcessIds.Add(processId);
            }

            return sessions.Values
                .Select(session => new ProcessAudioSessionSnapshot(
                    session.Metadata.Executable,
                    session.Metadata.DisplayName,
                    session.Metadata.ExecutablePath,
                    session.Peak,
                    session.IsActive,
                    session.ProcessIds.Count))
                .OrderByDescending(session => session.IsActive)
                .ThenBy(
                    session => session.DisplayName,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static ProcessMetadata? ResolveProcessMetadata(uint processId)
    {
        DateTime now = DateTime.UtcNow;
        if (MetadataCache.TryGetValue(processId, out CachedProcessMetadata? cached) &&
            cached.ExpiresUtc > now)
        {
            return cached.Metadata;
        }

        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            string executable = Config.NormalizeExecutableName(process.ProcessName);
            if (executable.Length == 0)
                return null;

            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
                // Elevated and protected processes may hide their image path.
            }

            string displayName = FriendlyName(executable, executablePath);
            var metadata = new ProcessMetadata(
                executable,
                displayName,
                executablePath);
            MetadataCache[processId] = new CachedProcessMetadata(
                metadata,
                now.AddSeconds(2));
            if (MetadataCache.Count > 512)
            {
                foreach ((uint id, CachedProcessMetadata value) in MetadataCache)
                {
                    if (value.ExpiresUtc <= now)
                        MetadataCache.TryRemove(id, out _);
                }
            }
            return metadata;
        }
        catch
        {
            MetadataCache.TryRemove(processId, out _);
            return null;
        }
    }

    private static string FriendlyName(string executable, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                string? description = info.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(description) &&
                    description.Length <= 80)
                {
                    return description;
                }
            }
            catch
            {
                // Fall back to the executable name.
            }
        }

        string name = Path.GetFileNameWithoutExtension(executable);
        return name.Length == 0 ? executable : name;
    }

    private sealed record ProcessMetadata(
        string Executable,
        string DisplayName,
        string? ExecutablePath);

    private sealed record CachedProcessMetadata(
        ProcessMetadata Metadata,
        DateTime ExpiresUtc);

    private sealed class MutableSession(ProcessMetadata metadata)
    {
        internal ProcessMetadata Metadata { get; } = metadata;
        internal HashSet<uint> ProcessIds { get; } = [];
        internal float Peak { get; set; }
        internal bool IsActive { get; set; }
    }
}

internal sealed class ProcessAudioSessionMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(160);
    private readonly Action<ProcessAudioSessionUpdate> _updated;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, float> _displayedPeaks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;
    private DateTime _lastFailureLogUtc;
    private int _disposed;

    internal ProcessAudioSessionMonitor(
        Action<ProcessAudioSessionUpdate> updated)
    {
        _updated = updated;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<ProcessAudioSessionSnapshot> sessions =
                    ProcessAudioSessionDiscovery.Capture();
                var visibleExecutables = new HashSet<string>(
                    sessions.Select(session => session.Executable),
                    StringComparer.OrdinalIgnoreCase);
                ProcessAudioSessionSnapshot[] smoothed = sessions
                    .Select(SmoothPeak)
                    .ToArray();
                foreach (string executable in _displayedPeaks.Keys.ToArray())
                {
                    if (!visibleExecutables.Contains(executable))
                        _displayedPeaks.Remove(executable);
                }
                _updated(new ProcessAudioSessionUpdate(smoothed, true));
            }
            catch (Exception exception)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastFailureLogUtc >= TimeSpan.FromSeconds(30))
                {
                    _lastFailureLogUtc = now;
                    Log.Write(
                        $"Audio-session meter unavailable " +
                        $"({exception.GetType().Name}).");
                }
                _updated(new ProcessAudioSessionUpdate([], false));
            }

            try
            {
                await Task.Delay(PollInterval, _cancellation.Token);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private ProcessAudioSessionSnapshot SmoothPeak(
        ProcessAudioSessionSnapshot session)
    {
        _displayedPeaks.TryGetValue(session.Executable, out float previous);
        float displayed = Math.Max(session.Peak, previous * 0.72f);
        if (displayed < 0.001f)
            displayed = 0;
        _displayedPeaks[session.Executable] = displayed;
        return session with { Peak = displayed };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Cancel();
        try
        {
            await _worker;
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
