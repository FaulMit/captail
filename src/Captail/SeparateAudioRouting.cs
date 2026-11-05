using Captail.Interop;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Captail;

internal sealed record SeparateAudioSession(uint ProcessId, string EndpointId,
    string EndpointName, bool IsSystemSounds, string AdapterName = "");

internal sealed record SeparateAudioEnvironment(
    IReadOnlyList<SeparateAudioSession> Sessions, bool IsComplete)
{
    internal static SeparateAudioEnvironment Capture()
    {
        var sessions = new List<SeparateAudioSession>();
        bool complete = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(
                         DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    AudioSessionManager? manager = null;
                    try
                    {
                        manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        string id = device.ID;
                        string name = device.FriendlyName;
                        string adapter = device.DeviceFriendlyName;
                        for (int index = 0; index < manager.Sessions.Count; index++)
                        {
                            using AudioSessionControl session = manager.Sessions[index];
                            if (session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;
                            sessions.Add(new SeparateAudioSession(
                                session.GetProcessID, id, name, session.IsSystemSoundsSession, adapter));
                        }
                    }
                    catch
                    {
                        complete = false;
                    }
                    finally
                    {
                        manager?.Dispose();
                    }
                }
            }
        }
        catch
        {
            complete = false;
        }
        return new SeparateAudioEnvironment(sessions, complete);
    }
}

/// <summary>Plans original application streams for known re-rendering mixers.</summary>
internal sealed class SeparateAudioRouting
{
    private readonly HashSet<ProcessIdentity> _originalRenderers = [];
    private bool _enabled;

    internal IReadOnlyList<ProcessAudioTarget>? Build(ProcessSnapshot snapshot,
        ProcessIdentity? gameIdentity, uint captailProcessId)
    {
        SeparateAudioEnvironment? environment = snapshot.AudioEnvironment;
        if (environment is null)
            return null;
        var nodes = snapshot.Nodes.ToDictionary(node => node.Identity.ProcessId);
        ProcessNode[] mixers = nodes.Values.Where(node => IsVoicemeeter(node.Executable)).ToArray();
        bool hasVirtualInput = environment.Sessions.Any(session =>
            (session.EndpointName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
             session.AdapterName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)) &&
            !session.IsSystemSounds && nodes.TryGetValue(session.ProcessId, out ProcessNode? node) &&
            !IsVoicemeeter(node.Executable));
        bool hasMixerOutput = environment.Sessions.Any(session =>
            mixers.Any(mixer => mixer.Identity.ProcessId == session.ProcessId));
        // Keep the selected strategy through session expiry, device switches and
        // mixer restarts. Switching back to a global mix can briefly re-add echoes.
        _enabled |= hasVirtualInput && hasMixerOutput && environment.IsComplete;
        if (!_enabled)
            return null;

        _originalRenderers.RemoveWhere(identity =>
            !nodes.TryGetValue(identity.ProcessId, out ProcessNode? node) || node.Identity != identity);
        foreach (SeparateAudioSession session in environment.Sessions)
        {
            if (!session.IsSystemSounds && nodes.TryGetValue(session.ProcessId, out ProcessNode? node))
                _originalRenderers.Add(node.Identity);
        }
        ProcessNode? captail = nodes.GetValueOrDefault(captailProcessId);
        bool IsExcluded(ProcessNode node) =>
            node.Identity.ProcessId == captailProcessId ||
            node.ParentProcessId == captailProcessId ||
            (captail is not null && snapshot.IsInTree(node, captail.Identity)) ||
            mixers.Any(mixer => snapshot.IsInTree(node, mixer.Identity)) ||
            (gameIdentity is ProcessIdentity game && snapshot.IsInTree(node, game));

        var targets = new List<ProcessAudioTarget>();
        foreach (ProcessNode node in nodes.Values.Where(node =>
                     _originalRenderers.Contains(node.Identity) && !IsExcluded(node)))
        {
            // A process-loopback source includes descendants. Never include an
            // ancestor that would bring a deliberately excluded renderer back.
            if (mixers.Any(mixer => snapshot.IsInTree(mixer, node.Identity)) ||
                (gameIdentity is ProcessIdentity game &&
                 nodes.TryGetValue(game.ProcessId, out ProcessNode? gameNode) &&
                 gameNode.Identity == game && snapshot.IsInTree(gameNode, node.Identity)))
                continue;
            targets.Add(new ProcessAudioTarget(node.Executable, 1) { Identity = node.Identity });
        }
        if (gameIdentity is ProcessIdentity selectedGame &&
            nodes.TryGetValue(selectedGame.ProcessId, out ProcessNode? selectedNode) &&
            selectedNode.Identity == selectedGame)
            targets.Add(new ProcessAudioTarget(selectedNode.Executable, 2) { Identity = selectedGame });
        return targets;
    }

    private static bool IsVoicemeeter(string executable) =>
        Config.NormalizeExecutableName(executable) is
            "voicemeeter.exe" or "voicemeeter_x64.exe" or
            "voicemeeterpro.exe" or "voicemeeterpro_x64.exe" or
            "voicemeeter8.exe" or "voicemeeter8x64.exe";
}
