using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

public enum RestFailure
{
    /// <summary>Session expired or not allowed.</summary>
    Auth,
    /// <summary>404 on a collection route: this gateway does not have the route.</summary>
    Unsupported,
    /// <summary>400/422, with the gateway's own reason.</summary>
    Invalid,
    Conflict,
    /// <summary>404 on one named item.</summary>
    NotFound,
    Failed
}

public sealed class RestException(RestFailure failure, string message) : Exception(message)
{
    public RestFailure Failure { get; } = failure;
}

/// <summary>
/// Shared plumbing for the dashboard's JSON routes (capabilities, and the settings pages after it): one bounded read, typed
/// failures that carry the gateway's own sanitised reason, tolerant field readers. Nothing here retries.
/// </summary>
public static class DashboardRest
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    public static async Task<JsonDocument> SendAsync(DashboardAuth auth, HttpMethod method, string relative, string? profile,
        JsonObject? body, bool collection, CancellationToken ct)
    {
        var path = relative + (string.IsNullOrWhiteSpace(profile) ? "" : (relative.Contains('?') ? "&" : "?") + "profile=" + Uri.EscapeDataString(profile));
        using var req = new HttpRequestMessage(method, new Uri(auth.BaseUri, path));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new RestException(Classify(resp.StatusCode, collection), await ReasonAsync(resp, ct).ConfigureAwait(false));
        var bytes = await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
        if (bytes.Length == 0) return JsonDocument.Parse("{}");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new RestException(RestFailure.Failed, "The gateway returned an unreadable response."); }
    }

    public static RestFailure Classify(HttpStatusCode code, bool collection) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RestFailure.Auth,
        HttpStatusCode.NotFound => collection ? RestFailure.Unsupported : RestFailure.NotFound,
        HttpStatusCode.Conflict => RestFailure.Conflict,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => RestFailure.Invalid,
        _ => RestFailure.Failed
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is { } len && len > MaxResponseBytes)
            throw new RestException(RestFailure.Failed, "The gateway's response was too large.");
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxResponseBytes) throw new RestException(RestFailure.Failed, "The gateway's response was too large.");
        }
        return ms.ToArray();
    }

    /// <summary>The gateway's own reason (<c>detail</c> / <c>error</c> / <c>message</c>), trimmed; never a stack or header.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var fallback = $"The gateway answered {(int)resp.StatusCode}.";
        try
        {
            using var doc = JsonDocument.Parse(await ReadBoundedAsync(resp, ct).ConfigureAwait(false));
            var r = doc.RootElement;
            string? reason = null;
            if (r.ValueKind == JsonValueKind.Object)
            {
                reason = Str(r, "detail") ?? Str(r, "error") ?? Str(r, "message");
                if (reason is null && r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                    reason = Str(d[0], "msg");
            }
            if (string.IsNullOrWhiteSpace(reason)) return fallback;
            reason = new string(reason.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
            return reason.Length > 300 ? reason[..300] : reason;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or RestException) { return fallback; }
    }

    // ---- tolerant readers --------------------------------------------------------------------------

    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    public static bool? Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null
            },
            _ => null
        };
    }

    public static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var p) ? p : 0;
    }

    public static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }

    /// <summary>A bare array, or an object holding the array under <paramref name="key"/>; anything else is empty.</summary>
    public static JsonElement ArrayOf(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) return a;
        return default;
    }
}
