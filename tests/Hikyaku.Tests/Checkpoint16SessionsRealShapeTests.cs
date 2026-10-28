using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16b — the stored-session REST payloads as the real gateway/Android source define them
/// (BookahHermes Sessions.kt @ 5f00986b): numeric row ids, float-second timestamps, lenient booleans, and message
/// content that is a parts array for multimodal turns. Earlier fixtures used only string ids and integer times,
/// which would throw against a real gateway. Synthetic payloads only.
/// </summary>
public class Checkpoint16SessionsRealShapeTests
{
    private sealed class Stub(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body(r), Encoding.UTF8, "application/json") });
    }

    private static SessionsApi Api(string json)
    {
        var auth = new DashboardAuth(new Uri("https://gw.example/"), new Stub(_ => json));
        return new SessionsApi(auth);
    }

    [Fact]
    public async Task Session_list_accepts_float_second_timestamps_and_lenient_booleans()
    {
        var api = Api("""
        {"sessions":[{"id":"a","title":"t","started_at":1730000000.25,"last_active":1730000100.5,"message_count":3,
          "pinned":0,"archived":false,"is_active":1,"unread":"true","cwd":"/work"},
          {"id":"b","started_at":null,"last_active":1730000200,"pinned":true,"archived":"false","is_active":"0","unread":0}],
         "total":2,"limit":50,"offset":0}
        """);
        var page = await api.ListAsync("p");
        var a = page.Sessions[0];
        Assert.Equal(1730000000, a.StartedAt);
        Assert.Equal(1730000100, a.LastActive);
        Assert.False(a.Pinned);
        Assert.True(a.IsActive);
        Assert.True(a.Unread);
        var b = page.Sessions[1];
        Assert.Equal(0, b.StartedAt);
        Assert.True(b.Pinned);
        Assert.False(b.Archived);
        Assert.False(b.IsActive);
        Assert.False(b.Unread);
    }

    [Fact]
    public async Task Search_results_accept_the_same_real_shapes()
    {
        var api = Api("""{"results":[{"id":"s1","last_active":1730000100.75,"snippet":">>>hit<<<","pinned":1}]}""");
        var hits = await api.SearchAsync("p", "hit");
        Assert.Equal(1730000100, hits[0].LastActive);
        Assert.True(hits[0].Pinned);
        Assert.Equal("hit", hits[0].Snippet);
    }

    [Fact]
    public async Task Messages_accept_numeric_ids_numeric_timestamps_and_parts_arrays()
    {
        var api = Api("""
        {"session_id":"s1","messages":[
          {"id":12,"role":"user","content":"hello","timestamp":1730000000.5,"compacted":1},
          {"id":13,"role":"assistant","content":[{"type":"text","text":"part one"},{"type":"image_url","image_url":{"url":"x"}},{"type":"text","text":"part two"}],
           "display_content":[{"text":"shown"}],"display_kind":"skill","active":true},
          {"id":"14","role":"assistant","content":null},
          {"id":15,"role":"tool","content":{"text":"object text"}}
        ]}
        """);
        var page = await api.MessagesAsync("s1", "p");
        Assert.Equal(new[] { "12", "13", "14", "15" }, page.Messages.Select(m => m.Id));
        Assert.Equal("hello", page.Messages[0].Content);
        Assert.Equal("1730000000.5", page.Messages[0].Timestamp);
        Assert.True(page.Messages[0].Compacted);
        Assert.Equal("part one\npart two", page.Messages[1].Content);   // text parts only, like Android plainText()
        Assert.Equal("shown", page.Messages[1].DisplayContent);
        Assert.Equal("", page.Messages[2].Content);
        Assert.Equal("object text", page.Messages[3].Content);
    }

    [Fact]
    public async Task Existing_string_shaped_fixtures_still_work()
    {
        var api = Api("""{"session_id":"s1","messages":[{"id":"m1","role":"user","content":"hi","timestamp":"2026-10-26T10:00:00Z"}]}""");
        var page = await api.MessagesAsync("s1", "p");
        Assert.Equal("m1", page.Messages[0].Id);
        Assert.Equal("2026-10-26T10:00:00Z", page.Messages[0].Timestamp);
    }
}
