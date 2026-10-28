using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 27 — session actions (rename, pin, archive, mark read, delete) with the gateway's mutation-snapshot handshake. Contract:
/// BookahHermes SessionsApi.kt @ 5f00986b. Fixtures only; no live gateway.
/// </summary>
public class Checkpoint27SessionActionsTests
{
    private sealed record Seen(HttpMethod Method, string PathAndQuery, string Body);

    private sealed class Stub(List<Seen> seen, Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int _n;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            seen.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, body));
            return reply(request, _n++);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Snap(long rev = 7, long gen = 3) =>
        Json(HttpStatusCode.OK, $$"""{"exists":true,"runtime_revision":{{rev}},"runtime_generation":{{gen}}}""");

    private static (SessionActionsApi Api, List<Seen> Seen, DashboardAuth Auth) Make(Func<HttpRequestMessage, int, HttpResponseMessage> reply)
    {
        var seen = new List<Seen>();
        var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Stub(seen, reply));
        return (new SessionActionsApi(auth), seen, auth);
    }

    [Fact]
    public async Task A_write_reads_the_snapshot_first_and_names_it()
    {
        var (api, seen, auth) = Make((r, n) => n == 0 ? Snap() : Json(HttpStatusCode.OK, """{"ok":true}"""));
        using var _a = auth;
        await api.RenameAsync("s/1", "work", "New name", CancellationToken.None);

        Assert.Equal(2, seen.Count);
        Assert.Equal(HttpMethod.Get, seen[0].Method);
        Assert.Equal("/api/sessions/s%2F1/mutation-snapshot?profile=work", seen[0].PathAndQuery);
        Assert.Equal(HttpMethod.Patch, seen[1].Method);
        Assert.Equal("/api/sessions/s%2F1", seen[1].PathAndQuery);
        using var body = JsonDocument.Parse(seen[1].Body);
        var r = body.RootElement;
        Assert.Equal("New name", r.GetProperty("title").GetString());
        Assert.Equal("work", r.GetProperty("profile").GetString());
        Assert.Equal(7, r.GetProperty("expected_revision").GetInt64());
        Assert.Equal(3, r.GetProperty("expected_generation").GetInt64());
        Assert.True(Guid.TryParse(r.GetProperty("request_id").GetString(), out _));
    }

    [Fact]
    public async Task Pin_archive_and_unread_send_only_their_own_field()
    {
        var (api, seen, auth) = Make((r, n) => r.Method == HttpMethod.Get ? Snap() : Json(HttpStatusCode.OK, "{}"));
        using var _a = auth;
        await api.SetPinnedAsync("s", "p", true, CancellationToken.None);
        await api.SetArchivedAsync("s", "p", true, CancellationToken.None);
        await api.SetUnreadAsync("s", "p", false, CancellationToken.None);
        var bodies = seen.Where(s => s.Method == HttpMethod.Patch).Select(s => JsonDocument.Parse(s.Body).RootElement).ToList();
        Assert.True(bodies[0].GetProperty("pinned").GetBoolean());
        Assert.False(bodies[0].TryGetProperty("archived", out _));
        Assert.True(bodies[1].GetProperty("archived").GetBoolean());
        Assert.False(bodies[2].GetProperty("unread").GetBoolean());
        Assert.False(bodies[2].TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Delete_names_the_identity_in_the_query()
    {
        var (api, seen, auth) = Make((r, n) => n == 0 ? Snap(9, 4) : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var _a = auth;
        await api.DeleteAsync("s1", "work", CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, seen[1].Method);
        Assert.StartsWith("/api/sessions/s1?profile=work&request_id=", seen[1].PathAndQuery);
        Assert.EndsWith("&expected_revision=9&expected_generation=4", seen[1].PathAndQuery);
    }

    [Fact]
    public async Task Deleting_a_chat_that_is_already_gone_succeeds_without_sending_a_delete()
    {
        var (api, seen, auth) = Make((_, _) => Json(HttpStatusCode.OK, """{"exists":false}"""));
        using var _a = auth;
        await api.DeleteAsync("s1", "p", CancellationToken.None);
        Assert.DoesNotContain(seen, s => s.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Changing_a_chat_that_is_gone_says_so()
    {
        var (api, seen, auth) = Make((_, _) => Json(HttpStatusCode.OK, """{"exists":false}"""));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SetPinnedAsync("s1", "p", true, CancellationToken.None));
        Assert.Equal(RestFailure.NotFound, ex.Failure);
        Assert.Equal(SessionActionsApi.GoneMessage, ex.Message);
        Assert.Single(seen);                                      // nothing was written
    }

    [Theory]
    [InlineData(404, """{"detail":"Not Found"}""", "application/json")]
    [InlineData(404, """{"detail":"No such API endpoint: x"}""", "application/json")]
    [InlineData(200, "<html>login</html>", "text/html")]
    public async Task A_gateway_without_snapshots_gets_the_write_without_an_identity(int code, string body, string type)
    {
        var (api, seen, auth) = Make((r, n) => n == 0
            ? new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, type) }
            : Json(HttpStatusCode.OK, "{}"));
        using var _a = auth;
        await api.SetPinnedAsync("s", "p", true, CancellationToken.None);
        using var sent = JsonDocument.Parse(seen[1].Body);
        Assert.False(sent.RootElement.TryGetProperty("request_id", out _));
        Assert.False(sent.RootElement.TryGetProperty("expected_revision", out _));
    }

    [Fact]
    public async Task A_snapshot_without_a_generation_stops_before_writing()
    {
        var (api, seen, auth) = Make((_, _) => Json(HttpStatusCode.OK, """{"exists":true,"runtime_revision":1}"""));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SetPinnedAsync("s", "p", true, CancellationToken.None));
        Assert.Contains("which version", ex.Message);
        Assert.Single(seen);
    }

    [Fact]
    public async Task A_write_that_lost_a_race_is_read_and_sent_again_once_with_a_new_identity()
    {
        var (api, seen, auth) = Make((r, n) => n switch
        {
            0 => Snap(1, 1),
            1 => Json(HttpStatusCode.Conflict, """{"detail":"revision_conflict: stale"}"""),
            2 => Snap(2, 1),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var _a = auth;
        await api.SetPinnedAsync("s", "p", true, CancellationToken.None);
        var writes = seen.Where(s => s.Method == HttpMethod.Patch).Select(s => JsonDocument.Parse(s.Body).RootElement).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal(1, writes[0].GetProperty("expected_revision").GetInt64());
        Assert.Equal(2, writes[1].GetProperty("expected_revision").GetInt64());
        Assert.NotEqual(writes[0].GetProperty("request_id").GetString(), writes[1].GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task A_second_lost_race_is_reported_in_words_and_never_sent_a_third_time()
    {
        var (api, seen, auth) = Make((r, n) => r.Method == HttpMethod.Get ? Snap() : Json(HttpStatusCode.Conflict, """{"detail":"stale_generation"}"""));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.SetArchivedAsync("s", "p", true, CancellationToken.None));
        Assert.Equal("The chat changed somewhere else meanwhile. Try again.", ex.Message);
        Assert.Equal(2, seen.Count(s => s.Method == HttpMethod.Patch));
    }

    [Fact]
    public async Task A_new_title_is_never_sent_again_over_a_race()
    {
        var (api, seen, auth) = Make((r, n) => r.Method == HttpMethod.Get ? Snap() : Json(HttpStatusCode.Conflict, """{"detail":"revision_conflict"}"""));
        using var _a = auth;
        await Assert.ThrowsAsync<RestException>(() => api.RenameAsync("s", "p", "t", CancellationToken.None));
        Assert.Single(seen, s => s.Method == HttpMethod.Patch);
    }

    [Theory]
    [InlineData("session_busy: x", "The chat is busy right now. Try again in a moment.")]
    [InlineData("permission_denied", "The gateway didn't allow this change to the chat.")]
    [InlineData("client_upgrade_required", "The gateway refused the change. Update Hikyaku and try again.")]
    [InlineData("Title already in use", "Title already in use")]
    public async Task Refusal_codes_are_said_in_words_and_other_messages_pass_through(string detail, string expected)
    {
        var (api, _, auth) = Make((r, n) => r.Method == HttpMethod.Get ? Snap() : Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { detail })));
        using var _a = auth;
        var ex = await Assert.ThrowsAsync<RestException>(() => api.RenameAsync("s", "p", "t", CancellationToken.None));
        Assert.Equal(RestFailure.Invalid, ex.Failure);
        Assert.Equal(expected, ex.Message);
    }
}
