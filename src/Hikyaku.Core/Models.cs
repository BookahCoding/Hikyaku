using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>One pickable model (BookahHermes ModelOption).</summary>
public sealed record ModelOption(
    string Id,
    string Provider,
    bool Reasoning = false,
    bool CanDisableReasoning = false,
    bool Fast = false,
    string? Price = null)
{
    public bool IsFree =>
        string.Equals(Price, "Free", StringComparison.OrdinalIgnoreCase)
        || Id.EndsWith(":free", StringComparison.OrdinalIgnoreCase);

    public string StarKey => Provider + "/" + Id;
}

public sealed record ModelProvider(
    string Slug,
    string Name,
    IReadOnlyList<ModelOption> Models,
    IReadOnlyList<string>? Featured = null,
    string? Warning = null,
    bool Chosen = true);

/// <summary>Catalog from model.options. Available ≠ entitlement.</summary>
public sealed record ModelCatalog(
    IReadOnlyList<ModelProvider> Providers,
    string? CurrentModel = null,
    string? CurrentProvider = null)
{
    public ModelOption? Find(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var p in Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider) &&
                !string.Equals(p.Slug, provider, StringComparison.OrdinalIgnoreCase))
                continue;
            var hit = p.Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Chosen providers: all models; others: free only. Catalog is not entitlement.</summary>
    public ModelCatalog Usable()
    {
        var list = new List<ModelProvider>();
        foreach (var p in Providers)
        {
            if (p.Chosen) { list.Add(p); continue; }
            var free = p.Models.Where(m => m.IsFree).ToList();
            if (free.Count > 0) list.Add(p with { Models = free });
        }
        return this with { Providers = list };
    }

    public ModelCatalog WithChosen(IReadOnlySet<string>? slugs) =>
        this with
        {
            Providers = Providers.Select(p => p with
            {
                Chosen = slugs is null || slugs.Contains(p.Slug)
            }).ToList()
        };

    public static ModelCatalog Parse(JsonObject? result)
    {
        var providers = new List<ModelProvider>();
        if (result?["providers"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject row) continue;
                if (row["authenticated"]?.GetValue<bool>() == false) continue;
                var slug = row["slug"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(slug)) continue;
                var capabilities = row["capabilities"] as JsonObject;
                var pricing = row["pricing"] as JsonObject;
                var unavailable = Strings(row, "unavailable_models").ToHashSet(StringComparer.Ordinal);
                var models = new List<ModelOption>();
                foreach (var id in Strings(row, "models"))
                {
                    if (unavailable.Contains(id)) continue;
                    var caps = capabilities?[id] as JsonObject;
                    models.Add(new ModelOption(
                        id, slug,
                        caps?["reasoning"]?.GetValue<bool>() == true,
                        caps?["can_disable_reasoning"]?.GetValue<bool>() == true,
                        caps?["fast"]?.GetValue<bool>() == true,
                        FormatPrice(pricing?[id] as JsonObject)));
                }
                if (models.Count == 0) continue;
                var featured = Strings(row, "featured_models").Where(f => models.Any(m => m.Id == f)).ToList();
                providers.Add(new ModelProvider(
                    slug,
                    row["name"]?.GetValue<string>() is { Length: > 0 } n ? n : slug,
                    models,
                    featured,
                    row["warning"]?.GetValue<string>(),
                    Chosen: true));
            }
        }
        return new ModelCatalog(
            providers.DistinctBy(p => p.Slug).ToList(),
            BlankToNull(result?["model"]?.GetValue<string>()),
            BlankToNull(result?["provider"]?.GetValue<string>()));
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IEnumerable<string> Strings(JsonObject row, string key)
    {
        if (row[key] is not JsonArray a) yield break;
        foreach (var n in a)
        {
            var s = n?.GetValue<string>();
            if (!string.IsNullOrEmpty(s)) yield return s;
        }
    }

    private static string? FormatPrice(JsonObject? price)
    {
        if (price is null) return null;
        if (price["free"]?.GetValue<bool>() == true) return "Free";
        var input = price["input"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(input)) return null;
        var output = price["output"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(output) ? input : $"{input} / {output}";
    }
}

public enum ReasoningEffort
{
    Off, Minimal, Low, Medium, High, XHigh, Max, Ultra
}

public static class ReasoningEffortWire
{
    public static ReasoningEffort Default => ReasoningEffort.Medium;

    public static string ToWire(ReasoningEffort e) => e switch
    {
        ReasoningEffort.Off => "none",
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        ReasoningEffort.Max => "max",
        ReasoningEffort.Ultra => "ultra",
        _ => "medium"
    };

    public static ReasoningEffort? FromWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "none" or "off" => ReasoningEffort.Off,
            "minimal" => ReasoningEffort.Minimal,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "extra high" => ReasoningEffort.XHigh,
            "max" => ReasoningEffort.Max,
            "ultra" => ReasoningEffort.Ultra,
            _ => null
        };
    }

    public static IReadOnlyList<ReasoningEffort> ChoicesFor(ModelOption? model)
    {
        if (model is null || !model.Reasoning) return Array.Empty<ReasoningEffort>();
        var all = Enum.GetValues<ReasoningEffort>();
        return model.CanDisableReasoning ? all : all.Where(e => e != ReasoningEffort.Off).ToArray();
    }
}

public abstract record ModelSwitchResult;
public sealed record ModelSwitchDone : ModelSwitchResult;
public sealed record ModelSwitchNeedsConfirmation(string Message) : ModelSwitchResult;
public sealed record ModelSwitchFailed(string? Error = null) : ModelSwitchResult;

/// <summary>Per-chat pending picks before session.create, plus live config.set (BookahHermes ChatSession).</summary>
public sealed class SessionModelControls
{
    private readonly Func<JsonRpcClient?> _client;
    private readonly Func<Task<string>> _ensureRuntimeSession;

    public SessionModelControls(Func<JsonRpcClient?> client, Func<Task<string>> ensureRuntimeSession)
    {
        _client = client;
        _ensureRuntimeSession = ensureRuntimeSession;
    }

    public string? Model { get; private set; }
    public string? Provider { get; private set; }
    public string? ReasoningEffort { get; private set; }
    public bool? Fast { get; private set; }

    /// <summary>Pending new-chat selection only — no RPC until session exists.</summary>
    public void RememberPending(string model, string provider, string? reasoning = null, bool? fast = null)
    {
        Model = model;
        Provider = provider;
        if (reasoning is not null) ReasoningEffort = reasoning;
        if (fast is not null) Fast = fast;
    }

    public async Task<ModelCatalog> OptionsAsync(string? runtimeSessionId, string? profile, CancellationToken ct = default)
    {
        var client = _client() ?? throw new RpcException(0, "Not connected to the gateway.");
        IReadOnlySet<string>? explicitSlugs = null;
        try
        {
            var explicitCat = await RequestOptionsAsync(client, runtimeSessionId, profile, refresh: false, explicitOnly: true, ct)
                .ConfigureAwait(false);
            var set = explicitCat.Providers.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
            if (set.Count > 0) explicitSlugs = set;
        }
        catch (RpcException) { /* older gateway */ }

        var catalog = await RequestOptionsAsync(client, runtimeSessionId, profile, refresh: false, explicitOnly: false, ct)
            .ConfigureAwait(false);
        return catalog.WithChosen(explicitSlugs).Usable();
    }

    public async Task<ModelSwitchResult> SetModelAsync(string model, string provider, bool confirm = false, CancellationToken ct = default)
    {
        var previousModel = Model;
        var previousProvider = Provider;
        Model = model;
        Provider = provider;
        try
        {
            var runtimeId = await _ensureRuntimeSession().ConfigureAwait(false);
            var client = _client() ?? throw new RpcException(0, "Not connected to the gateway.");
            var parameters = new JsonObject
            {
                ["session_id"] = runtimeId,
                ["key"] = "model",
                ["value"] = $"{model} --provider {provider} --session"
            };
            if (confirm) parameters["confirm_expensive_model"] = true;
            var result = await client.RequestAsync("config.set", parameters, ct: ct).ConfigureAwait(false) as JsonObject;
            if (result?["confirm_required"]?.GetValue<bool>() == true)
            {
                Model = previousModel;
                Provider = previousProvider;
                return new ModelSwitchNeedsConfirmation(
                    result["confirm_message"]?.GetValue<string>()
                    ?? "This model costs more than usual. Switch anyway?");
            }
            return new ModelSwitchDone();
        }
        catch (Exception ex)
        {
            Model = previousModel;
            Provider = previousProvider;
            return new ModelSwitchFailed(ex.Message);
        }
    }

    public async Task SetReasoningEffortAsync(string effort, CancellationToken ct = default)
    {
        var previous = ReasoningEffort;
        ReasoningEffort = effort;
        try
        {
            await ConfigSetAsync("reasoning", JsonValue.Create(effort)!, ct).ConfigureAwait(false);
        }
        catch
        {
            ReasoningEffort = previous;
            throw;
        }
    }

    public async Task SetFastAsync(bool fast, CancellationToken ct = default)
    {
        var previous = Fast;
        Fast = fast;
        try
        {
            await ConfigSetAsync("fast", JsonValue.Create(fast ? "fast" : "normal")!, ct).ConfigureAwait(false);
        }
        catch
        {
            Fast = previous;
            throw;
        }
    }

    /// <summary>Fields for session.create when the chat is still pending (no automatic paid route).</summary>
    public void ApplyToCreateParams(JsonObject createParams)
    {
        if (!string.IsNullOrWhiteSpace(Model)) createParams["model"] = Model;
        if (!string.IsNullOrWhiteSpace(Provider)) createParams["provider"] = Provider;
        if (!string.IsNullOrWhiteSpace(ReasoningEffort)) createParams["reasoning_effort"] = ReasoningEffort;
        if (Fast is { } f) createParams["fast"] = f;
    }

    private async Task ConfigSetAsync(string key, JsonNode value, CancellationToken ct)
    {
        var runtimeId = await _ensureRuntimeSession().ConfigureAwait(false);
        var client = _client() ?? throw new RpcException(0, "Not connected to the gateway.");
        await client.RequestAsync("config.set", new JsonObject
        {
            ["session_id"] = runtimeId,
            ["key"] = key,
            ["value"] = value
        }, ct: ct).ConfigureAwait(false);
    }

    private static async Task<ModelCatalog> RequestOptionsAsync(
        JsonRpcClient client, string? runtimeSessionId, string? profile,
        bool refresh, bool explicitOnly, CancellationToken ct)
    {
        var p = new JsonObject();
        if (!string.IsNullOrEmpty(runtimeSessionId)) p["session_id"] = runtimeSessionId;
        if (!string.IsNullOrEmpty(profile)) p["profile"] = profile;
        if (refresh) p["refresh"] = true;
        if (explicitOnly) p["explicit_only"] = true;
        var result = await client.RequestAsync("model.options", p, ct: ct).ConfigureAwait(false) as JsonObject;
        return ModelCatalog.Parse(result);
    }
}
