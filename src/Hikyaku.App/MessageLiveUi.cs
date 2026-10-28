using System.Collections.ObjectModel;
using System.Windows;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One step of the agent's plan, as a checklist line.</summary>
public sealed class PlanStepVM(TodoItem item, int depth)
{
    public string Text => item.Content;
    public Thickness Indent { get; } = new(depth * 16, 0, 0, 0);
    public string Glyph => item.Status switch
    {
        TodoStatus.Completed => "",     // check
        TodoStatus.InProgress => "",    // in progress
        TodoStatus.Cancelled => "",     // crossed out
        _ => ""                         // empty box
    };
    public double Opacity => item.Status is TodoStatus.Completed or TodoStatus.Cancelled ? 0.6 : 1.0;
    public TextDecorationCollection? Decorations => item.Status == TodoStatus.Cancelled ? TextDecorations.Strikethrough : null;
}

/// <summary>
/// Stage 44 — what the agent did for this reply, as it happens (Claude-style): its reasoning (a collapsed "Thinking…" block that
/// becomes "Thought for 12s"), the live activity and status line, tool steps that fill in their result, the plan as a checklist,
/// notices, and a warning when a tool's output was flagged. Simple view shows only a plain activity line and the warnings.
/// </summary>
public sealed partial class MessageVM
{
    private string _reasoning = "";
    private DateTimeOffset? _reasoningStart, _reasoningEnd;
    private bool _reasoningExpanded, _live;
    private string _activityLabel = "", _status = "", _riskWarning = "";
    private TodoList? _plan;

    public ObservableCollection<PlanStepVM> Plan { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();

    // ---- reasoning ----
    public string Reasoning => _reasoning;
    public bool HasReasoning => _reasoning.Length > 0;
    public Visibility ReasoningVisibility => HasReasoning ? Visibility.Visible : Visibility.Collapsed;
    public bool ReasoningExpanded { get => _reasoningExpanded; set => Set(ref _reasoningExpanded, value); }

    public string ReasoningHeader =>
        _live && _reasoningEnd is null && _reasoningStart is not null ? "Thinking…"
        : _reasoningStart is { } s && _reasoningEnd is { } e ? $"Thought for {Math.Max(1, (int)Math.Round((e - s).TotalSeconds))}s"
        : "Reasoning";

    public void AppendReasoning(string text, DateTimeOffset now)
    {
        if (text.Length == 0 || _reasoning.Length >= LiveEvents.MaxReasoningChars) return;
        _reasoningStart ??= now;
        var room = LiveEvents.MaxReasoningChars - _reasoning.Length;
        _reasoning += text.Length > room ? text[..room] : text;
        RaiseReasoning();
    }

    /// <summary>The reasoning of a stored reply (no timing is known for it).</summary>
    public void SetStoredReasoning(string text)
    {
        _reasoning = text.Length > LiveEvents.MaxReasoningChars ? text[..LiveEvents.MaxReasoningChars] : text;
        RaiseReasoning();
    }

    /// <summary>The model moved on from thinking (it began writing the answer or running a tool).</summary>
    public void EndReasoning(DateTimeOffset now)
    {
        if (_reasoningStart is null || _reasoningEnd is not null) return;
        _reasoningEnd = now;
        Raise(nameof(ReasoningHeader));
    }

    private void RaiseReasoning()
    {
        Raise(nameof(Reasoning)); Raise(nameof(HasReasoning)); Raise(nameof(ReasoningVisibility)); Raise(nameof(ReasoningHeader));
    }

    // ---- live activity and status ----
    /// <summary>A turn is running for this reply (its activity line shows only then).</summary>
    public bool IsLive { get => _live; set { Set(ref _live, value); RaiseActivity(); Raise(nameof(ReasoningHeader)); } }

    public void SetActivityLabel(string label) { _activityLabel = label; RaiseActivity(); }
    public void SetStatus(string status) { _status = status; RaiseActivity(); }

    private ToolStepVM? RunningTool => Tools.LastOrDefault(t => t.Running);

    /// <summary>Advanced: the gateway's own words (status, the running tool, the spinner label).</summary>
    public string ActivityLineAdvanced =>
        _status.Length > 0 ? _status
        : RunningTool is { } t ? "Running " + t.Name + "…"
        : _activityLabel.Length > 0 ? _activityLabel
        : HasReasoning && _reasoningEnd is null ? "Thinking…" : "Working…";

    /// <summary>Simple: plain words only (no tool names, no spinner art).</summary>
    public string ActivityLineSimple =>
        _status.Length > 0 && _status.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ? "Waiting for the model…"
        : RunningTool is not null ? "Working on it…"
        : "Thinking…";

    /// <summary>Shown only while the reply is being written and before any of its text has arrived or while a step runs.</summary>
    public Visibility ActivityLineVisibility => _live && (Text.Length == 0 || RunningTool is not null || _status.Length > 0)
        ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseActivity()
    {
        Raise(nameof(ActivityLineAdvanced)); Raise(nameof(ActivityLineSimple)); Raise(nameof(ActivityLineVisibility));
    }

    // ---- tool steps ----
    public void StartTool(ToolStarted t, DateTimeOffset now)
    {
        EndReasoning(now);
        var existing = Tools.FirstOrDefault(x => x.Id == t.Id);
        if (existing is not null) Tools.Remove(existing);
        Tools.Add(new ToolStepVM(t.Name, t.Input, null, false, t.Id, running: true) { Meta = t.Detail });
        _activityLabel = "";
        RaiseActivity();
    }

    public void FinishTool(ToolFinished f)
    {
        if (Tools.FirstOrDefault(x => x.Id == f.Id) is not { } step) return;
        step.Running = false;
        step.Input ??= f.Input;
        step.Output = f.Output;
        step.Failed = f.Failed;
        var parts = new[] { f.Summary ?? step.Meta, f.DurationSeconds is { } d ? (d < 10 ? d.ToString("0.0") : d.ToString("0")) + " s" : null }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        step.Meta = string.Join(" · ", parts);
        RaiseActivity();
    }

    // ---- warnings, plan, notices ----
    public string RiskWarning => _riskWarning;
    public Visibility RiskWarningVisibility => _riskWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A tool's output was flagged as high risk: said on the step (Advanced) and once for the reply in plain words (both views).</summary>
    public void FlagRisk(ToolRiskFlagged r)
    {
        if (Tools.FirstOrDefault(x => x.Id == r.Id) is { } step)
            step.Risk = "Flagged as risky" + (r.Findings.Count > 0 ? ": " + string.Join("; ", r.Findings.Take(3)) : "") + (r.Redacted ? " (parts were hidden from the agent)" : "");
        _riskWarning = "Something a tool brought back looked unsafe (for example, text trying to give Hermes orders), so it was flagged"
            + (r.Redacted ? " and the risky parts were hidden from the agent." : ". Check the reply with care.");
        Raise(nameof(RiskWarning)); Raise(nameof(RiskWarningVisibility));
    }

    public string PlanHeader => _plan is { } p ? $"Plan · {p.Done} of {p.Total} done" : "";
    public Visibility PlanVisibility => Plan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The newest plan wins; an older revision that arrives late is ignored.</summary>
    public void SetPlan(TodoList plan)
    {
        if (_plan is { } current && plan.Revision < current.Revision) return;
        _plan = plan;
        Plan.Clear();
        foreach (var (item, depth) in plan.Tree()) Plan.Add(new PlanStepVM(item, depth));
        Raise(nameof(PlanHeader)); Raise(nameof(PlanVisibility));
    }

    public Visibility NoticesVisibility => Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void AddNotice(string text)
    {
        if (Notices.Count >= 20 || Notices.Contains(text)) return;
        Notices.Add(text);
        Raise(nameof(NoticesVisibility));
    }

    /// <summary>The turn is over (finished, failed or stopped): nothing runs on, the activity line goes, the thinking time is fixed.</summary>
    public void FinishLive(DateTimeOffset now)
    {
        foreach (var t in Tools.Where(t => t.Running)) t.Running = false;
        EndReasoning(now);
        _status = "";
        _activityLabel = "";
        IsLive = false;
    }
}
