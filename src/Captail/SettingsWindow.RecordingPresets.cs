using System.Windows;
using System.Windows.Controls;
using System.Globalization;

namespace Captail;

public partial class SettingsWindow
{
    private List<RecordingPreset> _pendingRecordingPresets = [];
    private string _pendingRecordingPresetId = "";
    private int _pendingBufferSeconds = 300;

    private void LoadRecordingSettingsControls(Config source)
    {
        _pendingRecordingPresets = source.RecordingPresets.Select(preset => preset.Clone()).ToList();
        _pendingRecordingPresetId = source.SelectedRecordingPresetId;
        AdvancedSettingsBox.IsChecked = source.AdvancedSettingsEnabled;
        DiskReplayBufferBox.IsChecked = source.DiskReplayBufferEnabled;
        _pendingBufferSeconds = source.BufferSeconds;
        CustomBufferMinutesBox.Text = FormatBufferMinutes(source.BufferSeconds);
        SyncBufferOptions();
        EncoderTuningOptions tuning = source.EncoderTuning;
        PopulateOptions(NvencPresetBox, Enumerable.Range(1, 7).Select(n => ($"p{n}", $"P{n}")));
        PopulateOptions(AmfPresetBox,
        [
            ("speed", "Speed"), ("balanced", "Balanced"), ("quality", "Quality"),
        ]);
        PopulateOptions(QsvPresetBox, Enumerable.Range(1, 7).Select(n => ($"tu{n}", $"TU{n}")));
        PopulateOptions(KeyframeIntervalBox, Enumerable.Range(1, 10).Select(n =>
            (n.ToString(), Localization.Format("L.Advanced.SecondsFormat", n))), "0");
        PopulateOptions(BFramesBox, Enumerable.Range(0, 5).Select(n => (n.ToString(), n.ToString())), "-1");
        PopulateOptions(MultipassBox,
        [
            ("disabled", Localization.Text("L.Advanced.OnePass")),
            ("qres", Localization.Text("L.Advanced.QuarterPass")),
            ("fullres", Localization.Text("L.Advanced.FullPass")),
        ], autoValue: null);
        PopulateOptions(AdaptiveQuantizationBox,
        [
            ("on", Localization.Text("L.Advanced.On")),
            ("off", Localization.Text("L.Advanced.Off")),
        ]);
        int[] audioBitrates = [64, 96, 128, 160, 192, 256, 320, 384, 512];
        PopulateOptions(AudioBitrateBox, audioBitrates.Append(source.AudioBitrateKbps).Distinct().Order()
            .Select(n => (n.ToString(), Localization.Format("L.Advanced.KbpsFormat", n))), autoValue: null);
        SelectByTag(RateControlBox, tuning.RateControl);
        ConstantQualitySlider.Value = tuning.ConstantQuality;
        SelectByTag(NvencPresetBox, tuning.NvencPreset);
        SelectByTag(AmfPresetBox, tuning.AmfPreset);
        SelectByTag(QsvPresetBox, tuning.QsvTargetUsage);
        SelectByTag(KeyframeIntervalBox, tuning.KeyframeIntervalSeconds.ToString());
        SelectByTag(BFramesBox, tuning.BFrames.ToString());
        SelectByTag(MultipassBox, tuning.NvencMultipass);
        LookAheadBox.IsChecked = tuning.NvencLookAhead;
        SelectByTag(AdaptiveQuantizationBox, tuning.NvencAdaptiveQuantization);
        SelectByTag(AudioBitrateBox, source.AudioBitrateKbps.ToString());
        RefreshRecordingPresetPicker();
        UpdateAdvancedSettingsState();
        UpdateRecordingPresetState(source);
    }

    private static void PopulateOptions(ComboBox box, IEnumerable<(string Tag, string Label)> choices, string? autoValue = "auto")
    {
        box.Items.Clear();
        if (autoValue is not null)
            box.Items.Add(new ComboBoxItem { Tag = autoValue, Content = Localization.Text("L.Advanced.Auto") });
        foreach ((string tag, string label) in choices)
            box.Items.Add(new ComboBoxItem { Tag = tag, Content = label });
    }

    private void ReadRecordingSettingsControls(Config candidate)
    {
        candidate.AdvancedSettingsEnabled = AdvancedSettingsBox.IsChecked == true;
        candidate.DiskReplayBufferEnabled = DiskReplayBufferBox.IsChecked == true;
        candidate.EncoderTuning = new EncoderTuningOptions
        {
            RateControl = GetSelectedTag(RateControlBox, "cbr"),
            ConstantQuality = (int)Math.Round(ConstantQualitySlider.Value),
            NvencPreset = GetSelectedTag(NvencPresetBox, "auto"),
            AmfPreset = GetSelectedTag(AmfPresetBox, "auto"),
            QsvTargetUsage = GetSelectedTag(QsvPresetBox, "auto"),
            KeyframeIntervalSeconds = GetSelectedInt(KeyframeIntervalBox, 0),
            BFrames = GetSelectedInt(BFramesBox, -1),
            NvencMultipass = GetSelectedTag(MultipassBox, "disabled"),
            NvencLookAhead = LookAheadBox.IsChecked == true,
            NvencAdaptiveQuantization = GetSelectedTag(AdaptiveQuantizationBox, "auto"),
        };
        candidate.AudioBitrateKbps = GetSelectedInt(AudioBitrateBox, candidate.AudioBitrateKbps);
        candidate.RecordingPresets = _pendingRecordingPresets.Select(preset => preset.Clone()).ToList();
        candidate.SelectedRecordingPresetId = _pendingRecordingPresetId;
    }

    private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        UpdateAdvancedSettingsState();
        QueueSettingsDirtyRefresh();
    }

    private void UpdateAdvancedSettingsState()
    {
        if (!IsInitialized || AdvancedSettingsBox is null || RateControlBox is null)
            return;
        bool advanced = AdvancedSettingsBox.IsChecked == true;
        bool replay = GetSelectedRadioTag(DashboardCaptureModeOptions, "replay") == "replay";
        UpdateBufferDurationState();
        DiskReplayBufferRow.Visibility = advanced && replay ? Visibility.Visible : Visibility.Collapsed;
        AdvancedVideoSettings.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        AdvancedAudioSettings.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        bool cqp = GetSelectedTag(RateControlBox, "cbr") == "cqp";
        ConstantQualityRow.Visibility = cqp ? Visibility.Visible : Visibility.Collapsed;
        ConstantQualityValueText.Visibility = cqp ? Visibility.Visible : Visibility.Collapsed;
        BitrateBox.IsEnabled = !cqp;
        BitrateBox.ToolTip = cqp ? Localization.Text("L.Advanced.QualityHint") : null;
        string codec = GetSelectedTag(CodecBox, _config.Codec);
        string? family = _capabilities.Preferred(codec)?.Family;
        // UI-only preview has no live encoder probe. Keep the common controls visible.
        NvencPresetBox.Visibility = family is "amf" or "qsv" ? Visibility.Collapsed : Visibility.Visible;
        NvencPresetBox.IsEnabled = family == "nvenc";
        AmfPresetBox.Visibility = family == "amf" ? Visibility.Visible : Visibility.Collapsed;
        QsvPresetBox.Visibility = family == "qsv" ? Visibility.Visible : Visibility.Collapsed;
        NvencAdvancedSettings.Visibility = family == "nvenc" ? Visibility.Visible : Visibility.Collapsed;
        bool boundedReplay = GetSelectedInt(ReplaySizeLimitBox, 0) > 0;
        bool diskReplay = replay && DiskReplayBufferBox.IsChecked == true;
        BufferLimitHintText.SetResourceReference(TextBlock.TextProperty,
            diskReplay ? "L.Advanced.DiskBufferLimitHint" : "L.Replay.BufferLimitHint");
        BufferLimitHelpText.SetResourceReference(TextBlock.TextProperty,
            diskReplay ? "L.Help.DiskBufferLimit" : "L.Help.BufferLimit");
        BufferLengthHelpText.SetResourceReference(TextBlock.TextProperty,
            diskReplay ? "L.Help.DiskBufferLength" : "L.Help.BufferLength");
        KeyframeIntervalBox.IsEnabled = !boundedReplay && !diskReplay;
        KeyframeLimitHintText.Visibility = boundedReplay || diskReplay ? Visibility.Visible : Visibility.Collapsed;
        KeyframeLimitHintText.Text = Localization.Text(boundedReplay ? "L.Advanced.KeyframesLimited" : "L.Advanced.DiskKeyframesLimited");
        bool hevcCompatibility = family == "nvenc" && codec == "hevc";
        BFramesBox.IsEnabled = !hevcCompatibility;
        BFramesLimitHintText.Visibility = hevcCompatibility ? Visibility.Visible : Visibility.Collapsed;
    }

    private RecordingPreset? SelectedRecordingPreset() =>
        _pendingRecordingPresets.FirstOrDefault(preset => preset.Id == _pendingRecordingPresetId);

    private void RefreshRecordingPresetPicker()
    {
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        try
        {
            RecordingPresetBox.Items.Clear();
            RecordingPresetBox.Items.Add(new ComboBoxItem { Tag = "", Content = Localization.Text(
                _pendingRecordingPresets.Count == 0 ? "L.Preset.Empty" : "L.Preset.Current") });
            RecordingPresetBox.IsEnabled = _pendingRecordingPresets.Count > 0;
            foreach (RecordingPreset preset in _pendingRecordingPresets)
                RecordingPresetBox.Items.Add(new ComboBoxItem { Tag = preset.Id, Content = preset.Name });
            SelectByTag(RecordingPresetBox, _pendingRecordingPresetId);
            RecordingPresetNameBox.Text = SelectedRecordingPreset()?.Name ?? "";
            bool custom = _pendingRecordingPresets.Any(preset => preset.Id == _pendingRecordingPresetId);
            UpdateRecordingPresetButton.IsEnabled = custom;
            DeleteRecordingPresetButton.IsEnabled = custom;
        }
        finally
        {
            _updatingUi = wasUpdating;
        }
    }

    private void UpdateRecordingPresetState(Config pending)
    {
        RecordingPreset? preset = SelectedRecordingPreset();
        RecordingPresetModifiedText.Visibility = preset is not null && !preset.Settings.Matches(pending)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordingPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || !IsInitialized)
            return;
        string id = GetSelectedTag(RecordingPresetBox, "");
        RecordingPreset? preset = _pendingRecordingPresets.FirstOrDefault(item => item.Id == id);
        if (preset is not null && !_capabilities.Supports(preset.Settings.Codec))
        {
            RefreshRecordingPresetPicker();
            ShowError(Localization.Text("L.Error.CodecTitle"), Localization.Text("L.Error.CodecMessage"));
            return;
        }
        Config candidate = CreatePendingConfig();
        _pendingRecordingPresetId = id;
        candidate.SelectedRecordingPresetId = id;
        preset?.Settings.ApplyTo(candidate);
        LoadSettingsControls(candidate);
        QueueSettingsDirtyRefresh();
    }

    private bool TryReadPresetName(out string name, string? exceptId = null)
    {
        name = RecordingPresetNameBox.Text.Trim();
        string candidateName = name;
        if (name.Length is < 1 or > 60 ||
            _pendingRecordingPresets.Any(preset =>
                preset.Id != exceptId && string.Equals(preset.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(Localization.Text("L.Section.Presets"), Localization.Text("L.Preset.NameError"));
            return false;
        }
        return true;
    }

    private void SaveRecordingPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBufferDuration())
            return;
        if (!TryReadPresetName(out string name))
            return;
        if (_pendingRecordingPresets.Count >= 100)
        {
            ShowError(Localization.Text("L.Section.Presets"), Localization.Text("L.Preset.LimitError"));
            return;
        }
        var preset = new RecordingPreset { Name = name, Settings = RecordingSettingsSnapshot.Capture(CreatePendingConfig()) };
        _pendingRecordingPresets.Add(preset);
        _pendingRecordingPresetId = preset.Id;
        RefreshRecordingPresetPicker();
        QueueSettingsDirtyRefresh();
    }

    private void UpdateRecordingPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBufferDuration())
            return;
        RecordingPreset? preset = _pendingRecordingPresets.FirstOrDefault(item => item.Id == _pendingRecordingPresetId);
        if (preset is null || !TryReadPresetName(out string name, preset.Id))
            return;
        preset.Name = name;
        preset.Settings = RecordingSettingsSnapshot.Capture(CreatePendingConfig());
        RefreshRecordingPresetPicker();
        QueueSettingsDirtyRefresh();
    }

    private void DeleteRecordingPreset_Click(object sender, RoutedEventArgs e)
    {
        _pendingRecordingPresets.RemoveAll(preset => preset.Id == _pendingRecordingPresetId);
        _pendingRecordingPresetId = "";
        RefreshRecordingPresetPicker();
        QueueSettingsDirtyRefresh();
    }

    private static string FormatBufferMinutes(int seconds) =>
        (seconds / 60m).ToString("0.####", CultureInfo.CurrentCulture);

    private bool TryReadCustomBufferSeconds(out int seconds)
    {
        seconds = 0;
        if (!decimal.TryParse(CustomBufferMinutesBox.Text.Trim().Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal minutes) ||
            minutes <= 0 || minutes > Config.MaximumBufferSeconds / 60m)
            return false;
        seconds = (int)decimal.Round(minutes * 60m, 0, MidpointRounding.AwayFromZero);
        return seconds is >= Config.MinimumBufferSeconds and <= Config.MaximumBufferSeconds;
    }

    private bool IsBufferDurationInvalid() =>
        AdvancedSettingsBox.IsChecked == true &&
        GetSelectedRadioTag(DashboardCaptureModeOptions, "replay") == "replay" &&
        !TryReadCustomBufferSeconds(out _);

    private void CustomBufferMinutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingUi || !IsInitialized)
            return;
        if (TryReadCustomBufferSeconds(out int seconds))
        {
            _pendingBufferSeconds = seconds;
            SyncBufferOptions();
        }
        UpdateBufferDurationState();
        QueueSettingsDirtyRefresh();
    }

    private void BufferOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || !IsInitialized || sender is not RadioButton button ||
            !int.TryParse(button.Tag?.ToString(), out int seconds))
            return;
        _pendingBufferSeconds = seconds;
        CustomBufferMinutesBox.Text = FormatBufferMinutes(seconds);
        UpdateBufferDurationState();
        QueueSettingsDirtyRefresh();
    }

    private void SyncBufferOptions()
    {
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        try
        {
            foreach (RadioButton button in BufferOptions.Children.OfType<RadioButton>())
                button.IsChecked = button.Tag?.ToString() == _pendingBufferSeconds.ToString(CultureInfo.InvariantCulture);
        }
        finally { _updatingUi = wasUpdating; }
    }

    private void UpdateBufferDurationState()
    {
        bool advanced = AdvancedSettingsBox.IsChecked == true;
        StandardBufferLengthControl.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        CustomBufferLengthControl.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        CustomBufferErrorText.Visibility = IsBufferDurationInvalid() ? Visibility.Visible : Visibility.Collapsed;
        bool fixedChoice = BufferOptions.Children.OfType<RadioButton>().Any(button => button.IsChecked == true);
        CustomBufferSummaryText.Visibility = !advanced && !fixedChoice ? Visibility.Visible : Visibility.Collapsed;
        CustomBufferSummaryText.Text = Localization.Format("L.Advanced.CustomBufferValue", FormatBufferMinutes(_pendingBufferSeconds));
    }

    private bool ValidateBufferDuration()
    {
        UpdateBufferDurationState();
        if (!IsBufferDurationInvalid())
            return true;
        CustomBufferMinutesBox.BringIntoView();
        CustomBufferMinutesBox.Focus();
        return false;
    }
}
