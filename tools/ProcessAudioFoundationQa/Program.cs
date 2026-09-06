using Captail;
using Captail.Interop;
using System.Text.Json;

namespace ProcessAudioFoundationQa;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--overlay-accent"))
            {
                Run("notification uses selected accent", NotificationUsesSelectedAccent);
                return 0;
            }
            Run("normalized executable matching", NormalizedExecutableMatching);
            Run("independent same-name roots", IndependentSameNameRoots);
            Run("same-name descendant deduplication", SameNameDescendantDeduplication);
            Run("same-track ancestor deduplication", SameTrackAncestorDeduplication);
            Run("PID reuse parent validation", PidReuseBreaksParentEdge);
            Run("reconciler restart lifecycle", ReconcilerRestartLifecycle);
            Run("reconciler overlapping roots", ReconcilerOverlappingRoots);
            Run("reconciler failure isolation", ReconcilerFailureIsolation);
            Run("native failure visibility and recovery", NativeFailureVisibilityAndRecovery);
            Run("reconciler thread ownership", ReconcilerThreadOwnership);
            Run("reconciler clean shutdown", ReconcilerCleanShutdown);
            Run("advanced config normalization", AdvancedConfigNormalization);
            Run("config clone copy and equality", ConfigCloneCopyAndEquality);
            Run("old config remains simple", OldConfigRemainsSimple);
            Run("routing across tracks", RoutingAcrossTracks);
            Run("mixer bits and encoder continuity", MixerBitsAndEncoderContinuity);
            Run("simple audio topology regression", SimpleAudioTopologyRegression);
            Run("detected game audio routing", DetectedGameAudioRouting);
            Run("separate game identity restriction", SeparateGameIdentityRestriction);
            Run("replay recording mode classification", ReplayRecordingModeClassification);
            Run("polling reacquisition cadence", PollingReacquisitionCadence);
            Run("process audio health notifications", ProcessAudioHealthNotifications);
            Run("advanced capability model", AdvancedCapabilityModel);
            Run("advanced unavailable reasons", AdvancedUnavailableReasons);
            Run("advanced diagnostics privacy", AdvancedDiagnosticsPrivacy);
            Run("audio routing format limits", AudioRoutingFormatLimits);
            Run("audio process group priority", AudioProcessGroupPriority);
            Run("audio route view refresh stability", AudioRouteViewRefreshStability);
            Run("process icon loading and caching", ProcessIconLoadingAndCaching);
            Run("game catalog matching", GameCatalogMatching);
            Run("ambiguous game executable fallback", AmbiguousGameExecutableFallback);
            Run("friendly replay game folders", FriendlyReplayGameFolders);
            Run("replay audio labels come from file metadata", ReplayAudioLabelsComeFromMetadata);
            Console.WriteLine($"PASS {_passed} process audio foundation tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void NormalizedExecutableMatching()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 0, "Discord.exe"));
        ProcessNode[] roots = snapshot
            .SelectIndependentRoots([@"C:\Apps\DISCORD.EXE"])
            .ToArray();
        Equal(1, roots.Length);
        Equal(10u, roots[0].Identity.ProcessId);
    }

    private static void IndependentSameNameRoots()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 1, "browser.exe"),
            Node(20, 200, 1, "browser.exe"));
        ProcessNode[] roots = snapshot.SelectIndependentRoots(["browser.exe"]).ToArray();
        SequenceEqual([10u, 20u], roots.Select(root => root.Identity.ProcessId));
    }

    private static void SameNameDescendantDeduplication()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 1, "browser.exe"),
            Node(11, 110, 10, "browser.exe"),
            Node(12, 120, 11, "browser.exe"));
        ProcessNode[] roots = snapshot.SelectIndependentRoots(["browser.exe"]).ToArray();
        SequenceEqual([10u], roots.Select(root => root.Identity.ProcessId));
    }

    private static void SameTrackAncestorDeduplication()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 1, "launcher.exe"),
            Node(11, 110, 10, "game.exe"),
            Node(12, 120, 11, "helper.exe"));
        ProcessNode[] roots = snapshot
            .SelectIndependentRoots(["helper.exe", "game.exe", "launcher.exe"])
            .ToArray();
        SequenceEqual([10u], roots.Select(root => root.Identity.ProcessId));
    }

    private static void PidReuseBreaksParentEdge()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 300, 1, "browser.exe"),
            Node(11, 200, 10, "browser.exe"));
        ProcessNode[] roots = snapshot.SelectIndependentRoots(["browser.exe"]).ToArray();
        SequenceEqual([11u, 10u], roots.Select(root => root.Identity.ProcessId));
    }

    private static void ReconcilerRestartLifecycle()
    {
        var created = new List<ProcessIdentity>();
        var destroyed = new List<nint>();
        nint nextSource = 100;
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) =>
            {
                created.Add(identity);
                return nextSource++;
            },
            destroyed.Add);

        ProcessAudioReconcileResult first = reconciler.Reconcile(
            Snapshot(Node(10, 100, 1, "game.exe")),
            [Target("game.exe", 1)]);
        Equal(1, first.CreatedSources);

        ProcessAudioReconcileResult second = reconciler.Reconcile(
            Snapshot(Node(10, 200, 1, "game.exe")),
            [Target("game.exe", 1)]);
        Equal(1, second.DestroyedSources);
        Equal(1, second.CreatedSources);
        Equal(2, created.Count);
        Equal(1, destroyed.Count);
        Equal(200L, reconciler.ActiveIdentities.Single().CreationTime);
    }

    private static void ReconcilerFailureIsolation()
    {
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) => identity.ProcessId == 10
                ? throw new InvalidOperationException("expected")
                : checked((nint)identity.ProcessId),
            _ => { });
        ProcessAudioReconcileResult result = reconciler.Reconcile(
            Snapshot(
                Node(10, 100, 1, "game.exe"),
                Node(20, 200, 1, "game.exe")),
            [Target("game.exe", 1)]);
        Equal(2, result.DesiredSources);
        Equal(1, result.ActiveSources);
        Equal(1, result.FailedSources);
        Equal(20u, reconciler.ActiveIdentities.Single().ProcessId);
    }

    private static void SeparateGameIdentityRestriction()
    {
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 0, "game.exe"),
            Node(11, 110, 10, "helper.exe"),
            Node(20, 200, 0, "game.exe"));
        var created = new List<ProcessIdentity>();
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) => { created.Add(identity); return (nint)identity.ProcessId; },
            _ => { });
        var target = new ProcessAudioTarget("game.exe", 2) { Identity = new ProcessIdentity(10, 100) };
        Equal(1, reconciler.Reconcile(snapshot, [target]).ActiveSources);
        SequenceEqual([10u], created.Select(identity => identity.ProcessId));
        ProcessSnapshot restarted = Snapshot(Node(10, 300, 0, "game.exe"));
        Equal(0, reconciler.Reconcile(restarted, [target]).ActiveSources);
        Equal(1, reconciler.Reconcile(restarted,
            [target with { Identity = new ProcessIdentity(10, 300) }]).ActiveSources);
        var config = new Config { CaptureSystemAudio = true, SeparateAudioTracks = true };
        Equal(false, ObsReplayEngine.IsAudioRoutingAvailable(config, AdvancedProcessAudioAvailability.SourceUnavailable));
        config.SeparateAudioTracks = false;
        Equal(true, ObsReplayEngine.IsAudioRoutingAvailable(config, AdvancedProcessAudioAvailability.SourceUnavailable));
    }

    private static void ReplayRecordingModeClassification()
    {
        var clip = new ReplayClip("unused.mp4", "Recording_2026-09-06.mp4", null,
            DateTime.MinValue, 1, TimeSpan.FromSeconds(1), null);
        Equal("recording", clip.RecordingMode);
        Equal("replay", (clip with { Name = "Replay_2026-09-06_trimmed.mp4" }).RecordingMode);
        Equal("unknown", (clip with { Name = "renamed.mp4" }).RecordingMode);
    }

    private static void NotificationUsesSelectedAccent()
    {
        var application = new System.Windows.Application();
        ThemeManager.ApplyAccent("violet");
        var overlay = new OverlayNotificationWindow();
        try
        {
            foreach (OverlayTone tone in new[] { OverlayTone.Success, OverlayTone.Neutral })
            {
                overlay.ShowNotification("✓", "Accent QA", "Violet", tone);
                var expected = ((System.Windows.Media.SolidColorBrush)application.Resources["AccentBrush"]).Color;
                var icon = (System.Windows.Controls.TextBlock)overlay.FindName("IconText");
                var actual = ((System.Windows.Media.SolidColorBrush)icon.Foreground).Color;
                Equal(expected, actual);
                ThemeManager.ApplyAccent("blue");
                expected = ((System.Windows.Media.SolidColorBrush)application.Resources["AccentBrush"]).Color;
                actual = ((System.Windows.Media.SolidColorBrush)icon.Foreground).Color;
                Equal(expected, actual);
            }
        }
        finally
        {
            overlay.ClosePermanently();
            application.Shutdown();
        }
    }

    private static void AudioRoutingFormatLimits()
    {
        AudioRoutingFormatCapabilities aac =
            AudioRoutingFormatCapabilities.For("aac");
        Equal("aac", aac.AudioCodec);
        Equal("MP4", aac.Container);
        Equal(6, aac.MaxTracks);

        AudioRoutingFormatCapabilities opus =
            AudioRoutingFormatCapabilities.For("opus");
        Equal("opus", opus.AudioCodec);
        Equal("MKV", opus.Container);
        Equal(6, opus.MaxTracks);
    }

    private static void AudioProcessGroupPriority()
    {
        var process = new ProcessAudioSessionSnapshot(
            "chat.exe",
            "Chat",
            null,
            0,
            false,
            false,
            2);
        var audio = process with
        {
            Peak = 0.4f,
            IsActive = true,
            HasAudioSession = true,
            ProcessCount = 1,
        };
        ProcessAudioSessionSnapshot merged =
            ProcessAudioSessionMonitor.MergeProcesses([process], [audio]).Single();
        Equal(true, merged.HasAudioSession);
        Equal(true, merged.IsActive);
        Equal(2, merged.ProcessCount);

        var item = new ProcessAudioRouteItem(
            "chat.exe",
            "Chat",
            isSelected: false,
            track: 1,
            captureEnabled: true);
        item.UpdateSession(process);
        Equal(2, item.GroupOrder);
        item.UpdateSession(audio);
        Equal(1, item.GroupOrder);
        item.IsSelected = true;
        Equal(0, item.GroupOrder);
        item.IsSelected = false;
        Equal(1, item.GroupOrder);
    }

    private static void AudioRouteViewRefreshStability()
    {
        var item = new ProcessAudioRouteItem(
            "chat.exe",
            "Chat",
            isSelected: false,
            track: 1,
            captureEnabled: true);
        var quiet = new ProcessAudioSessionSnapshot(
            "chat.exe",
            "Chat",
            null,
            0.1f,
            true,
            true,
            1);
        Equal(true, item.UpdateSession(quiet));

        ProcessAudioSessionSnapshot louder = quiet with { Peak = 0.8f };
        Equal(false, item.UpdateSession(louder));

        ProcessAudioSessionSnapshot inactive = louder with
        {
            Peak = 0,
            IsActive = false,
        };
        Equal(true, item.UpdateSession(inactive));
        Equal(false, item.UpdateSession(inactive));
    }

    private static void ProcessIconLoadingAndCaching()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("QA executable path is unavailable.");
        System.Windows.Media.ImageSource? first = ProcessIconProvider
            .GetAsync(executablePath)
            .GetAwaiter()
            .GetResult();
        if (first is null || !first.IsFrozen)
            throw new InvalidOperationException("Executable icon was not loaded and frozen.");

        System.Windows.Media.ImageSource? second = ProcessIconProvider
            .GetAsync(executablePath)
            .GetAwaiter()
            .GetResult();
        Equal(true, ReferenceEquals(first, second));
        Equal(
            null,
            ProcessIconProvider.GetAsync(null).GetAwaiter().GetResult());
    }

    private static void ReplayAudioLabelsComeFromMetadata()
    {
        var advanced = new Config
        {
            AudioRoutingMode = "advanced",
            CaptureMicrophone = true,
            AdvancedMicrophoneTrack = 1,
            ProcessAudioRoutes =
            [
                new ProcessAudioRoute
                {
                    Executable = "kingdomcome.exe",
                    Track = 2,
                    Enabled = true,
                },
                new ProcessAudioRoute
                {
                    Executable = "discord.exe",
                    Track = 3,
                    Enabled = false,
                },
            ],
        };
        Equal("Track 1 - Microphone", ObsReplayEngine.BuildAudioTrackName(advanced, 1));
        Equal("Track 2 - kingdomcome", ObsReplayEngine.BuildAudioTrackName(advanced, 2));
        Equal("Track 3", ObsReplayEngine.BuildAudioTrackName(advanced, 3));

        Equal(
            "kingdomcome",
            ClipEditorWindow.AudioLabel(
                new AudioTrackInfo(2, 1, "aac", "Track 2 - kingdomcome", 2),
                2));
        Equal(
            "Mixed audio",
            ClipEditorWindow.AudioLabel(
                new AudioTrackInfo(1, 0, "aac", "Mixed audio", 2),
                1));
        Equal(
            Localization.Format("L.Library.AudioTrackNumber", 1),
            ClipEditorWindow.AudioLabel(
                new AudioTrackInfo(1, 0, "aac", "Captail Audio 1", 2),
                2));
    }

    private static void NativeFailureVisibilityAndRecovery()
    {
        ProcessAudioSourceStatus status = new(
            ProcessAudioSourceState.Capturing,
            0);
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) => checked((nint)identity.ProcessId),
            _ => { },
            readSourceStatus: _ => status);
        ProcessSnapshot snapshot = Snapshot(Node(10, 100, 1, "game.exe"));

        ProcessAudioReconcileResult healthy = reconciler.Reconcile(
            snapshot,
            [Target("game.exe", 1)]);
        Equal(0, healthy.RuntimeFailedSources);

        status = new ProcessAudioSourceStatus(
            ProcessAudioSourceState.ActivationFailed,
            unchecked((int)0x80004005));
        ProcessAudioReconcileResult failed = reconciler.Reconcile(
            snapshot,
            [Target("game.exe", 1)]);
        Equal(1, failed.RuntimeFailedSources);
        Equal(unchecked((int)0x80004005), failed.LastErrorCode);

        status = new ProcessAudioSourceStatus(
            ProcessAudioSourceState.Capturing,
            0);
        ProcessAudioReconcileResult recovered = reconciler.Reconcile(
            snapshot,
            [Target("game.exe", 1)]);
        Equal(1, recovered.RecoveredSources);
    }

    private static void ReconcilerOverlappingRoots()
    {
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) => checked((nint)identity.ProcessId),
            _ => { });
        ProcessAudioReconcileResult result = reconciler.Reconcile(
            Snapshot(
                Node(10, 100, 1, "launcher.exe"),
                Node(11, 110, 10, "game.exe")),
            [Target("launcher.exe", 2), Target("game.exe", 5)]);
        Equal(1, result.ActiveSources);
        Equal(11u, reconciler.ActiveIdentities.Single().ProcessId);
        Equal(5, reconciler.ActiveTracks.Single().Value);
    }

    private static void ReconcilerThreadOwnership()
    {
        using var reconciler = new ProcessAudioReconciler(
            (identity, _) => checked((nint)identity.ProcessId),
            _ => { });
        Exception? failure = Task.Run(() =>
        {
            try
            {
                reconciler.Reconcile(Snapshot(), []);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }).GetAwaiter().GetResult();
        if (failure is not InvalidOperationException)
            throw new InvalidOperationException("A non-owner thread was not rejected.");
    }

    private static void ReconcilerCleanShutdown()
    {
        var destroyed = new List<nint>();
        var reconciler = new ProcessAudioReconciler(
            (identity, _) => checked((nint)identity.ProcessId),
            destroyed.Add);
        reconciler.Reconcile(
            Snapshot(
                Node(10, 100, 1, "game.exe"),
                Node(20, 200, 1, "game.exe")),
            [Target("game.exe", 1)]);
        reconciler.Dispose();
        Equal(2, destroyed.Count);
        Equal(0, reconciler.ActiveIdentities.Count);
    }

    private static void AdvancedConfigNormalization()
    {
        var config = new Config
        {
            AudioRoutingMode = " ADVANCED ",
            AdvancedMicrophoneTrack = 9,
            ProcessAudioRoutes =
            [
                new() { Executable = @"C:\Apps\Discord.EXE", Track = 2 },
                new() { Executable = "discord", Track = 5 },
                new() { Executable = "FIREFOX", Track = 3 },
                new() { Executable = "not-a-process.dll", Track = 1 },
                new() { Executable = "ignored.exe", Track = 7 },
                new() { Executable = "", Track = 1 },
            ],
        };
        config.Normalize();

        Equal("advanced", config.AudioRoutingMode);
        Equal(1, config.AdvancedMicrophoneTrack);
        Equal(2, config.ProcessAudioRoutes.Count);
        Equal("discord.exe", config.ProcessAudioRoutes[0].Executable);
        Equal(2, config.ProcessAudioRoutes[0].Track);
        Equal("firefox.exe", config.ProcessAudioRoutes[1].Executable);
        Equal(3, config.ProcessAudioRoutes[1].Track);
    }

    private static void ConfigCloneCopyAndEquality()
    {
        var source = new Config
        {
            AudioRoutingMode = "advanced",
            AdvancedMicrophoneTrack = 4,
            ProcessAudioRoutes =
            [
                new() { Executable = "Discord.exe", Track = 2 },
                new() { Executable = "firefox", Track = 3 },
            ],
        };
        source.Normalize();
        Config clone = source.Clone();
        if (!source.PipelineEquals(clone))
            throw new InvalidOperationException("A normalized clone changed the pipeline.");
        if (ReferenceEquals(source.ProcessAudioRoutes, clone.ProcessAudioRoutes))
            throw new InvalidOperationException("Route lists were not deep-copied.");
        clone.ProcessAudioRoutes[0].Track = 6;
        if (source.PipelineEquals(clone))
            throw new InvalidOperationException("A route change did not restart the pipeline.");

        clone.CopyFrom(source);
        clone.AdvancedMicrophoneTrack = 5;
        if (source.PipelineEquals(clone))
            throw new InvalidOperationException("A microphone route change was ignored.");

        clone.CopyFrom(source);
        clone.AudioRoutingMode = "simple";
        if (source.PipelineEquals(clone))
            throw new InvalidOperationException("A routing-mode change was ignored.");
    }

    private static void OldConfigRemainsSimple()
    {
        const string json = """
            {
              "CaptureSystemAudio": true,
              "CaptureMicrophone": true,
              "SeparateAudioTracks": true
            }
            """;
        Config config = JsonSerializer.Deserialize<Config>(json)
            ?? throw new InvalidOperationException("Old config did not deserialize.");
        config.Normalize();
        Equal("simple", config.AudioRoutingMode);
        Equal(0, config.ProcessAudioRoutes.Count);
        Equal(1, config.AdvancedMicrophoneTrack);
        Equal(3, ObsReplayEngine.AudioTrackCount(config));

        config.ProcessAudioRoutes = null!;
        Config copied = config.Clone();
        Equal(0, copied.ProcessAudioRoutes.Count);
    }

    private static void RoutingAcrossTracks()
    {
        var created = new List<(uint ProcessId, int Track)>();
        var destroyed = new List<nint>();
        using var reconciler = new ProcessAudioReconciler(
            (identity, track) =>
            {
                created.Add((identity.ProcessId, track));
                return checked((nint)identity.ProcessId);
            },
            destroyed.Add);
        ProcessSnapshot snapshot = Snapshot(
            Node(10, 100, 1, "discord.exe"),
            Node(20, 200, 1, "firefox.exe"),
            Node(30, 300, 1, "chrome.exe"),
            Node(31, 310, 30, "chrome.exe"),
            Node(40, 400, 1, "chrome.exe"));
        ProcessAudioReconcileResult result = reconciler.Reconcile(
            snapshot,
            [
                Target("discord.exe", 2),
                Target("firefox.exe", 2),
                Target("chrome.exe", 5),
            ]);
        Equal(4, result.ActiveSources);
        SequenceEqual(
            [(10u, 2), (20u, 2), (30u, 5), (40u, 5)],
            created);

        ProcessAudioReconcileResult changed = reconciler.Reconcile(
            snapshot,
            [
                Target("discord.exe", 2),
                Target("firefox.exe", 4),
                Target("chrome.exe", 5),
            ]);
        Equal(1, changed.DestroyedSources);
        Equal(1, changed.CreatedSources);
        Equal(1, destroyed.Count);
        Equal(4, reconciler.ActiveTracks.Single(item =>
            item.Key.ProcessId == 20).Value);
    }

    private static void MixerBitsAndEncoderContinuity()
    {
        Equal(1u, ObsReplayEngine.MixerBit(1));
        Equal(2u, ObsReplayEngine.MixerBit(2));
        Equal(32u, ObsReplayEngine.MixerBit(6));

        var config = new Config
        {
            AudioRoutingMode = "advanced",
            CaptureMicrophone = true,
            AdvancedMicrophoneTrack = 6,
            ProcessAudioRoutes =
            [
                new() { Executable = "game.exe", Track = 1 },
                new() { Executable = "chat.exe", Track = 4 },
            ],
        };
        config.Normalize();
        Equal(6, ObsReplayEngine.AudioTrackCount(config));

        config.CaptureMicrophone = false;
        Equal(4, ObsReplayEngine.AudioTrackCount(config));
    }

    private static void SimpleAudioTopologyRegression()
    {
        var config = new Config { AudioRoutingMode = "simple" };
        config.CaptureSystemAudio = false;
        config.CaptureMicrophone = false;
        Equal(1, ObsReplayEngine.AudioTrackCount(config));
        config.CaptureSystemAudio = true;
        Equal(1, ObsReplayEngine.AudioTrackCount(config));
        config.CaptureMicrophone = true;
        Equal(1, ObsReplayEngine.AudioTrackCount(config));
        config.SeparateAudioTracks = true;
        Equal(3, ObsReplayEngine.AudioTrackCount(config));
        Equal("System", ObsReplayEngine.BuildAudioTrackName(config, 1));
        Equal("Game", ObsReplayEngine.BuildAudioTrackName(config, 2));
        Equal("Microphone", ObsReplayEngine.BuildAudioTrackName(config, 3));
        config.CaptureMicrophone = false;
        Equal(2, ObsReplayEngine.AudioTrackCount(config));
        config.CaptureSystemAudio = false;
        config.CaptureMicrophone = true;
        Equal(1, ObsReplayEngine.AudioTrackCount(config));
        Equal("Microphone", ObsReplayEngine.BuildAudioTrackName(config, 1));
    }

    private static void PollingReacquisitionCadence()
    {
        var cadence = new ProcessAudioPollCadence();
        Equal(TimeSpan.FromMilliseconds(1000), cadence.NextInterval);
        cadence.Observe(Result(desired: 0));
        Equal(TimeSpan.FromMilliseconds(1000), cadence.NextInterval);
        cadence.Observe(Result(desired: 1, created: 1));
        Equal(TimeSpan.FromMilliseconds(250), cadence.NextInterval);
        for (int poll = 0; poll < 7; poll++)
        {
            cadence.Observe(Result(desired: 1));
            Equal(TimeSpan.FromMilliseconds(250), cadence.NextInterval);
        }
        cadence.Observe(Result(desired: 1));
        Equal(TimeSpan.FromMilliseconds(1000), cadence.NextInterval);

        cadence.Observe(Result(desired: 0, destroyed: 1));
        Equal(TimeSpan.FromMilliseconds(250), cadence.NextInterval);
    }

    private static void DetectedGameAudioRouting()
    {
        var config = new Config
        {
            CaptureSource = "game",
            CaptureSystemAudio = true,
            CaptureMicrophone = true,
            SeparateAudioTracks = true,
            AudioRoutingMode = "simple",
        };
        Equal(
            true,
            ObsReplayEngine.UsesDetectedGameAudio(
                config,
                AdvancedProcessAudioAvailability.Available));
        Equal(
            true,
            ObsReplayEngine.ShouldMonitorProcessAudio(
                config,
                AdvancedProcessAudioAvailability.Available));
        ProcessAudioTarget target = ObsReplayEngine.BuildProcessAudioTargets(
                config,
                "peak.exe",
                AdvancedProcessAudioAvailability.Available)
            .Single();
        Equal("peak.exe", target.Executable);
        Equal(2, target.Track);

        config.CaptureSource = "desktop";
        Equal(
            true,
            ObsReplayEngine.UsesDetectedGameAudio(
                config,
                AdvancedProcessAudioAvailability.Available));
        Equal(
            1,
            ObsReplayEngine.BuildProcessAudioTargets(
                    config,
                    "peak.exe",
                    AdvancedProcessAudioAvailability.Available)
                .Count);

        config.CaptureSource = "game";
        Equal(
            false,
            ObsReplayEngine.UsesDetectedGameAudio(
                config,
                AdvancedProcessAudioAvailability.SourceUnavailable));

        config.AudioRoutingMode = "advanced";
        config.ProcessAudioRoutes =
        [
            new ProcessAudioRoute
            {
                Executable = "Discord.exe",
                Track = 3,
            },
        ];
        ProcessAudioTarget advancedTarget =
            ObsReplayEngine.BuildProcessAudioTargets(
                    config,
                    "peak.exe",
                    AdvancedProcessAudioAvailability.Available)
                .Single();
        Equal("Discord.exe", advancedTarget.Executable);
        Equal(3, advancedTarget.Track);
    }

    private static void ProcessAudioHealthNotifications()
    {
        var health = new ProcessAudioHealthTracker();
        var failed = Result(desired: 1) with
        {
            RuntimeFailedSources = 1,
            LastErrorCode = unchecked((int)0x80004005),
        };

        Equal(0, health.Observe(failed).Count);
        Equal(0, health.Observe(failed).Count);
        ProcessAudioMonitorEvent failure = health.Observe(failed).Single();
        Equal(ProcessAudioMonitorEventKind.PersistentFailure, failure.Kind);
        Equal(1, failure.Count);
        Equal(0, health.Observe(failed).Count);

        ProcessAudioMonitorEvent recovered = health.Observe(
            Result(desired: 1) with { RecoveredSources = 1 }).Single();
        Equal(ProcessAudioMonitorEventKind.Recovered, recovered.Kind);

        var conflict = Result(desired: 2) with { ConflictingSources = 1 };
        ProcessAudioMonitorEvent conflictEvent = health.Observe(conflict).Single();
        Equal(ProcessAudioMonitorEventKind.RoutingConflict, conflictEvent.Kind);
        Equal(0, health.Observe(conflict).Count);
        Equal(0, health.Observe(Result(desired: 2)).Count);
        Equal(1, health.Observe(conflict).Count);
    }

    private static void AdvancedCapabilityModel()
    {
        Equal(
            AdvancedProcessAudioAvailability.UnsupportedWindowsVersion,
            ObsReplayEngine.DetectProcessAudioAvailability(
                new Version(10, 0, 19040),
                sourceRegistered: true));
        Equal(
            AdvancedProcessAudioAvailability.SourceUnavailable,
            ObsReplayEngine.DetectProcessAudioAvailability(
                new Version(10, 0, 19041),
                sourceRegistered: false));
        Equal(
            AdvancedProcessAudioAvailability.Available,
            ObsReplayEngine.DetectProcessAudioAvailability(
                new Version(10, 0, 19041),
                sourceRegistered: true));

        var simple = new Config { AudioRoutingMode = "simple" };
        var advanced = new Config { AudioRoutingMode = "advanced" };
        if (!ObsReplayEngine.IsAudioRoutingAvailable(
                simple,
                AdvancedProcessAudioAvailability.UnsupportedWindowsVersion))
        {
            throw new InvalidOperationException(
                "Unsupported process loopback disabled simple audio.");
        }
        if (ObsReplayEngine.IsAudioRoutingAvailable(
                advanced,
                AdvancedProcessAudioAvailability.SourceUnavailable))
        {
            throw new InvalidOperationException(
                "Advanced audio was enabled without its OBS source.");
        }
    }

    private static void AdvancedUnavailableReasons()
    {
        string unsupported = new AdvancedProcessAudioUnavailableException(
            AdvancedProcessAudioAvailability.UnsupportedWindowsVersion).Message;
        string missing = new AdvancedProcessAudioUnavailableException(
            AdvancedProcessAudioAvailability.SourceUnavailable).Message;
        if (string.Equals(unsupported, missing, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Advanced audio availability reasons were collapsed into one message.");
        }
    }

    private static void AdvancedDiagnosticsPrivacy()
    {
        var config = new Config
        {
            AudioRoutingMode = "advanced",
            ProcessAudioRoutes =
            [
                new() { Executable = "private-chat.exe", Track = 2 },
                new() { Executable = "private-game.exe", Track = 4 },
            ],
        };
        config.Normalize();
        string summary = BugReportInfo.FormatAudio(config);
        if (summary.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
            !summary.Contains("2 rules", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Advanced diagnostics exposed a configured executable.");
        }
    }

    private static void GameCatalogMatching()
    {
        Equal("PEAK", GameCatalog.ExactNameForExecutable(
            @"C:\Games\PEAK\peak.exe"));
        if (GameCatalog.Count < 10_000)
        {
            throw new InvalidOperationException(
                $"Expected full game catalog; found {GameCatalog.Count} entries.");
        }
    }

    private static void AmbiguousGameExecutableFallback()
    {
        Equal<string?>(null, GameCatalog.ExactNameForExecutable("game.exe"));
    }

    private static void FriendlyReplayGameFolders()
    {
        var config = new Config
        {
            OrganizeReplaysByGame = true,
            OutputDirectory = @"C:\Captail",
        };
        Equal(
            @"C:\Captail\PEAK",
            ReplayPaths.CaptureDirectory(config, @"C:\Games\PEAK\peak.exe"));
        Equal(
            @"C:\Captail\game",
            ReplayPaths.CaptureDirectory(config, @"C:\Games\Unknown\game.exe"));
    }

    private static ProcessAudioReconcileResult Result(
        int desired,
        int created = 0,
        int destroyed = 0) =>
        new(desired, desired, created, destroyed, 0);

    private static ProcessAudioTarget Target(string executable, int track) =>
        new(executable, track);

    private static ProcessNode Node(
        uint processId,
        long creationTime,
        uint parentProcessId,
        string executable) =>
        new(new ProcessIdentity(processId, creationTime), parentProcessId, executable);

    private static ProcessSnapshot Snapshot(params ProcessNode[] nodes) =>
        ProcessSnapshot.CreateSynthetic(nodes);

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; found {actual}.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}]; " +
                $"found [{string.Join(", ", actual)}].");
        }
    }
}
