using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Captail;

internal static class Program
{
    internal static readonly string Root = Path.Combine(Path.GetTempPath(), "Captail-DiagnosticsQa", Guid.NewGuid().ToString("N"));
    private static int _passed;

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await Run("dated metadata, multiline stacks and immediate error flush", () =>
            {
                string path = Fixture("format");
                var file = new DiagnosticLogFile(path);
                file.Write(DiagnosticLevel.Error, "Replay", "failure\nstack-frame", "abc123");
                string[] lines = Read(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Check(lines.Length == 3 && lines.All(line => Regex.IsMatch(line,
                    @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2} \[\w+\] \[\w+\] session=[a-f0-9]+ pid=\d+ tid=\d+")));
                Check(lines.Skip(1).All(line => line.Contains("op=abc123")));
                file.Close();
                file.Close();
            });
            await Run("single low-volume event is flushed without another write", async () =>
            {
                string path = Fixture("timer");
                var file = new DiagnosticLogFile(path);
                file.Write(DiagnosticLevel.Info, "Test", "low-volume-event");
                await Task.Delay(1500);
                Check(Read(path).Contains("low-volume-event"));
                file.Close();
            });
            await Run("parallel callers keep every complete record", async () =>
            {
                string path = Fixture("parallel");
                var file = new DiagnosticLogFile(path);
                await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
                {
                    for (int index = 0; index < 100; index++)
                        file.Write(DiagnosticLevel.Info, "Test", $"record={worker}:{index}");
                })));
                file.Close();
                string[] records = Read(path).Split('\n').Where(line => line.Contains("record=")).ToArray();
                Check(records.Length == 800 && records.Distinct().Count() == 800);
            });
            await Run("rotation keeps bounded history and newest event", () =>
            {
                string path = Fixture("rotation");
                var file = new DiagnosticLogFile(path, 4096, 2);
                for (int index = 0; index < 90; index++)
                    file.Write(DiagnosticLevel.Info, "Test", $"record={index} " + new string('x', 220));
                file.Close();
                string[] files = Directory.GetFiles(Path.GetDirectoryName(path)!, "log.txt*");
                Check(files.Length == 3 && files.All(candidate => new FileInfo(candidate).Length < 5000));
                Check(files.Select(Read).Any(text => text.Contains("record=89 ")));
                Check(!files.Select(Read).Any(text => text.Contains("record=0 ")));
            });
            await Run("second writer isolates log instead of corrupting active file", () =>
            {
                string path = Fixture("sharing");
                var first = new DiagnosticLogFile(path);
                var second = new DiagnosticLogFile(path);
                first.Write(DiagnosticLevel.Error, "Test", "first-owner");
                second.Write(DiagnosticLevel.Error, "Test", "second-owner");
                Check(first.ActivePath != second.ActivePath);
                Check(Read(first.ActivePath).Contains("first-owner") && !Read(first.ActivePath).Contains("second-owner"));
                Check(Read(second.ActivePath).Contains("second-owner"));
                first.Close();
                second.Close();
            });
            await Run("unwritable destination never throws and reports recovery", () =>
            {
                string folder = Path.Combine(Root, "blocked");
                File.WriteAllText(folder, "blocker");
                string path = Path.Combine(folder, "log.txt");
                var file = new DiagnosticLogFile(path);
                file.Write(DiagnosticLevel.Error, "Test", "lost-event");
                File.Delete(folder);
                file.Write(DiagnosticLevel.Error, "Test", "recovered-event");
                Check(Read(path).Contains("lostEvents=1") && Read(path).Contains("recovered-event"));
                file.Close();
            });
            await Run("old process logs are pruned without deleting active history", () =>
            {
                string path = Fixture("process-retention");
                string directory = Path.GetDirectoryName(path)!;
                for (int index = 101; index <= 108; index++)
                {
                    string candidate = Path.Combine(directory, $"log-{index}.txt");
                    File.WriteAllText(candidate, "old process log");
                    File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(index - 110));
                }
                string active = Path.Combine(directory, "log-101.txt");
                File.WriteAllText(active + ".1", "active archived evidence");
                File.SetLastWriteTimeUtc(active + ".1", DateTime.UtcNow.AddDays(-10));
                using var held = new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.Read);
                var file = new DiagnosticLogFile(path);
                file.Write(DiagnosticLevel.Error, "Test", "retention-finished");
                Check(File.Exists(active + ".1") && !File.Exists(Path.Combine(directory, "log-102.txt")));
                Check(File.Exists(Path.Combine(directory, "log-108.txt")));
                file.Close();
            });
            await Run("new and legacy public excerpts redact structured secrets and native text", () =>
            {
                string path = Fixture("sanitize");
                var file = new DiagnosticLogFile(path);
                file.Write(DiagnosticLevel.Error, "Replay", "replay.save.failed " + JsonSerializer.Serialize(new
                {
                    stage = "probe-and-remux", token = "private token with spaces", SystemAudioDeviceId = "private headset",
                    path = @"C:\Users\private-person\Videos\private.mp4", email = "private@example.com",
                    endpoint = "10.9.8.7:443", title = "private window", password = "secret-value", api_key = 99887766,
                }), "abc123");
                file.Write(DiagnosticLevel.Error, "OBS", "libobs[100]: uncontrolled native text");
                file.Close();
                string excerpt = DiagnosticLogExporter.CreateExcerpt(path);
                foreach (string secret in new[] { "private token", "private headset", "private-person", "private.mp4", "private@example.com", "10.9.8.7", "private window", "secret-value", "99887766", "uncontrolled native text" })
                    Check(!excerpt.Contains(secret), "Sensitive value survived: " + secret);
                Check(excerpt.Contains("replay.save.failed") && excerpt.Contains("probe-and-remux") && excerpt.Contains("op=abc123"));
                File.WriteAllText(path, "17:00:00.000 Replay save failed: C:\\Users\\private-person\\clip.mp4\n17:00:00.001 libobs[100]: uncontrolled old native text");
                excerpt = DiagnosticLogExporter.CreateExcerpt(path);
                Check(excerpt.Contains("Replay save failed") && !excerpt.Contains("private-person") && !excerpt.Contains("uncontrolled"));
            });
            await Run("incident survives later health noise and file rotation in excerpt", () =>
            {
                string path = Fixture("incident");
                File.WriteAllText(path + ".1", "2026-10-15T12:00:00.000+02:00 [ERROR] [DiskReplay] session=aaa pid=1 tid=2 op=abc123 disk.snapshot-timeout limitSeconds=15\n");
                File.WriteAllLines(path, Enumerable.Range(0, 150).Select(index =>
                    $"2026-10-15T12:01:00.000+02:00 [INFO] [Capture] session=aaa pid=1 tid=2 capture.state sample={index}"));
                string excerpt = DiagnosticLogExporter.CreateExcerpt(path);
                Check(excerpt.Contains("disk.snapshot-timeout") && excerpt.Contains("limitSeconds=15") && excerpt.Length <= 1800);
            });
            await Run("operation correlation flows across tasks, restores parent and flushes failure", async () =>
            {
                using (var parent = Log.BeginOperation("qa.parent"))
                {
                    string parentId = Log.OperationId!;
                    await Task.Run(() => Check(Log.OperationId == parentId));
                    using (var child = Log.BeginOperation("qa.child"))
                    {
                        child.Stage("wait-snapshot", new { limitSeconds = 15 });
                        child.Fail(new TimeoutException("injected timeout"));
                    }
                    Check(Log.OperationId == parentId);
                    parent.Complete();
                }
                Check(Log.OperationId is null);
                string text = Read(AppDataPaths.LogFile);
                Check(text.Contains("qa.child.failed") && text.Contains("wait-snapshot") && text.Contains("injected timeout"));
            });
            await Run("zero failure counters are informational and actual failures are errors", () =>
            {
                Log.Write("counter-fixture-normal: failed=0, runtimeFailed=0");
                Log.Write("counter-fixture-positive: failed=0, runtimeFailed=1");
                Log.Flush();
                string text = Read(AppDataPaths.LogFile);
                Check(Regex.IsMatch(text, @"\[INFO\].*counter-fixture-normal:"));
                Check(Regex.IsMatch(text, @"\[ERROR\].*counter-fixture-positive:"));
            });
            await Run("native noise is counted while native errors remain intact", () =>
            {
                for (int index = 0; index < 100; index++)
                    Log.Native(400, "Audio timestamp diagnostic-fixture=" + index);
                Log.Native(100, "Audio timestamp diagnostic-critical-error");
                Log.Close();
                string text = Read(AppDataPaths.LogFile);
                Check(Regex.Matches(text, "diagnostic-fixture=").Count == 1);
                Check(text.Contains("diagnostic-critical-error") && text.Contains("\"suppressed\":99"));
            });
            Log.Close();
            Console.WriteLine($"PASS: {_passed} diagnostic logging contracts. Evidence: {Root}");
            return 0;
        }
        catch (Exception exception)
        {
            Log.Close();
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("Evidence: " + Root);
            return 1;
        }
    }

    private static async Task Run(string name, Action action) => await Run(name, () => { action(); return Task.CompletedTask; });
    private static async Task Run(string name, Func<Task> action)
    {
        await action();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Check(bool passed, string message = "Diagnostic contract failed.")
    {
        if (!passed) throw new InvalidOperationException(message);
    }
    private static string Fixture(string name)
    {
        string directory = Path.Combine(Root, name);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "log.txt");
    }
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

namespace Captail
{
    internal static class AppDataPaths
    {
        internal static string LogFile => Path.Combine(Program.Root, "operations.log");
    }
}
