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
    bool Chosen = true,
    /// <summary><c>model.options</c> <c>auth_type</c>, when the gateway sent one (<c>local</c>, <c>api_key</c>, …).</summary>
    string? AuthType = null,
    /// <summary>
    /// Runs without a cloud API key: <c>auth_type</c> <c>local</c>, or a llamacpp / Ollama / LM Studio / custom
    /// endpoint. <see cref="ModelCatalog.Usable"/> keeps every model of one, even with no price and even when
    /// the gateway's <c>explicit_only</c> list left it out.
    /// </summary>
    bool Keyless = false);

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

    /// <summary>
    /// What the picker offers: every model of a provider the user set up (<see cref="ModelProvider.Chosen"/>, or a
    /// <see cref="ModelProvider.Keyless"/> local or custom endpoint), and only the free ones of the rest. A keyless
    /// provider is marked chosen so the picker lists it under its own name. A provider left with nothing is dropped.
    /// Catalog is not entitlement.
    /// </summary>
    public ModelCatalog Usable()
    {
        var list = new List<ModelProvider>();
        foreach (var p in Providers)
        {
            if (p.Chosen) { list.Add(p); continue; }
            if (p.Keyless) { list.Add(p with { Chosen = true }); continue; }
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

    /// <summary>
    /// Lenient: a provider with no models is dropped, and so is a cloud provider that still needs a key
    /// (<c>authenticated</c> false, and not <see cref="IsKeylessProvider"/>). A local or no-key custom provider
    /// is kept even then: it has nothing to authenticate. Unknown fields are ignored.
    /// </summary>
    public static ModelCatalog Parse(JsonObject? result)
    {
        var providers = new List<ModelProvider>();
        if (result?["providers"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject row) continue;
                var slug = Text(row, "slug");
                if (string.IsNullOrWhiteSpace(slug)) continue;
                var name = Text(row, "name") ?? slug;
                var authType = Text(row, "auth_type");
                var keyless = IsKeylessProvider(slug, name, authType, Text(row, "key_env"), Flag(row, "is_user_defined") == true);
                // A cloud row with no credentials can't be selected. A local runtime reports the same flag
                // because it has no API key, and it still has models.
                if (Flag(row, "authenticated") == false && !keyless) continue;
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
                var warning = Text(row, "warning");
                providers.Add(new ModelProvider(
                    slug,
                    name,
                    models,
                    featured,
                    warning,
                    Chosen: true,
                    AuthType: authType,
                    Keyless: keyless));
            }
        }
        return new ModelCatalog(
            providers.DistinctBy(p => p.Slug).ToList(),
            BlankToNull(result?["model"]?.GetValue<string>()),
            BlankToNull(result?["provider"]?.GetValue<string>()));
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Text(JsonObject row, string key)
    {
        if (row[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static bool? Flag(JsonObject row, string key) =>
        row[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>
    /// Slugs that serve a machine on the user's network, or a custom OpenAI-compatible endpoint, and do not need
    /// a cloud key. <c>ollama-cloud</c> is not one of these. Hermes aliases llamacpp / ollama / lmstudio onto
    /// <c>custom</c>; a saved endpoint is <c>custom:&lt;name&gt;</c>.
    /// </summary>
    private static readonly HashSet<string> KeylessSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "llamacpp", "llama.cpp", "llama-cpp", "local",
        "ollama", "lmstudio", "lm-studio", "lm_studio", "custom",
    };

    /// <summary>Display names the local runtimes use when the slug was normalized away (<c>Local</c> is the llamacpp row).</summary>
    private static readonly HashSet<string> KeylessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "ollama", "lm studio", "custom endpoint",
    };

    /// <summary>
    /// A provider the chat picker can offer with no API key. <c>auth_type</c> <c>local</c> is the llamacpp row.
    /// Ollama, LM Studio and a user-added custom endpoint are the same kind of row: models, often no price, and
    /// <c>authenticated: false</c> because there is nothing to authenticate. A user-defined row with no key env is too.
    /// A cloud provider that names a key (<c>auth_type</c> <c>api_key</c> and a <c>key_env</c>) is not.
    /// </summary>
    public static bool IsKeylessProvider(string slug, string name, string? authType, string? keyEnv, bool userDefined)
    {
        if (string.Equals(authType?.Trim(), "local", StringComparison.OrdinalIgnoreCase)) return true;
        var id = slug.Trim();
        if (KeylessSlugs.Contains(id) || id.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return true;
        if (KeylessNames.Contains(name.Trim())) return true;
        if (!userDefined) return false;
        var type = authType?.Trim();
        return string.IsNullOrEmpty(type)
            || (string.Equals(type, "api_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(keyEnv));
    }

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
