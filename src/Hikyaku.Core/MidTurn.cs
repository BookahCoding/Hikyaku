using System.Text.Json.Nodes;

namespace Hikyaku.Core;

public enum SteerOutcome
{
    /// <summary>The running turn took the text in (it reads it after its current tool call).</summary>
    Accepted,
    /// <summary>The turn ended meanwhile or the agent cannot take it now: send it as the next prompt instead.</summary>
    Rejected,
    /// <summary>The gateway cannot steer at all (no such method, or an agent without <c>steer()</c>).</summary>
    Unsupported
}

public enum SideTaskKind { Btw, Background }

/// <summary>Calls that act on a running chat without starting or ending its turn.</summary>
public interface IMidTurnTransport
{
    Task<SteerOutcome> SteerAsync(ProfileKey profile, string runtimeSessionId, string text, CancellationToken ct);

    /// <summary>Starts a side agent (<c>prompt.btw</c> / <c>prompt.background</c>); its answer arrives later as an event carrying the returned task id.</summary>
    Task<string?> StartSideTaskAsync(ProfileKey profile, string runtimeSessionId, SideTaskKind kind, string text, CancellationToken ct);
}

public sealed record SideTaskAnswer(SideTaskKind Kind, string? TaskId, string Text, string? Question, bool Failed);

public static class SideTaskEvents
{
    /// <summary>How <c>_spawn_side_agent</c> reports a side task that threw.</summary>
    public const string ErrorPrefix = "error: ";

    public static bool TryParse(GatewayEvent ev, out SideTaskAnswer answer)
    {
        answer = null!;
        var kind = ev.Type switch
        {
            "btw.complete" => SideTaskKind.Btw,
            "background.complete" => SideTaskKind.Background,
            _ => (SideTaskKind?)null
        };
        if (kind is null || ev.Payload is not JsonObject p) return false;
        var text = Str(p, "text")?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        answer = new SideTaskAnswer(kind.Value, Str(p, "task_id"), text, Str(p, "question"), text.StartsWith(ErrorPrefix, StringComparison.Ordinal));
        return true;
    }

    private static string? Str(JsonObject o, string n) => o[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
