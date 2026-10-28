using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>New / edit form for a scheduled job, with the structured schedule picker and an Advanced raw field.</summary>
public partial class CronEditorWindow : Window
{
    private readonly CronEditorRequest _request;
    private readonly bool _isNew;
    private bool _ready;

    public CronEditorResult? Result { get; private set; }

    private static readonly (ScheduleKind Kind, string Label)[] Kinds =
    [
        (ScheduleKind.EveryMinutes, "Every … minutes"),
        (ScheduleKind.EveryHours, "Every … hours"),
        (ScheduleKind.Daily, "Every day at"),
        (ScheduleKind.Weekdays, "Weekdays at"),
        (ScheduleKind.Weekly, "Every week on"),
        (ScheduleKind.InMinutes, "Once, in … minutes"),
        (ScheduleKind.InHours, "Once, in … hours"),
        (ScheduleKind.Advanced, "Advanced (schedule text)")
    ];

    public CronEditorWindow(CronEditorRequest request)
    {
        InitializeComponent();
        _request = request;
        _isNew = request.Existing is null;
        Title = _isNew ? "New scheduled job" : "Edit scheduled job";

        foreach (var (_, label) in Kinds) KindBox.Items.Add(label);
        foreach (var d in Enum.GetValues<DayOfWeek>()) DayBox.Items.Add(d.ToString());
        for (var h = 0; h < 24; h++) HourBox.Items.Add(h.ToString("00", CultureInfo.InvariantCulture));
        for (var m = 0; m < 60; m += 5) MinuteBox.Items.Add(m.ToString("00", CultureInfo.InvariantCulture));
        foreach (var p in request.Profiles) ProfileBox.Items.Add(p);
        ProfileBox.SelectedItem = request.DefaultProfile is { } dp && request.Profiles.Contains(dp) ? dp : request.Profiles.FirstOrDefault();
        ProfileBox.IsEnabled = _isNew && request.Profiles.Count > 1;   // an existing job stays in the profile that owns it

        var job = request.Existing;
        NameBox.Text = job?.Name ?? "";
        PromptBox.Text = job?.Prompt ?? "";
        // A one-shot job has no editable schedule text: show Advanced empty, and leaving it empty keeps the schedule as it is.
        var spec = job is null ? new ScheduleSpec(ScheduleKind.Daily)
            : job.EditableSchedule.Length == 0 ? new ScheduleSpec(ScheduleKind.Advanced, Raw: "")
            : ScheduleSpec.FromText(job.EditableSchedule);
        KindBox.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == spec.Kind);
        AmountBox.Text = spec.Amount.ToString(CultureInfo.InvariantCulture);
        DayBox.SelectedItem = spec.Day.ToString();
        HourBox.SelectedItem = spec.Hour.ToString("00", CultureInfo.InvariantCulture);
        MinuteBox.SelectedItem = (spec.Minute / 5 * 5).ToString("00", CultureInfo.InvariantCulture);
        AdvancedBox.Text = spec.Raw;
        _ready = true;
        ApplyKind();

        _ = LoadTargetsAsync(job?.Deliver);
    }

    private async Task LoadTargetsAsync(string? current)
    {
        DeliverBox.IsEnabled = false;
        var profile = (ProfileBox.SelectedItem as string) ?? _request.Existing?.Profile;
        var targets = await _request.LoadTargets(profile);
        DeliverBox.ItemsSource = targets;
        DeliverBox.SelectedItem = targets.FirstOrDefault(t => t.Id == (string.IsNullOrWhiteSpace(current) ? "local" : current)) ?? targets.FirstOrDefault();
        DeliverBox.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(current) && targets.All(t => t.Id != current))
            DeliverNote.Text = $"This job delivers to “{current}”, which this profile doesn't list now. Saving keeps “{targets.FirstOrDefault()?.Name}” unless you leave delivery unchanged.";
    }

    private ScheduleKind Kind => Kinds[Math.Max(0, KindBox.SelectedIndex)].Kind;

    private void Kind_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyKind(); }
    private void Field_Changed(object sender, RoutedEventArgs e) { if (_ready) UpdatePreview(); }

    private void ApplyKind()
    {
        var k = Kind;
        AmountRow.Visibility = k is ScheduleKind.EveryMinutes or ScheduleKind.EveryHours or ScheduleKind.InMinutes or ScheduleKind.InHours ? Visibility.Visible : Visibility.Collapsed;
        UnitText.Text = k is ScheduleKind.EveryHours or ScheduleKind.InHours ? "hours" : "minutes";
        TimeRow.Visibility = k is ScheduleKind.Daily or ScheduleKind.Weekdays or ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        DayBox.Visibility = k == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        AdvancedRow.Visibility = k == ScheduleKind.Advanced ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private ScheduleSpec CurrentSpec()
    {
        _ = int.TryParse(AmountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount);
        _ = int.TryParse(HourBox.SelectedItem as string, NumberStyles.None, CultureInfo.InvariantCulture, out var hour);
        _ = int.TryParse(MinuteBox.SelectedItem as string, NumberStyles.None, CultureInfo.InvariantCulture, out var minute);
        _ = Enum.TryParse<DayOfWeek>(DayBox.SelectedItem as string, out var day);
        return new ScheduleSpec(Kind, amount, hour, minute, day, AdvancedBox.Text);
    }

    private bool KeepsExistingSchedule(ScheduleSpec spec) =>
        !_isNew && _request.Existing!.EditableSchedule.Length == 0 && spec.Kind == ScheduleKind.Advanced && string.IsNullOrWhiteSpace(spec.Raw);

    private void UpdatePreview()
    {
        var spec = CurrentSpec();
        PreviewText.Text = KeepsExistingSchedule(spec) ? "The job's current schedule stays as it is."
            : spec.Problem() is { } p ? p
            : "Sent to the gateway as: " + spec.ToText();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var spec = CurrentSpec();
        var keeps = KeepsExistingSchedule(spec);
        var schedule = keeps || spec.Problem() is not null ? "" : spec.ToText();
        var deliver = (DeliverBox.SelectedItem as DeliveryTarget)?.Id ?? _request.Existing?.Deliver ?? "local";
        var name = NameBox.Text.Trim();

        var problem = keeps
            ? (string.IsNullOrWhiteSpace(PromptBox.Text) ? "Write what the job should do (the prompt)." : null)
            : spec.Problem() ?? new CronJobDraft(name, PromptBox.Text, schedule, deliver).Problem();
        if (problem is not null) { ProblemText.Text = problem; return; }

        Result = new CronEditorResult(name, PromptBox.Text, schedule, deliver, ProfileBox.SelectedItem as string);
        DialogResult = true;
    }
}
