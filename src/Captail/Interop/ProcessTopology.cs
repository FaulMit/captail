using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Captail.Interop;

internal readonly record struct ProcessIdentity(uint ProcessId, long CreationTime);

internal sealed record ProcessNode(
    ProcessIdentity Identity,
    uint ParentProcessId,
    string Executable)
{
    internal ProcessIdentity? ParentIdentity { get; init; }
}

internal sealed record RoutedProcessRoot(ProcessNode Node, int Track);

internal sealed record RoutedProcessSelection(
    IReadOnlyList<RoutedProcessRoot> Roots,
    int ConflictingSources);

internal sealed class ProcessSnapshot
{
    private readonly IReadOnlyDictionary<ProcessIdentity, ProcessNode> _nodes;

    private ProcessSnapshot(IEnumerable<ProcessNode> nodes)
    {
        ProcessNode[] materialized = nodes.ToArray();
        var byProcessId = new Dictionary<uint, ProcessNode>();
        foreach (ProcessNode node in materialized)
        {
            if (!byProcessId.TryAdd(node.Identity.ProcessId, node))
                throw new ArgumentException("A process snapshot cannot contain duplicate process IDs.");
        }

        var validated = new Dictionary<ProcessIdentity, ProcessNode>();
        foreach (ProcessNode node in materialized)
        {
            ProcessIdentity? parentIdentity = null;
            if (node.ParentProcessId != 0 &&
                node.ParentProcessId != node.Identity.ProcessId &&
                byProcessId.TryGetValue(node.ParentProcessId, out ProcessNode? parent) &&
                parent.Identity.CreationTime < node.Identity.CreationTime)
            {
                parentIdentity = parent.Identity;
            }

            ProcessNode validatedNode = node with { ParentIdentity = parentIdentity };
            validated.Add(validatedNode.Identity, validatedNode);
        }

        _nodes = validated;
    }

    internal IReadOnlyCollection<ProcessNode> Nodes => _nodes.Values.ToArray();

    internal static ProcessSnapshot CreateSynthetic(IEnumerable<ProcessNode> nodes) => new(nodes);

    internal static ProcessSnapshot Capture()
    {
        if (!ProcessNative.ProcessIdToSessionId(
                checked((uint)Environment.ProcessId),
                out uint currentSessionId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using ProcessNative.SafeSnapshotHandle snapshot =
            ProcessNative.CreateToolhelp32Snapshot(
            ProcessNative.Th32csSnapProcess,
            0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var nodes = new List<ProcessNode>();
        var entry = new ProcessNative.ProcessEntry32
        {
            Size = checked((uint)Marshal.SizeOf<ProcessNative.ProcessEntry32>()),
            ExecutableFile = "",
        };

        if (!ProcessNative.Process32First(snapshot, ref entry))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ProcessNative.ErrorNoMoreFiles)
                return new ProcessSnapshot(nodes);
            throw new Win32Exception(error);
        }

        do
        {
            TryAddProcess(entry, currentSessionId, nodes);
            entry.Size = checked((uint)Marshal.SizeOf<ProcessNative.ProcessEntry32>());
        }
        while (ProcessNative.Process32Next(snapshot, ref entry));

        int finalError = Marshal.GetLastWin32Error();
        if (finalError != ProcessNative.ErrorNoMoreFiles)
            throw new Win32Exception(finalError);
        return new ProcessSnapshot(nodes);
    }

    internal IReadOnlyList<ProcessNode> SelectIndependentRoots(
        IEnumerable<string> executableTargets)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string target in executableTargets)
        {
            string normalized = NormalizeExecutable(target);
            if (normalized.Length > 0)
                targets.Add(normalized);
        }

        if (targets.Count == 0)
            return [];

        ProcessNode[] candidates = _nodes.Values
            .Where(node => targets.Contains(node.Executable))
            .OrderBy(NodeDepth)
            .ThenBy(node => node.Identity.CreationTime)
            .ThenBy(node => node.Identity.ProcessId)
            .ToArray();

        var selectedIdentities = new HashSet<ProcessIdentity>();
        var selected = new List<ProcessNode>();
        foreach (ProcessNode candidate in candidates)
        {
            if (HasSelectedAncestor(candidate, selectedIdentities))
                continue;
            selected.Add(candidate);
            selectedIdentities.Add(candidate.Identity);
        }
        return selected;
    }

    internal RoutedProcessSelection SelectRoutedRoots(
        IReadOnlyDictionary<string, int> executableTargets)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string executable, int track) in executableTargets)
        {
            string normalized = NormalizeExecutable(executable);
            if (normalized.Length > 0 && track is >= 1 and <= 6)
                targets.TryAdd(normalized, track);
        }

        RoutedProcessRoot[] candidates = _nodes.Values
            .Where(node => targets.ContainsKey(node.Executable))
            .Select(node => new RoutedProcessRoot(node, targets[node.Executable]))
            .ToArray();
        if (candidates.Length == 0)
            return new RoutedProcessSelection([], 0);

        RoutedProcessRoot[] eligible = candidates
            .Where(candidate => !candidates.Any(other =>
                other.Track != candidate.Track &&
                HasAncestor(other.Node, candidate.Node.Identity)))
            .OrderBy(candidate => NodeDepth(candidate.Node))
            .ThenBy(candidate => candidate.Node.Identity.CreationTime)
            .ThenBy(candidate => candidate.Node.Identity.ProcessId)
            .ToArray();

        var selectedIdentities = new HashSet<ProcessIdentity>();
        var selected = new List<RoutedProcessRoot>();
        foreach (RoutedProcessRoot candidate in eligible)
        {
            if (HasSelectedAncestor(candidate.Node, selectedIdentities))
                continue;
            selected.Add(candidate);
            selectedIdentities.Add(candidate.Node.Identity);
        }

        return new RoutedProcessSelection(
            selected,
            candidates.Length - eligible.Length);
    }

    internal static string NormalizeExecutable(string? value) =>
        global::Captail.Config.NormalizeExecutableName(value);

    private static void TryAddProcess(
        ProcessNative.ProcessEntry32 entry,
        uint currentSessionId,
        ICollection<ProcessNode> nodes)
    {
        uint processId = entry.ProcessId;
        if (processId == 0 || processId == Environment.ProcessId ||
            !ProcessNative.ProcessIdToSessionId(processId, out uint sessionId) ||
            sessionId != currentSessionId)
        {
            return;
        }

        using SafeProcessHandle process = ProcessNative.OpenProcess(
            ProcessNative.ProcessQueryLimitedInformation,
            false,
            processId);
        if (process.IsInvalid ||
            !ProcessNative.GetProcessTimes(
                process,
                out ProcessNative.FileTime creationTime,
                out _,
                out _,
                out _))
        {
            return;
        }

        string executable = QueryExecutable(process);
        if (executable.Length == 0)
            executable = NormalizeExecutable(entry.ExecutableFile);
        if (executable.Length == 0)
            return;

        long creation = unchecked((long)(
            ((ulong)creationTime.HighDateTime << 32) |
            creationTime.LowDateTime));
        nodes.Add(new ProcessNode(
            new ProcessIdentity(processId, creation),
            entry.ParentProcessId,
            executable));
    }

    private static string QueryExecutable(SafeProcessHandle process)
    {
        uint capacity = 1024;
        var buffer = new StringBuilder(checked((int)capacity));
        if (!ProcessNative.QueryFullProcessImageName(process, 0, buffer, ref capacity))
            return "";
        return NormalizeExecutable(buffer.ToString());
    }

    private bool HasSelectedAncestor(
        ProcessNode node,
        IReadOnlySet<ProcessIdentity> selected)
    {
        var visited = new HashSet<ProcessIdentity> { node.Identity };
        ProcessIdentity? parentIdentity = node.ParentIdentity;
        while (parentIdentity is ProcessIdentity parent)
        {
            if (!visited.Add(parent))
                return false;
            if (selected.Contains(parent))
                return true;
            if (!_nodes.TryGetValue(parent, out ProcessNode? parentNode))
                return false;
            parentIdentity = parentNode.ParentIdentity;
        }
        return false;
    }

    private bool HasAncestor(ProcessNode node, ProcessIdentity ancestor)
    {
        var visited = new HashSet<ProcessIdentity> { node.Identity };
        ProcessIdentity? parentIdentity = node.ParentIdentity;
        while (parentIdentity is ProcessIdentity parent)
        {
            if (!visited.Add(parent))
                return false;
            if (parent == ancestor)
                return true;
            if (!_nodes.TryGetValue(parent, out ProcessNode? parentNode))
                return false;
            parentIdentity = parentNode.ParentIdentity;
        }
        return false;
    }

    private int NodeDepth(ProcessNode node)
    {
        int depth = 0;
        var visited = new HashSet<ProcessIdentity> { node.Identity };
        ProcessIdentity? parentIdentity = node.ParentIdentity;
        while (parentIdentity is ProcessIdentity parent &&
               visited.Add(parent) &&
               _nodes.TryGetValue(parent, out ProcessNode? parentNode))
        {
            depth++;
            parentIdentity = parentNode.ParentIdentity;
        }
        return depth;
    }
}

internal static class ProcessNative
{
    internal const uint Th32csSnapProcess = 0x00000002;
    internal const uint ProcessQueryLimitedInformation = 0x00001000;
    internal const int ErrorNoMoreFiles = 18;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal nuint DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

    internal sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeSnapshotHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeSnapshotHandle CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
