using System.Diagnostics;
using System.Globalization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ProcessAudioQaTarget;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        TargetOptions options = TargetOptions.Parse(args);
        using WaveOutEvent? output = options.Silent ? null : CreateTone(options.Frequency);
        output?.Play();

        if (options.NoWindow)
        {
            LaunchChild(options);
            Thread.Sleep(options.Duration);
            return;
        }

        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = $"Process Audio QA - {options.Frequency:0.##} Hz - PID {Environment.ProcessId}",
            Width = 480,
            Height = 150,
            StartPosition = FormStartPosition.CenterScreen,
        };
        form.Controls.Add(new Label
        {
            AutoSize = true,
            Left = 20,
            Top = 30,
            Text = options.Silent
                ? $"Silent parent process, PID {Environment.ProcessId}"
                : $"Playing {options.Frequency:0.##} Hz, PID {Environment.ProcessId}",
        });

        var timer = new System.Windows.Forms.Timer
        {
            Interval = (int)Math.Clamp(options.Duration.TotalMilliseconds, 1, int.MaxValue),
        };
        timer.Tick += (_, _) => form.Close();
        form.Shown += (_, _) =>
        {
            LaunchChild(options);
            timer.Start();
        };
        Application.Run(form);
    }

    private static WaveOutEvent CreateTone(double frequency)
    {
        var signal = new SignalGenerator(48_000, 2)
        {
            Frequency = frequency,
            Gain = 0.12,
            Type = SignalGeneratorType.Sin,
        };
        var output = new WaveOutEvent { DesiredLatency = 100 };
        output.Init(signal);
        return output;
    }

    private static void LaunchChild(TargetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ChildExecutable))
            return;

        string childArguments = FormattableString.Invariant(
            $"--frequency {options.ChildFrequency} --duration {options.Duration.TotalSeconds}") +
            (options.ChildNoWindow ? " --no-window" : string.Empty);
        Process.Start(new ProcessStartInfo
        {
            FileName = options.ChildExecutable,
            Arguments = childArguments,
            UseShellExecute = false,
        });
    }
}

internal sealed class TargetOptions
{
    internal double Frequency { get; private init; } = 440;
    internal TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(30);
    internal bool NoWindow { get; private init; }
    internal bool Silent { get; private init; }
    internal string? ChildExecutable { get; private init; }
    internal double ChildFrequency { get; private init; } = 880;
    internal bool ChildNoWindow { get; private init; }

    internal static TargetOptions Parse(string[] args)
    {
        double frequency = 440;
        TimeSpan duration = TimeSpan.FromSeconds(30);
        bool noWindow = false;
        bool silent = false;
        string? childExecutable = null;
        double childFrequency = 880;
        bool childNoWindow = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--frequency":
                    frequency = double.Parse(
                        NextValue(args, ref i),
                        CultureInfo.InvariantCulture);
                    break;
                case "--duration":
                    duration = TimeSpan.FromSeconds(double.Parse(
                        NextValue(args, ref i),
                        CultureInfo.InvariantCulture));
                    break;
                case "--no-window":
                    noWindow = true;
                    break;
                case "--silent":
                    silent = true;
                    break;
                case "--child":
                    childExecutable = Path.GetFullPath(NextValue(args, ref i));
                    break;
                case "--child-frequency":
                    childFrequency = double.Parse(
                        NextValue(args, ref i),
                        CultureInfo.InvariantCulture);
                    break;
                case "--child-no-window":
                    childNoWindow = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new TargetOptions
        {
            Frequency = frequency,
            Duration = duration,
            NoWindow = noWindow,
            Silent = silent,
            ChildExecutable = childExecutable,
            ChildFrequency = childFrequency,
            ChildNoWindow = childNoWindow,
        };
    }

    private static string NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {args[index]}");
        index++;
        return args[index];
    }
}
