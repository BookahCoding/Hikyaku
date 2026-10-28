using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hikyaku.Core;

/// <summary>Archive filter wire values for GET /api/sessions (BookahHermes Sessions.kt).</summary>
public enum SessionArchiveFilter
{
    Exclude,
    Only,
    Include
}

/// <summary>Sidebar list filter. Recent excludes machine sources (acp,cron,kanban,oneshot,subagent,tool).</summary>
public sealed record SessionListFilter(SessionArchiveFilter Archived, IReadOnlyList<string>? ExcludeSources = null, string? Source = null)
{
    public static IReadOnlyList<string> MachineSources { get; } =
        new[] { "acp", "cron", "kanban", "oneshot", "subagent", "tool" };

    public static SessionListFilter Recent { get; } = new(SessionArchiveFilter.Exclude, MachineSources);
    public static SessionListFilter ArchivedOnly { get; } = new(SessionArchiveFilter.Only);

    public string ArchivedWire => Archived switch
    {
        SessionArchiveFilter.Only => "only",
        SessionArchiveFilter.Include => "include",
        _ => "exclude"
    };
}

public sealed record SessionSummary(
    string Id,
    string? Title = null,
    string? Preview = null,
    string? Source = null,
    string? Model = null,
    long StartedAt = 0,
    long LastActive = 0,
    int MessageCount = 0,
    bool Pinned = false,
    bool Archived = false,
    bool IsActive = false,
    string? Snippet = null,
    string? Cwd = null,
    bool Unread = false);

public sealed record SessionPage(
    IReadOnlyList<SessionSummary> Sessions,
    int Total = 0,
    int Limit = 0,
    int Offset = 0);

public sealed record SessionMessage(
    string Id,
    string Role,
    string Content,
    string? DisplayContent = null,
    string? Timestamp = null,
    string? DisplayKind = null,
    string? SessionId = null,
    bool Compacted = false,
    IReadOnlyList<StoredToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    /// <summary>The model's reasoning for this step, when the gateway kept it (BookahHermes Sessions.kt <c>reasoning</c>).</summary>
    string? Reasoning = null);

public sealed record SessionMessagesPage(string SessionId, IReadOnlyList<SessionMessage> Messages);

/// <summary>
/// Stored-session REST (BookahHermes SessionsApi.kt). Every call takes an explicit profile id;
/// callers must gate with NotificationGate before invoking.
/// </summary>
public sealed class SessionsApi(DashboardAuth auth)
{
    public const int PageSize = 50;
    public const int SearchLimitDefault = 30;
    public const int HistoryPage = 60;
    public const int HistoryPageMax = 500;

    public Task<SessionPage> ListAsync(string? profile, int limit = PageSize, int offset = 0,
        SessionListFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= SessionListFilter.Recent;
        var q = new List<string>
        {
            "limit=" + Uri.EscapeDataString(Math.Clamp(limit, 1, 100).ToString()),
            "offset=" + Uri.EscapeDataString(Math.Max(0, offset).ToString()),
            "archived=" + Uri.EscapeDataString(filter.ArchivedWire),
            "order=recent"
        };
        if (!string.IsNullOrEmpty(profile)) q.Add("profile=" + Uri.EscapeDataString(profile));
        if (!string.IsNullOrEmpty(filter.Source)) q.Add("source=" + Uri.EscapeDataString(filter.Source));
        if (filter.ExcludeSources is { Count: > 0 } ex)
            q.Add("exclude_sources=" + Uri.EscapeDataString(string.Join(",", ex)));
        return GetAsync<SessionPageDto, SessionPage>("api/sessions?" + string.Join("&", q),
            d => new SessionPage(d.Sessions.Select(MapSummary).ToList(), d.Total, d.Limit, d.Offset), ct);
    }

    public async Task<IReadOnlyList<SessionSummary>> SearchAsync(string? profile, string query, int limit = SearchLimitDefault,
        CancellationToken ct = default)
    {
        var q = new List<string>
        {
            "q=" + Uri.EscapeDataString(query),
            "limit=" + Uri.EscapeDataString(Math.Clamp(limit, 1, 100).ToString())
        };
        if (!string.IsNullOrEmpty(profile)) q.Add("profile=" + Uri.EscapeDataString(profile));
        var dto = await GetRawAsync<SearchDto>("api/sessions/search?" + string.Join("&", q), ct).ConfigureAwait(false);
        return (dto.Results ?? []).Select(s => MapSummary(s) with { Snippet = StripMatchMarkers(s.Snippet) }).ToList();
    }

    public Task<SessionMessagesPage> MessagesAsync(string sessionId, string? profile, int limit = HistoryPage,
        int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session id required", nameof(sessionId));
        var enc = Uri.EscapeDataString(sessionId);
        var q = new List<string>
        {
            "limit=" + Uri.EscapeDataString(Math.Clamp(limit, 1, HistoryPageMax).ToString()),
            "order=latest",
            "include_compacted=true"
        };
        if (offset > 0) q.Add("offset=" + Uri.EscapeDataString(offset.ToString()));
        if (!string.IsNullOrEmpty(profile)) q.Add("profile=" + Uri.EscapeDataString(profile));
        return GetAsync<MessagesDto, SessionMessagesPage>($"api/sessions/{enc}/messages?" + string.Join("&", q),
            d => new SessionMessagesPage(d.SessionId ?? sessionId, (d.Messages ?? []).Select(MapMessage).ToList()), ct);
    }

    public const int MaxHistoryPages = 50;

    /// <summary>
    /// Every stored row, oldest first, read newest-first in pages of <see cref="HistoryPageMax"/> counted back from the
    /// newest row (what counting rows to branch at needs). Rows repeated because the chat grew meanwhile are dropped.
    /// </summary>
    public async Task<StoredRows> AllMessagesAsync(string sessionId, string? profile, CancellationToken ct = default)
    {
        var newestFirstPages = new List<IReadOnlyList<SessionMessage>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fetched = 0;
        var complete = false;
        for (var page = 0; page < MaxHistoryPages; page++)
        {
            var batch = (await MessagesAsync(sessionId, profile, HistoryPageMax, fetched, ct).ConfigureAwait(false)).Messages;
            newestFirstPages.Add(batch.Where(m => string.IsNullOrEmpty(m.Id) || seen.Add(m.Id)).ToList());
            fetched += batch.Count;
            if (batch.Count < HistoryPageMax) { complete = true; break; }
        }
        newestFirstPages.Reverse();
        return new StoredRows(newestFirstPages.SelectMany(p => p).ToList(), complete);
    }

    private async Task<TOut> GetAsync<TDto, TOut>(string relative, Func<TDto, TOut> map, CancellationToken ct)
    {
        var dto = await GetRawAsync<TDto>(relative, ct).ConfigureAwait(false);
        return map(dto);
    }

    private async Task<TDto> GetRawAsync<TDto>(string relative, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(auth.BaseUri, relative));
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthFailedException("session_expired", "session_expired");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Sessions API {(int)resp.StatusCode}: {Trim(body)}");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var dto = await JsonSerializer.DeserializeAsync<TDto>(stream, SessionsJson.Options, ct).ConfigureAwait(false);
        return dto ?? throw new HttpRequestException("Empty sessions response");
    }

    public static string StripMatchMarkers(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return snippet ?? "";
        return snippet.Replace(">>>", "", StringComparison.Ordinal).Replace("<<<", "", StringComparison.Ordinal);
    }

    /// <summary>Stored-session summaries from any answer that lists them (a scheduled job's runs, for one), read as tolerantly as the session list.</summary>
    public static IReadOnlyList<SessionSummary> ParseSummaries(JsonElement array, int max)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        var list = new List<SessionSummary>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            SummaryDto? dto;
            try { dto = item.Deserialize<SummaryDto>(SessionsJson.Options); }
            catch (JsonException) { continue; }                                     // one odd row loses only itself
            if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) continue;
            list.Add(MapSummary(dto));
            if (list.Count >= max) break;
        }
        return list;
    }

    private static SessionSummary MapSummary(SummaryDto s) => new(
        s.Id ?? "",
        s.Title,
        s.Preview,
        s.Source,
        s.Model,
        s.StartedAt,
        s.LastActive,
        s.MessageCount,
        s.Pinned,
        s.Archived,
        s.IsActive,
        s.Snippet,
        s.Cwd,
        s.Unread);

    private static SessionMessage MapMessage(MessageDto m) => new(
        m.Id ?? "",
        m.Role ?? "assistant",
        m.Content ?? "",
        m.DisplayContent,
        m.Timestamp,
        m.DisplayKind,
        m.SessionId,
        m.Compacted,
        ParseToolCalls(m.ToolCalls),
        m.ToolCallId,
        string.IsNullOrWhiteSpace(m.Reasoning) ? null : m.Reasoning.Length > LiveEvents.MaxReasoningChars ? m.Reasoning[..LiveEvents.MaxReasoningChars] : m.Reasoning);

    /// <summary>The calls an assistant row made (<c>tool_calls[]</c>, OpenAI style or flat); anything unreadable is skipped.</summary>
    internal static IReadOnlyList<StoredToolCall> ParseToolCalls(JsonElement? element)
    {
        var list = new List<StoredToolCall>();
        if (element is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var call in arr.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object) continue;
            JsonElement fn = default;
            var hasFn = call.TryGetProperty("function", out fn) && fn.ValueKind == JsonValueKind.Object;
            var nameEl = hasFn && fn.TryGetProperty("name", out var n1) ? n1 : (call.TryGetProperty("name", out var n2) ? n2 : default);
            if (nameEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nameEl.GetString())) continue;
            JsonElement args = default;
            var hasArgs = (hasFn && fn.TryGetProperty("arguments", out args)) || call.TryGetProperty("arguments", out args);
            string? id = null;
            if (call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString();
            list.Add(new StoredToolCall(id, nameEl.GetString()!, hasArgs ? ToolDetails.Input(args) : null));
        }
        return list;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    private sealed class SessionPageDto
    {
        public List<SummaryDto> Sessions { get; set; } = new();
        public int Total { get; set; }
        public int Limit { get; set; }
        public int Offset { get; set; }
    }

    private sealed class SearchDto
    {
        public List<SummaryDto>? Results { get; set; }
    }

    private sealed class MessagesDto
    {
        public string? SessionId { get; set; }
        public List<MessageDto>? Messages { get; set; }
    }

    private sealed class SummaryDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Preview { get; set; }
        public string? Source { get; set; }
        public string? Model { get; set; }
        [JsonConverter(typeof(LenientLongConverter))] public long StartedAt { get; set; }
        [JsonConverter(typeof(LenientLongConverter))] public long LastActive { get; set; }
        public int MessageCount { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool Pinned { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool Archived { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool IsActive { get; set; }
        public string? Snippet { get; set; }
        public string? Cwd { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool Unread { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonConverter(typeof(LenientStringConverter))] public string? Id { get; set; }
        public string? Role { get; set; }
        [JsonConverter(typeof(PlainTextConverter))] public string? Content { get; set; }
        [JsonConverter(typeof(PlainTextConverter))] public string? DisplayContent { get; set; }
        [JsonConverter(typeof(LenientStringConverter))] public string? Timestamp { get; set; }
        public string? DisplayKind { get; set; }
        public string? SessionId { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool Compacted { get; set; }
        public JsonElement? ToolCalls { get; set; }
        [JsonConverter(typeof(LenientStringConverter))] public string? ToolCallId { get; set; }
        [JsonConverter(typeof(PlainTextConverter))] public string? Reasoning { get; set; }
    }
}

internal static class SessionsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Profile-gated session catalog: list/search/resume + cache invalidation on profile switch.
/// Never returns or caches rows for a deselected profile.
/// </summary>
public sealed class SessionCatalog
{
    private readonly NotificationGate _gate;
    private readonly Func<SessionsApi?> _api;
    private readonly object _lock = new();
    private ProfileKey? _activeProfile;
    private IReadOnlyList<SessionSummary> _cached = Array.Empty<SessionSummary>();
    private string _cachedQuery = "";

    public SessionCatalog(NotificationGate gate, Func<SessionsApi?> api)
    {
        _gate = gate;
        _api = api;
    }

    public ProfileKey? ActiveProfile
    {
        get { lock (_lock) return _activeProfile; }
    }

    public IReadOnlyList<SessionSummary> Cached
    {
        get { lock (_lock) return _cached; }
    }

    public bool IsOfflineStale { get; private set; }

    /// <summary>List archived chats instead of recent ones. Changing it drops the cached list.</summary>
    public bool ShowArchived
    {
        get { lock (_lock) return _showArchived; }
        set { lock (_lock) { if (_showArchived == value) return; _showArchived = value; _cached = Array.Empty<SessionSummary>(); _cachedQuery = ""; } }
    }
    private bool _showArchived;

    /// <summary>Switch sidebar profile. Clears cache when the key changes (no cross-profile stale rows).</summary>
    public void SetActiveProfile(ProfileKey? profile)
    {
        lock (_lock)
        {
            if (_activeProfile == profile) return;
            _activeProfile = profile;
            _cached = Array.Empty<SessionSummary>();
            _cachedQuery = "";
            IsOfflineStale = false;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> LoadRecentAsync(CancellationToken ct = default)
    {
        var profile = RequireAllowed(Surface.Navigation);
        var api = _api() ?? throw new InvalidOperationException("Sessions API unavailable.");
        var page = await api.ListAsync(profile.ProfileId, filter: ShowArchived ? SessionListFilter.ArchivedOnly : null, ct: ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_activeProfile != profile) return Array.Empty<SessionSummary>();
            _cached = page.Sessions.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
            _cachedQuery = "";
            IsOfflineStale = false;
            return _cached;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> SearchAsync(string query, CancellationToken ct = default)
    {
        var profile = RequireAllowed(Surface.Search);
        var api = _api() ?? throw new InvalidOperationException("Sessions API unavailable.");
        if (string.IsNullOrWhiteSpace(query))
            return await LoadRecentAsync(ct).ConfigureAwait(false);
        var results = await api.SearchAsync(profile.ProfileId, query.Trim(), ct: ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_activeProfile != profile) return Array.Empty<SessionSummary>();
            _cached = results.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
            _cachedQuery = query.Trim();
            IsOfflineStale = false;
            return _cached;
        }
    }

    public async Task<StoredRows> LoadAllMessagesAsync(string storedSessionId, CancellationToken ct = default)
    {
        var profile = RequireAllowed(Surface.Conversation);
        var api = _api() ?? throw new InvalidOperationException("Sessions API unavailable.");
        return await api.AllMessagesAsync(storedSessionId, profile.ProfileId, ct).ConfigureAwait(false);
    }

    /// <param name="profile">The chat's own profile. Omitted, the sidebar's active profile is used (older callers).</param>
    public async Task<SessionMessagesPage> LoadMessagesAsync(string storedSessionId, ProfileKey? profile = null, CancellationToken ct = default)
    {
        var p = profile is { } explicitProfile ? RequireAllowed(Surface.Conversation, explicitProfile) : RequireAllowed(Surface.Conversation);
        var api = _api() ?? throw new InvalidOperationException("Sessions API unavailable.");
        return await api.MessagesAsync(storedSessionId, p.ProfileId, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One older page of a chat's history: <paramref name="offset"/> rows back from the newest, read for the chat's own
    /// profile (never the sidebar's), and only if that profile is allowed.
    /// </summary>
    public async Task<SessionMessagesPage> LoadMessagesPageAsync(ProfileKey profile, string storedSessionId, int offset, int limit, CancellationToken ct = default)
    {
        var p = RequireAllowed(Surface.Conversation, profile);
        var api = _api() ?? throw new InvalidOperationException("Sessions API unavailable.");
        return await api.MessagesAsync(storedSessionId, p.ProfileId, limit, Math.Max(0, offset), ct).ConfigureAwait(false);
    }

    public void ApplyOffline(IReadOnlyList<SessionSummary> rows, bool stale)
    {
        lock (_lock)
        {
            if (_activeProfile is not { } p || !_gate.Allow(Surface.Navigation, p)) return;
            _cached = rows.ToList();
            IsOfflineStale = stale;
        }
    }

    private ProfileKey RequireAllowed(Surface surface, ProfileKey explicitProfile)
    {
        if (!_gate.Allow(surface, explicitProfile))
            throw new InvalidOperationException("Selected profile is not allowed for " + surface + ".");
        return explicitProfile;
    }

    private ProfileKey RequireAllowed(Surface surface)
    {
        ProfileKey profile;
        lock (_lock)
        {
            profile = _activeProfile ?? throw new InvalidOperationException("No profile selected for sessions.");
        }
        if (!_gate.Allow(surface, profile))
            throw new InvalidOperationException("Selected profile is not allowed for " + surface + ".");
        return profile;
    }
}

/// <summary>Row ids are numbers on the real gateway (Android: <c>Long</c>) but strings in older fixtures; timestamps are float seconds.</summary>
internal sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.String: return r.GetString();
            case JsonTokenType.Number: return System.Text.Encoding.UTF8.GetString(r.ValueSpan);
            case JsonTokenType.True: return "true";
            case JsonTokenType.False: return "false";
            case JsonTokenType.StartObject or JsonTokenType.StartArray: r.Skip(); return null;
            default: return null;
        }
    }

    public override void Write(Utf8JsonWriter w, string? v, JsonSerializerOptions o) => w.WriteStringValue(v);
}

/// <summary>Timestamps arrive as float seconds (<c>1730000000.25</c>); seconds are kept, fractions dropped.</summary>
internal sealed class LenientLongConverter : JsonConverter<long>
{
    public override bool HandleNull => true;

    public override long Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                return r.TryGetInt64(out var l) ? l : double.IsFinite(r.GetDouble()) ? (long)Math.Floor(r.GetDouble()) : 0;
            case JsonTokenType.String:
                return double.TryParse(r.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? (long)Math.Floor(d) : 0;
            case JsonTokenType.StartObject or JsonTokenType.StartArray: r.Skip(); return 0;
            default: return 0;
        }
    }

    public override void Write(Utf8JsonWriter w, long v, JsonSerializerOptions o) => w.WriteNumberValue(v);
}

/// <summary>Android's LenientBooleanSerializer: real booleans, 0/1, and "true"/"false"/"1"/"0" strings.</summary>
internal sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool HandleNull => true;

    public override bool Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Number: return r.TryGetDouble(out var d) && d != 0;
            case JsonTokenType.String:
                var s = r.GetString();
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            case JsonTokenType.StartObject or JsonTokenType.StartArray: r.Skip(); return false;
            default: return false;
        }
    }

    public override void Write(Utf8JsonWriter w, bool v, JsonSerializerOptions o) => w.WriteBooleanValue(v);
}

/// <summary>
/// Android <c>plainText()</c>: strings pass through; OpenAI-style parts arrays join their <c>text</c> parts with a newline
/// (images etc. are dropped); an object contributes its <c>text</c>; anything else is empty.
/// </summary>
internal sealed class PlainTextConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.String: return r.GetString();
            case JsonTokenType.StartArray:
            {
                using var doc = JsonDocument.ParseValue(ref r);
                var parts = new List<string>();
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) parts.Add(part.GetString() ?? "");
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                        parts.Add(tx.GetString() ?? "");
                }
                return string.Join("\n", parts);
            }
            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref r);
                return doc.RootElement.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : "";
            }
            default: return r.TokenType == JsonTokenType.Null ? null : "";
        }
    }

    public override void Write(Utf8JsonWriter w, string? v, JsonSerializerOptions o) => w.WriteStringValue(v);
}
