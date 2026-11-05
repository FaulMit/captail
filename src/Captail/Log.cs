using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Captail;

internal enum DiagnosticLevel { Debug, Info, Warning, Error, Fatal }

public static class Log
{
    private static readonly AsyncLocal<string?> Operation = new();
    private static readonly DiagnosticLogFile File = new(AppDataPaths.LogFile);
    private static readonly object RateGate = new();
    private static readonly Dictionary<string, (long Last, int Suppressed)> Rates = [];
    private static readonly Regex ErrorPattern = new(
        @"\b(?:errors?|fail(?:ed|ure)?|crash(?:ed)?|exception)\b(?!\s*=\s*0\b)|" +
        @"\bruntimeFailed\s*=\s*[1-9]\d*|timed out",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly string Path = AppDataPaths.LogFile;
    internal static string? OperationId => Operation.Value;
    internal static string CurrentPath => File.ActivePath;

    internal static long FileSize(string path)
    {
        try { return new System.IO.FileInfo(path).Length; }
        catch { return -1; }
    }

    static Log() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();

    public static void Write(string message) => Event(
        IsUrgent(message) ? DiagnosticLevel.Error : DiagnosticLevel.Info, "App", message);

    internal static void Event(DiagnosticLevel level, string component, string name,
        object? details = null, string? operationId = null, Exception? exception = null)
    {
        string message = name;
        try
        {
            if (details is not null)
                message += " " + JsonSerializer.Serialize(details);
            if (exception is not null)
            {
                if (exception is System.ComponentModel.Win32Exception nativeError)
                    message += $" nativeError={nativeError.NativeErrorCode}";
                message += $" hresult=0x{exception.HResult:X8}\n{exception}";
            }
        }
        catch (Exception formattingError)
        {
            message += " [diagnostic formatting failed: " + formattingError.GetType().Name + "]";
        }
        File.Write(level, component, message, operationId ?? OperationId);
    }

    internal static void Native(int level, string message)
    {
        DiagnosticLevel severity = level <= 100 ? DiagnosticLevel.Error :
            level <= 200 ? DiagnosticLevel.Warning :
            level <= 300 ? DiagnosticLevel.Info : DiagnosticLevel.Debug;
        string? key = message.Contains("Audio timestamp", StringComparison.Ordinal) ? "audio-timestamps" :
            message.Contains("Changing output file to", StringComparison.Ordinal) ? "segment-rotation" : null;
        if (key is not null && level > 200)
        {
            lock (RateGate)
            {
                long now = Stopwatch.GetTimestamp();
                if (Rates.TryGetValue(key, out var rate) &&
                    Stopwatch.GetElapsedTime(rate.Last, now) < TimeSpan.FromSeconds(30))
                {
                    Rates[key] = (rate.Last, rate.Suppressed + 1);
                    return;
                }
                if (rate.Suppressed > 0)
                    Event(DiagnosticLevel.Info, "OBS", "native.repeated",
                        new { kind = key, suppressed = rate.Suppressed });
                Rates[key] = (now, 0);
            }
        }
        Event(severity, "OBS", $"libobs[{level}]: {message}");
    }

    internal static DiagnosticOperation BeginOperation(string name, object? details = null) => new(name, details);

    internal static string? SetOperation(string? id)
    {
        string? previous = Operation.Value;
        Operation.Value = id;
        return previous;
    }

    public static void Close()
    {
        lock (RateGate)
        {
            foreach (var (key, rate) in Rates)
                if (rate.Suppressed > 0)
                    Event(DiagnosticLevel.Info, "OBS", "native.repeated",
                        new { kind = key, suppressed = rate.Suppressed });
            Rates.Clear();
        }
        File.Close();
    }

    internal static void Flush() => File.Flush();

    private static bool IsUrgent(string message) => ErrorPattern.IsMatch(message);
}

internal sealed class DiagnosticOperation : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Timer _progress;
    private string _stage = "requested";
    private int _finished;
    internal string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    internal DiagnosticOperation(string name, object? details)
    {
        _name = name;
        _previous = Log.SetOperation(Id);
        Log.Event(DiagnosticLevel.Info, "Operation", name + ".begin",
            new { parentOperation = _previous, context = details }, Id);
        _progress = new Timer(_ =>
        {
            if (Volatile.Read(ref _finished) == 0)
                Log.Event(DiagnosticLevel.Info, "Operation", _name + ".pending",
                    new { stage = Volatile.Read(ref _stage), elapsedMs = _elapsed.ElapsedMilliseconds }, Id);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal void Stage(string stage, object? details = null)
    {
        Volatile.Write(ref _stage, stage);
        Log.Event(DiagnosticLevel.Info, "Operation", _name + "." + stage,
            new { elapsedMs = _elapsed.ElapsedMilliseconds, state = details }, Id);
    }

    internal void Complete(object? details = null) => Finish("completed", null, details);
    internal void Fail(Exception exception) => Finish(
        exception is OperationCanceledException ? "cancelled" : "failed", exception, null);

    private void Finish(string outcome, Exception? exception, object? details)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        _progress.Dispose();
        Log.Event(exception is null or OperationCanceledException ? DiagnosticLevel.Info : DiagnosticLevel.Error,
            "Operation", _name + "." + outcome,
            new { stage = _stage, elapsedMs = _elapsed.ElapsedMilliseconds, state = details }, Id, exception);
        Log.Flush();
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _finished) == 0)
            Finish("abandoned", new InvalidOperationException("Operation ended without a result."), null);
        Log.SetOperation(_previous);
    }
}
