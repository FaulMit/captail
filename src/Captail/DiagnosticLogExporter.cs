using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Captail;

internal static class DiagnosticLogExporter
{
    private const int MaxReadBytes = 128 * 1024;
    private const int MaxExcerptChars = 1800;
    private const int MaxLineChars = 420;
    private const string Header =
        "Automatically sanitized by Captail. Paths, network addresses, " +
        "identifiers, window titles, device names, and secrets were removed.";

    private static readonly Regex PriorityLinePattern = new(
        @"error|fail|exception|crash|watchdog|recover|rejected|unavailable|" +
        @"fallback|ignored|stopped|started|pipeline|capture|encoder|gpu",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LibObsLinePattern = new(
        @"^\d{2}:\d{2}:\d{2}\.\d{3}\s+libobs\[",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SensitiveKeyPattern = new(
        @"\b(authorization|bearer|token|secret|password|api[_-]?key)" +
        @"\b\s*[:=]?\s*(?:Bearer\s+)?[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowOrDevicePattern = new(
        @"\b(window(?:_title)?|title|device(?:_id|_name)?|monitor_id)" +
        @"\s*=\s*(?:""[^""]*""|'[^']*'|[^,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FileUriPattern = new(
        @"\bfile:///\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathPattern = new(
        @"(?<![A-Za-z0-9])(?:(?:\\\\\?\\)?[A-Z]:[\\/]|\\\\)" +
        @"[^""'\r\n<>|:]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(
        @"\bhttps?://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ipv4Pattern = new(
        @"(?<!\d)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}" +
        @"(?:25[0-5]|2[0-4]\d|1?\d?\d)(?::\d{1,5})?(?!\d)",
        RegexOptions.Compiled);
    private static readonly Regex MacPattern = new(
        @"\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SidPattern = new(
        @"\bS-1-(?:\d+-){1,14}\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidPattern = new(
        @"\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-" +
        @"[0-9A-F]{4}-[0-9A-F]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ControlCharactersPattern = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
        RegexOptions.Compiled);

    internal static string CreateExcerpt()
    {
        try
        {
            Log.Flush();
            return CreateExcerpt(Log.Path);
        }
        catch (Exception exception)
        {
            return $"Captail could not prepare a sanitized log excerpt " +
                   $"({exception.GetType().Name}).";
        }
    }

    internal static string CreateExcerpt(string path)
    {
        string tail = ReadTail(path);
        if (string.IsNullOrWhiteSpace(tail))
            return "No recent diagnostic log was available.";

        string[] lines = tail
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !LibObsLinePattern.IsMatch(line))
            .Select(SanitizeLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
            return "No safe recent diagnostic lines were available.";

        int[] selectedIndices = lines
            .Select((line, index) => (line, index))
            .Where(item => PriorityLinePattern.IsMatch(item.line))
            .TakeLast(18)
            .Select(item => item.index)
            .Concat(Enumerable.Range(Math.Max(0, lines.Length - 18),
                Math.Min(18, lines.Length)))
            .Distinct()
            .Order()
            .ToArray();

        int budget = MaxExcerptChars - Header.Length - Environment.NewLine.Length;
        var chosen = new List<string>();
        for (int index = selectedIndices.Length - 1; index >= 0; index--)
        {
            string line = lines[selectedIndices[index]];
            int cost = line.Length + Environment.NewLine.Length;
            if (cost > budget && chosen.Count > 0)
                continue;
            if (cost > budget)
                line = line[..Math.Max(1, budget - 1)] + "…";
            chosen.Add(line);
            budget -= Math.Min(cost, budget);
            if (budget <= 0)
                break;
        }
        chosen.Reverse();
        return Header + Environment.NewLine + string.Join(Environment.NewLine, chosen);
    }

    private static string ReadTail(string path)
    {
        if (!File.Exists(path))
            return "";

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.SequentialScan);
        long offset = Math.Max(0, stream.Length - MaxReadBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        string text = reader.ReadToEnd();
        if (offset == 0)
            return text;

        int firstLineEnd = text.IndexOf('\n');
        return firstLineEnd >= 0 ? text[(firstLineEnd + 1)..] : "";
    }

    private static string SanitizeLine(string line)
    {
        string sanitized = ControlCharactersPattern.Replace(line, "");
        sanitized = SensitiveKeyPattern.Replace(
            sanitized,
            match => $"{match.Groups[1].Value}=<redacted>");
        sanitized = WindowOrDevicePattern.Replace(
            sanitized,
            match => $"{match.Groups[1].Value}=<redacted>");
        sanitized = FileUriPattern.Replace(sanitized, "<path>");
        sanitized = WindowsPathPattern.Replace(sanitized, "<path>");
        sanitized = UrlPattern.Replace(sanitized, "<url>");
        sanitized = EmailPattern.Replace(sanitized, "<email>");
        sanitized = Ipv4Pattern.Replace(sanitized, "<ip>");
        sanitized = MacPattern.Replace(sanitized, "<mac>");
        sanitized = SidPattern.Replace(sanitized, "<sid>");
        sanitized = GuidPattern.Replace(sanitized, "<id>");
        sanitized = ReplaceMachineIdentity(sanitized, Environment.UserName, "<user>");
        sanitized = ReplaceMachineIdentity(
            sanitized,
            Environment.MachineName,
            "<machine>");
        sanitized = sanitized.Trim();
        return sanitized.Length <= MaxLineChars
            ? sanitized
            : sanitized[..(MaxLineChars - 1)] + "…";
    }

    private static string ReplaceMachineIdentity(
        string value,
        string identity,
        string replacement)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length < 3)
            return value;
        return Regex.Replace(
            value,
            $@"\b{Regex.Escape(identity)}\b",
            replacement,
            RegexOptions.IgnoreCase);
    }
}
