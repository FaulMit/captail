using System.Globalization;
using System.IO;
using System.Text;

namespace Captail;

/// <summary>Bounded local logs. Failure to log must never fail a capture operation.</summary>
internal sealed class DiagnosticLogFile
{
    internal const long DefaultMaxBytes = 8 * 1024 * 1024;
    internal const int DefaultArchiveCount = 4;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _archives;
    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private readonly Timer _flushTimer;
    private StreamWriter? _writer;
    private string? _activePath;
    private long _bytes;
    private int _lost;
    private string? _lastWriteError;
    private bool _retentionChecked;
    private bool _closed;
    internal string ActivePath { get { lock (_gate) return _activePath ?? _path; } }

    internal DiagnosticLogFile(string path, long maxBytes = DefaultMaxBytes, int archives = DefaultArchiveCount)
    {
        _path = path;
        _maxBytes = maxBytes;
        _archives = archives;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    internal void Write(DiagnosticLevel level, string component, string message, string? operationId = null)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            try
            {
                EnsureWriter();
                if (_lost > 0)
                {
                    WriteLines(DiagnosticLevel.Warning, "Log",
                        $"log.write-recovered lostEvents={_lost} previousError={_lastWriteError}", null);
                    _lost = 0;
                    _lastWriteError = null;
                }
                if (message.Length > 16384)
                    message = message[..16384] + " [message truncated]";
                WriteLines(level, component, message, operationId);
                if (level >= DiagnosticLevel.Warning)
                    _writer!.Flush();
                if (_bytes >= _maxBytes)
                    Rotate();
            }
            catch (Exception exception)
            {
                _lost++;
                _lastWriteError = $"{exception.GetType().Name}:0x{exception.HResult:X8} {exception.Message}";
                ResetWriter();
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
            return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (!_retentionChecked)
        {
            _retentionChecked = true;
            CleanOldProcessLogs();
        }
        _activePath ??= _path;
        try { OpenWriter(); }
        catch (IOException) when (_activePath == _path)
        {
            // A second process must not corrupt the active instance's append offsets.
            _activePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!,
                $"log-{Environment.ProcessId}.txt");
            OpenWriter();
        }
        WriteLines(DiagnosticLevel.Info, "Log",
            $"log.open format=2 maxBytes={_maxBytes} archives={_archives} path={_activePath}", null);
        _writer!.Flush();
    }

    private void OpenWriter()
    {
        var stream = new FileStream(_activePath!, FileMode.Append, FileAccess.Write,
            FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        _bytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void CleanOldProcessLogs()
    {
        try
        {
            var oldGroups = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(_path)!, "log-*.txt*")
                .Where(path => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path), @"^log-\d+\.txt(?:\.[1-4])?$"))
                .GroupBy(path => System.Text.RegularExpressions.Regex.Replace(path, @"(\.txt)\.[1-4]$", "$1"))
                .OrderByDescending(group => group.Max(System.IO.File.GetLastWriteTimeUtc)).Skip(3);
            foreach (var group in oldGroups)
            {
                try
                {
                    // Retain the entire history of an active process, including closed archives.
                    if (System.IO.File.Exists(group.Key))
                        using (new FileStream(group.Key, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string candidate in group)
                {
                    try
                    {
                        using (new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        System.IO.File.Delete(candidate);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteLines(DiagnosticLevel level, string component, string message, string? operationId)
    {
        string prefix = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture) +
            $" [{level.ToString().ToUpperInvariant()}] [{component}] session={_session} " +
            $"pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId}" +
            (operationId is null ? "" : $" op={operationId}") + " ";
        string[] lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (string line in lines.Take(64))
        {
            string formatted = prefix + line;
            _writer!.WriteLine(formatted);
            _bytes += Encoding.UTF8.GetByteCount(formatted) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        }
        if (lines.Length > 64)
        {
            string truncated = prefix + "[remaining message lines truncated]";
            _writer!.WriteLine(truncated);
            _bytes += Encoding.UTF8.GetByteCount(truncated) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        }
    }

    private void Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        for (int index = _archives; index >= 1; index--)
        {
            string destination = _activePath + "." + index;
            if (index == _archives)
                System.IO.File.Delete(destination);
            string source = index == 1 ? _activePath! : _activePath + "." + (index - 1);
            if (System.IO.File.Exists(source))
                System.IO.File.Move(source, destination, overwrite: true);
        }
        EnsureWriter();
    }

    internal void Flush()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); }
            catch (Exception exception)
            {
                _lost++;
                _lastWriteError = $"{exception.GetType().Name}:0x{exception.HResult:X8} {exception.Message}";
                ResetWriter();
            }
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _flushTimer.Dispose();
            try
            {
                if (_writer is not null)
                    WriteLines(DiagnosticLevel.Info, "Log", "log.close", null);
            }
            catch { }
            ResetWriter();
        }
    }

    private void ResetWriter()
    {
        try { _writer?.Dispose(); }
        catch { }
        _writer = null;
    }
}
