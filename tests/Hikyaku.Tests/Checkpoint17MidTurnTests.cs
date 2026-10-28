using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 17a — mid-turn gateway calls (BookahHermes ChatSession.steer / startSideTask @ 5f00986b): <c>session.steer</c>
/// outcomes (status "queued" = taken in; METHOD_NOT_FOUND / 4010 = cannot steer), <c>prompt.btw</c> / <c>prompt.background</c>
/// task ids, and the <c>btw.complete</c> / <c>background.complete</c> events. Synthetic gateway only.
/// </summary>
public class Checkpoint17MidTurnTests
{
    private static readonly ProfileKey Profile = new("gw1", "default");

    private static JsonElement ParamsOf(FakeRpcTransport s, string method)
    {
        using var doc = JsonDocument.Parse(s.FirstCall(method)!);
        return doc.RootElement.GetProperty("params").Clone();
    }

    // ---- steer -----------------------------------------------------------------------------------

    [Fact]
    public async Task Steer_accepted_when_the_gateway_says_queued()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "session.steer" ? """{"status":"queued"}""" : null);
        await using var _t = t;
        var outcome = await t.SteerAsync(Profile, "rt-1", "use the other file", default);
        stop.Cancel();
        Assert.Equal(SteerOutcome.Accepted, outcome);
        var p = ParamsOf(s, "session.steer");
        Assert.Equal("rt-1", p.GetProperty("session_id").GetString());
        Assert.Equal("use the other file", p.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"status":"rejected"}""")]
    [InlineData("""{"status":"idle"}""")]
    [InlineData("""{}""")]
    public async Task Any_other_steer_status_is_rejected_so_the_caller_can_queue_instead(string result)
    {
        var (t, _, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "session.steer" ? result : null);
        await using var _t = t;
        Assert.Equal(SteerOutcome.Rejected, await t.SteerAsync(Profile, "rt-1", "x", default));
        stop.Cancel();
    }

    [Theory]
    [InlineData(-32601)]
    [InlineData(4010)]
    public async Task Gateways_that_cannot_steer_are_reported_unsupported_not_failed(int code)
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "session.steer" ? $"ERR:{code}:no steer" : null);
        await using var _t = t;
        Assert.Equal(SteerOutcome.Unsupported, await t.SteerAsync(Profile, "rt-1", "x", default));
        stop.Cancel();
        Assert.Equal(1, s.CountCalls("session.steer"));            // never retried
    }

    [Fact]
    public async Task Other_steer_errors_propagate()
    {
        var (t, _, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "session.steer" ? "ERR:5000:kaput" : null);
        await using var _t = t;
        var ex = await Assert.ThrowsAsync<RpcException>(() => t.SteerAsync(Profile, "rt-1", "x", default));
        Assert.Equal(5000, ex.Code);
        stop.Cancel();
    }

    [Fact]
    public async Task Steer_for_another_profiles_session_is_refused_before_any_request()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "prompt.submit" ? """{"status":"streaming"}""" : null);
        await using var _t = t;
        await foreach (var _ in t.StreamChatAsync(new ChatRequest(Profile, new[] { new ChatMessage("user", "hi") }, "s1"), default)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.SteerAsync(new ProfileKey("gw1", "other"), "s1", "x", default));
        stop.Cancel();
        Assert.Equal(0, s.CountCalls("session.steer"));
    }

    // ---- side tasks ------------------------------------------------------------------------------

    [Theory]
    [InlineData(SideTaskKind.Btw, "prompt.btw")]
    [InlineData(SideTaskKind.Background, "prompt.background")]
    public async Task Side_tasks_use_their_own_method_and_return_the_task_id(SideTaskKind kind, string method)
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == method ? """{"task_id":"task-9"}""" : null);
        await using var _t = t;
        var id = await t.StartSideTaskAsync(Profile, "rt-1", kind, "what is 2+2", default);
        stop.Cancel();
        Assert.Equal("task-9", id);
        var p = ParamsOf(s, method);
        Assert.Equal("rt-1", p.GetProperty("session_id").GetString());
        Assert.Equal("what is 2+2", p.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_side_task_without_an_id_returns_null_and_a_missing_method_propagates()
    {
        var (t, _, stop) = await Checkpoint16RewindTests.Start((m, _) => m switch
        {
            "prompt.btw" => "{}",
            "prompt.background" => "ERR:-32601:nope",
            _ => null
        });
        await using var _t = t;
        Assert.Null(await t.StartSideTaskAsync(Profile, "rt-1", SideTaskKind.Btw, "q", default));
        var ex = await Assert.ThrowsAsync<RpcException>(() => t.StartSideTaskAsync(Profile, "rt-1", SideTaskKind.Background, "q", default));
        Assert.Equal(-32601, ex.Code);
        stop.Cancel();
    }

    // ---- events ----------------------------------------------------------------------------------

    private static GatewayEvent Ev(string type, string json, string? session = "rt-1") =>
        new(type, System.Text.Json.Nodes.JsonNode.Parse(json), session, null);

    [Fact]
    public void Btw_and_background_completions_parse_with_task_id_text_and_question()
    {
        Assert.True(SideTaskEvents.TryParse(Ev("btw.complete", """{"task_id":"t1","text":" 4 ","question":"2+2?"}"""), out var btw));
        Assert.Equal(SideTaskKind.Btw, btw.Kind);
        Assert.Equal("t1", btw.TaskId);
        Assert.Equal("4", btw.Text);
        Assert.Equal("2+2?", btw.Question);
        Assert.False(btw.Failed);

        Assert.True(SideTaskEvents.TryParse(Ev("background.complete", """{"task_id":"t2","text":"done"}"""), out var bg));
        Assert.Equal(SideTaskKind.Background, bg.Kind);
        Assert.Null(bg.Question);
    }

    [Fact]
    public void A_crashed_side_task_is_reported_as_failed_and_blank_or_foreign_events_are_ignored()
    {
        Assert.True(SideTaskEvents.TryParse(Ev("btw.complete", """{"task_id":"t","text":"error: boom"}"""), out var failed));
        Assert.True(failed.Failed);
        Assert.False(SideTaskEvents.TryParse(Ev("btw.complete", """{"task_id":"t","text":"   "}"""), out _));
        Assert.False(SideTaskEvents.TryParse(Ev("btw.complete", """{"task_id":"t"}"""), out _));
        Assert.False(SideTaskEvents.TryParse(Ev("message.complete", """{"text":"x"}"""), out _));
        Assert.False(SideTaskEvents.TryParse(new GatewayEvent("btw.complete", null, "rt", null), out _));
    }
}
