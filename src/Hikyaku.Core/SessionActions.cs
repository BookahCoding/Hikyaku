using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>
/// Changes to a stored chat on the gateway (rename, pin, archive, mark read / unread, delete), sent the way the dashboard takes
/// a write: the row's mutation snapshot is read first and the write names it (a fresh request id plus the revision and generation
/// the row had), so it applies once and only over the row as read. Wire contract: BookahHermes <c>SessionsApi.kt</c> @ 5f00986b
/// (<c>mutation-snapshot</c>, <c>PATCH</c> / <c>DELETE api/sessions/{id}</c>). A gateway too old to have snapshots gets the write
/// without an identity. A write that lost a race is read and sent again once, except a new title (it could overwrite one another
/// device just gave the chat). Nothing else is retried. The profile is named on every call.
/// </summary>
public sealed class SessionActionsApi(DashboardAuth auth)
{
    private const int Attempts = 2;
    public const string GoneMessage = "This chat no longer exists.";

    public Task RenameAsync(string sessionId, string profile, string title, CancellationToken ct) =>
        PatchAsync(sessionId, profile, new JsonObject { ["title"] = title }, retry: false, ct);

    public Task SetPinnedAsync(string sessionId, string profile, bool pinned, CancellationToken ct) =>
        PatchAsync(sessionId, profile, new JsonObject { ["pinned"] = pinned }, retry: true, ct);

    public Task SetArchivedAsync(string sessionId, string profile, bool archived, CancellationToken ct) =>
        PatchAsync(sessionId, profile, new JsonObject { ["archived"] = archived }, retry: true, ct);

    /// <summary>Sets the gateway's shared read state (<c>unread:false</c> marks the chat read for every client).</summary>
    public Task SetUnreadAsync(string sessionId, string profile, bool unread, CancellationToken ct) =>
        PatchAsync(sessionId, profile, new JsonObject { ["unread"] = unread }, retry: true, ct);

    /// <summary>Deletes the chat; one that is already gone also succeeds.</summary>
    public async Task DeleteAsync(string sessionId, string profile, CancellationToken ct)
    {
        try
        {
            await MutateAsync(sessionId, profile, retry: true, ct, identity =>
            {
                var q = "profile=" + Uri.EscapeDataString(profile);
                if (identity is { } i)
                {
                    q += "&request_id=" + Uri.EscapeDataString(i.RequestId) + "&expected_revision=" + i.Revision;
                    if (i.Generation is { } g) q += "&expected_generation=" + g;
                }
                return new HttpRequestMessage(HttpMethod.Delete, new Uri(auth.BaseUri, "api/sessions/" + Uri.EscapeDataString(sessionId) + "?" + q));
            }).ConfigureAwait(false);
        }
        catch (RestException ex) when (ex.Failure == RestFailure.NotFound) { /* already gone */ }
    }

    private Task PatchAsync(string sessionId, string profile, JsonObject fields, bool retry, CancellationToken ct) =>
        MutateAsync(sessionId, profile, retry, ct, identity =>
        {
            var body = new JsonObject();
            foreach (var (k, v) in fields) body[k] = v?.DeepClone();
            body["profile"] = profile;
            if (identity is { } i)
            {
                body["request_id"] = i.RequestId;
                body["expected_revision"] = i.Revision;
                if (i.Generation is { } g) body["expected_generation"] = g;
            }
            return new HttpRequestMessage(HttpMethod.Patch, new Uri(auth.BaseUri, "api/sessions/" + Uri.EscapeDataString(sessionId)))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
        });

    private readonly record struct Identity(string RequestId, long Revision, long? Generation);

    private async Task MutateAsync(string sessionId, string profile, bool retry, CancellationToken ct, Func<Identity?, HttpRequestMessage> build)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < (retry ? Attempts : 1); attempt++)
        {
            var identity = await ReadSnapshotAsync(sessionId, profile, ct).ConfigureAwait(false);
            using var req = build(identity);
            using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return;
            var (code, message) = await ReadFailureAsync(resp, ct).ConfigureAwait(false);
            var ex = new RestException(resp.StatusCode == HttpStatusCode.NotFound ? RestFailure.NotFound : DashboardRest.Classify(resp.StatusCode, false), Words(message));
            // Retry only a write that lost a race with another change, and only when it named an identity.
            if (identity is not null && resp.StatusCode == HttpStatusCode.Conflict && (code is "revision_conflict" or "stale_generation")) { last = ex; continue; }
            throw ex;
        }
        throw last ?? new RestException(RestFailure.Failed, "The change could not be made.");
    }

    private async Task<Identity?> ReadSnapshotAsync(string sessionId, string profile, CancellationToken ct)
    {
        var uri = new Uri(auth.BaseUri, "api/sessions/" + Uri.EscapeDataString(sessionId) + "/mutation-snapshot?profile=" + Uri.EscapeDataString(profile));
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await auth.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var (_, message) = await ReadFailureAsync(resp, ct).ConfigureAwait(false);
            // A gateway without snapshots: the route is missing ("Not Found" / "No such API endpoint"). Any other 404 is the answer.
            if (resp.StatusCode == HttpStatusCode.NotFound && (message is "Not Found" || message.StartsWith("No such API endpoint", StringComparison.Ordinal)))
                return null;
            throw new RestException(DashboardRest.Classify(resp.StatusCode, false), Words(message));
        }
        // Something in front of the route that answers with a page rather than JSON also means "no snapshots".
        if (resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return null;
        using var doc = JsonDocument.Parse(await ReadBoundedAsync(resp, ct).ConfigureAwait(false));
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (DashboardRest.Bool(r, "exists") != true) throw new RestException(RestFailure.NotFound, GoneMessage);
        if (!r.TryGetProperty("runtime_generation", out var g) || g.ValueKind != JsonValueKind.Number || !g.TryGetInt64(out var generation))
            throw new RestException(RestFailure.Failed, "The gateway didn't say which version of the chat it has. Try again.");
        long revision = r.TryGetProperty("runtime_revision", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.TryGetInt64(out var rev) ? rev : 0;
        return new Identity(Guid.NewGuid().ToString(), revision, generation);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 256 * 1024) throw new RestException(RestFailure.Failed, "The gateway's response was too large.");
        }
        return ms.ToArray();
    }

    /// <summary>The gateway's reason, with its machine code (the part before ':') when there is one.</summary>
    private static async Task<(string Code, string Message)> ReadFailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await ReadBoundedAsync(resp, ct).ConfigureAwait(false));
            var r = doc.RootElement;
            var m = r.ValueKind == JsonValueKind.Object ? DashboardRest.Str(r, "detail") ?? DashboardRest.Str(r, "error") ?? DashboardRest.Str(r, "message") : null;
            if (m is null && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object)
                m = DashboardRest.Str(d, "code") is { } c ? c + ": " + (DashboardRest.Str(d, "message") ?? "") : DashboardRest.Str(d, "message");
            m = new string((m ?? "").Where(ch => !char.IsControl(ch) || ch == ' ').ToArray()).Trim();
            if (m.Length > 300) m = m[..300];
            return (m.Split(':', 2)[0].Trim(), m.Length > 0 ? m : $"The gateway answered {(int)resp.StatusCode}.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or RestException)
        {
            return ("", $"The gateway answered {(int)resp.StatusCode}.");
        }
    }

    /// <summary>The gateway's session-write refusal codes, said for the user; other messages (for example a title already in use) pass as they are.</summary>
    public static string Words(string message) => message.Split(':', 2)[0].Trim() switch
    {
        "revision_conflict" or "stale_generation" => "The chat changed somewhere else meanwhile. Try again.",
        "session_busy" => "The chat is busy right now. Try again in a moment.",
        "not_found" => GoneMessage,
        "permission_denied" or "profile_mismatch" => "The gateway didn't allow this change to the chat.",
        "client_upgrade_required" or "mutation_identity_required" => "The gateway refused the change. Update Hikyaku and try again.",
        "owner_unreachable" or "runtime_draining" or "storage_unavailable" => "The gateway can't change the chat right now. Try again in a moment.",
        _ => message
    };
}
