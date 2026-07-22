using System.IO;

namespace Captail;

public static class Log
{
    private static readonly Lock _lock = new();
    private static StreamWriter? _writer;
    private static int _pendingLines;
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Captail", "log.txt");

    static Log() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();

    public static void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                _writer ??= CreateWriter();
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                _pendingLines++;
                if (_pendingLines >= 32 || IsUrgent(message))
                {
                    _writer.Flush();
                    _pendingLines = 0;
                }
            }
            catch
            {
                _writer?.Dispose();
                _writer = null;
                _pendingLines = 0;
            }
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _pendingLines = 0;
        }
    }

    private static StreamWriter CreateWriter()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        return new StreamWriter(new FileStream(
            Path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.SequentialScan))
        {
            AutoFlush = false,
        };
    }

    private static bool IsUrgent(string message) =>
        message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("crash", StringComparison.OrdinalIgnoreCase);
}
