using System.Text.Json.Nodes;

namespace Hikyaku.Core;

public enum ApprovalChoice
{
    Once,
    Session,
    Always,
    Deny
}

public static class ApprovalChoiceWire
{
    public static string ToWire(ApprovalChoice c) => c switch
    {
        ApprovalChoice.Session => "session",
        ApprovalChoice.Always => "always",
        ApprovalChoice.Deny => "deny",
        _ => "once"
    };

    public static ApprovalChoice? FromWire(string? wire) => wire switch
    {
        "once" => ApprovalChoice.Once,
        "session" => ApprovalChoice.Session,
        "always" => ApprovalChoice.Always,
        "deny" => ApprovalChoice.Deny,
        _ => null
    };
}

public sealed record ClarifyQuestion(string? Qid, string Question, IReadOnlyList<string> Choices, bool MultiSelect = false);

/// <summary>Server→client request this client knows how to show (BookahHermes InputRequest).</summary>
public abstract record NeedsYouRequest(string Id, string? SessionId, ProfileKey? Profile);

public sealed record ApprovalRequest(
    string Id,
    string? SessionId,
    ProfileKey? Profile,
    string Command,
    string Description,
    string? ToolName,
    IReadOnlyList<ApprovalChoice> Choices) : NeedsYouRequest(Id, SessionId, Profile);

public sealed record ClarifyRequest(
    string Id,
    string? SessionId,
    ProfileKey? Profile,
    IReadOnlyList<ClarifyQuestion> Questions) : NeedsYouRequest(Id, SessionId, Profile);

public sealed record SecretRequest(
    string Id,
    string? SessionId,
    ProfileKey? Profile,
    string Kind,
    string Prompt,
    string? EnvVar,
    string? Command) : NeedsYouRequest(Id, SessionId, Profile);

public sealed record VaultSaveLoginRequest(
    string Id,
    string? SessionId,
    ProfileKey? Profile,
    string Site,
    string Origin) : NeedsYouRequest(Id, SessionId, Profile);

public static class InputAnswers
{
    public static JsonObject Approval(ApprovalChoice choice) =>
        new() { ["choice"] = ApprovalChoiceWire.ToWire(choice) };

    public static JsonObject ClarifySingle(string answer) => new() { ["answer"] = answer };

    public static JsonObject ClarifyCancel { get; } = new();

    /// <summary>Empty value declines. Callers must not log <paramref name="value"/>.</summary>
    public static JsonObject Value(string value) => new() { ["value"] = value };
}

/// <summary>
/// Needs-you surface: approvals/questions separate from cron Inbox.
/// Secret/approval answer payloads are never written to the preference store or logs.
/// </summary>
public sealed class NeedsYouService
{
    public static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "approval", "clarify", "sudo", "secret", "vault.unlock_prompt", "vault.code", "vault.save_login"
    };

    private readonly NotificationGate _gate;
    private readonly ProfileSelectionService _selection;
    private readonly string _gatewayId;
    private readonly Func<JsonRpcClient?> _client;
    private readonly object _lock = new();
    private readonly List<NeedsYouRequest> _pending = new();

    public NeedsYouService(NotificationGate gate, ProfileSelectionService selection, string gatewayId, Func<JsonRpcClient?> client)
    {
        _gate = gate;
        _selection = selection;
        _gatewayId = gatewayId;
        _client = client;
    }

    public IReadOnlyList<NeedsYouRequest> Pending
    {
        get { lock (_lock) return _pending.ToList(); }
    }

    public event Action? Changed;

    /// <summary>Ingest a gateway event. Returns true when it affected Needs-you state.</summary>
    public bool HandleGatewayEvent(GatewayEvent ev)
    {
        if (ev.Type is "request.cancel" or "approval.cancelled")
        {
            var id = ev.Payload?["id"]?.GetValue<string>()
                ?? ev.Payload?["request_id"]?.GetValue<string>();
            if (id is null) return false;
            return Clear(id);
        }

        if (ev.Type is "message.complete")
        {
            // Clear requests scoped to this session when the turn ends (BookahHermes).
            if (ev.SessionId is null) return false;
            lock (_lock)
            {
                var n = _pending.RemoveAll(r => r.SessionId == ev.SessionId);
                if (n > 0) { Changed?.Invoke(); return true; }
            }
            return false;
        }

        if (!ev.Type.StartsWith("server.request:", StringComparison.Ordinal) || ev.RequestId is null)
            return false;

        var method = ev.Type["server.request:".Length..];
        if (!Methods.Contains(method))
        {
            // Decline unknown methods so they do not stick as pending forever when we can answer.
            var rpc = _client();
            if (rpc is not null)
                _ = rpc.RespondErrorAsync(ev.RequestId, 4090, "not shown", "SESSION_NOT_SHOWN");
            return false;
        }

        var profile = ResolveProfile(ev);
        if (profile is not null)
        {
            if (!_gate.Allow(Surface.NeedsYou, profile))
                return false; // deselected / unknown profile: do not display
        }
        else
        {
            // Unattributable: mark pending only when the install has finished onboarding (fail closed cold/empty).
            if (!_selection.IsLoaded || _selection.NeedsOnboarding)
                return false;
        }

        var parsed = Parse(ev.RequestId, method, ev.Payload as JsonObject, ev.SessionId, profile);
        if (parsed is null) return false;

        lock (_lock)
        {
            _pending.RemoveAll(r => r.Id == parsed.Id);
            _pending.Add(parsed);
        }
        Changed?.Invoke();
        return true;
    }

    public async Task AnswerApprovalAsync(string id, ApprovalChoice choice, CancellationToken ct = default)
    {
        var rpc = _client() ?? throw new InvalidOperationException("Not connected");
        // Do not log choice payloads beyond the enum wire value (non-secret).
        await rpc.RespondAsync(id, InputAnswers.Approval(choice), ct).ConfigureAwait(false);
        Clear(id);
    }

    public async Task AnswerClarifyAsync(string id, string answer, CancellationToken ct = default)
    {
        var rpc = _client() ?? throw new InvalidOperationException("Not connected");
        await rpc.RespondAsync(id, InputAnswers.ClarifySingle(answer), ct).ConfigureAwait(false);
        Clear(id);
    }

    /// <summary>Answer a secret/sudo/vault prompt. <paramref name="value"/> must never be logged or cached.</summary>
    public async Task AnswerSecretAsync(string id, string value, CancellationToken ct = default)
    {
        var rpc = _client() ?? throw new InvalidOperationException("Not connected");
        await rpc.RespondAsync(id, InputAnswers.Value(value), ct).ConfigureAwait(false);
        Clear(id);
    }

    public async Task DeclineSecretAsync(string id, CancellationToken ct = default)
        => await AnswerSecretAsync(id, "", ct).ConfigureAwait(false);

    public bool Clear(string id)
    {
        lock (_lock)
        {
            var n = _pending.RemoveAll(r => r.Id == id);
            if (n == 0) return false;
        }
        Changed?.Invoke();
        return true;
    }

    public void ClearAll()
    {
        lock (_lock) _pending.Clear();
        Changed?.Invoke();
    }

    private ProfileKey? ResolveProfile(GatewayEvent ev)
    {
        var bot = (ev.Payload as JsonObject)?["profile"]?.GetValue<string>()
            ?? (ev.Payload as JsonObject)?["bot"]?.GetValue<string>();
        if (bot is not null) return new ProfileKey(_gatewayId, bot);
        // Session-only attribution: still require a selected profile gate via null deny unless one profile selected — fail closed.
        return null;
    }

    public static NeedsYouRequest? Parse(string id, string method, JsonObject? paramsObj, string? sessionId, ProfileKey? profile)
    {
        paramsObj ??= new JsonObject();
        return method switch
        {
            "approval" => new ApprovalRequest(
                id, sessionId, profile,
                paramsObj["command"]?.GetValue<string>() ?? "",
                paramsObj["description"]?.GetValue<string>() ?? "",
                paramsObj["tool_name"]?.GetValue<string>(),
                ParseApprovalChoices(paramsObj)),
            "clarify" => ParseClarify(id, sessionId, profile, paramsObj),
            "sudo" => new SecretRequest(id, sessionId, profile, "sudo", "Password for sudo", null,
                paramsObj["command"]?.GetValue<string>()),
            "secret" => new SecretRequest(id, sessionId, profile, "secret",
                paramsObj["prompt"]?.GetValue<string>() is { Length: > 0 } p
                    ? p
                    : "Value for " + (paramsObj["env_var"]?.GetValue<string>() ?? ""),
                paramsObj["env_var"]?.GetValue<string>(), null),
            "vault.unlock_prompt" => new SecretRequest(id, sessionId, profile, "vault.unlock",
                "Unlock password manager", null, null),
            "vault.code" => new SecretRequest(id, sessionId, profile, "vault.code",
                "Enter the sign-in code.", null, null),
            "vault.save_login" => new VaultSaveLoginRequest(id, sessionId, profile,
                paramsObj["site"]?.GetValue<string>() ?? paramsObj["origin"]?.GetValue<string>() ?? "",
                paramsObj["origin"]?.GetValue<string>() ?? ""),
            _ => null
        };
    }

    private static ClarifyRequest? ParseClarify(string id, string? sessionId, ProfileKey? profile, JsonObject paramsObj)
    {
        var questions = new List<ClarifyQuestion>();
        if (paramsObj["questions"] is JsonArray batch)
        {
            foreach (var item in batch)
            {
                if (item is not JsonObject q) continue;
                var qid = q["qid"]?.GetValue<string>();
                if (qid is null) continue;
                questions.Add(new ClarifyQuestion(qid, q["question"]?.GetValue<string>() ?? "",
                    ReadStrings(q["choices"]), q["multi_select"]?.GetValue<bool>() == true));
            }
        }
        else if (paramsObj["question"]?.GetValue<string>() is { } single)
        {
            questions.Add(new ClarifyQuestion(null, single, ReadStrings(paramsObj["choices"]),
                paramsObj["multi_select"]?.GetValue<bool>() == true));
        }
        return questions.Count == 0 ? null : new ClarifyRequest(id, sessionId, profile, questions);
    }

    private static IReadOnlyList<ApprovalChoice> ParseApprovalChoices(JsonObject paramsObj)
    {
        var list = new List<ApprovalChoice>();
        if (paramsObj["choices"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                var c = ApprovalChoiceWire.FromWire(n?.GetValue<string>());
                if (c is { } v) list.Add(v);
            }
        }
        if (list.Count == 0)
        {
            list.Add(ApprovalChoice.Once);
            list.Add(ApprovalChoice.Session);
            list.Add(ApprovalChoice.Always);
        }
        if (!list.Contains(ApprovalChoice.Once)) list.Insert(0, ApprovalChoice.Once);
        if (!list.Contains(ApprovalChoice.Deny)) list.Add(ApprovalChoice.Deny);
        return list.Distinct().ToList();
    }

    private static IReadOnlyList<string> ReadStrings(JsonNode? node)
    {
        if (node is not JsonArray arr) return Array.Empty<string>();
        return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
    }
}
