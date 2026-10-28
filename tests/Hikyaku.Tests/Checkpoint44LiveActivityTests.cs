using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 44 — what the agent does while it answers, from the gateway's live events (BookahHermes ChatReducer.kt @ 5f00986b):
/// reasoning, the live label, the status line, tool steps, a flagged output, the plan and notices. Before this stage the
/// transport dropped every one of them except a one-line tool summary.
/// </summary>
public class Checkpoint44LiveActivityTests
{
    private static List<ChatEvent> From(string type, string json) => LiveEvents.From(type, JsonNode.Parse(json)).ToList();

    [Fact]
    public void Reasoning_streams_and_a_thinking_frame_is_a_label_unless_it_explains_a_provider_wait()
    {
        Assert.Equal("Let me check.", Assert.IsType<ReasoningDelta>(Assert.Single(From("reasoning.delta", """{"text":"Let me check."}"""))).Text);
        Assert.IsType<ReasoningDelta>(Assert.Single(From("reasoning.available", """{"text":"x"}""")));
        Assert.Equal("(>∀<☆)☆ musing...", Assert.IsType<ActivityLabel>(Assert.Single(From("thinking.delta", """{"text":"  (>∀<☆)☆ musing...  "}"""))).Text);
        Assert.IsType<StatusLine>(Assert.Single(From("thinking.delta", """{"text":"⏳ waiting on provider…"}""")));
        Assert.Equal("preparing web_search…", Assert.IsType<ActivityLabel>(Assert.Single(From("tool.generating", """{"name":"web_search"}"""))).Text);
        Assert.Empty(From("reasoning.delta", """{"text":""}"""));
    }

    [Fact]
    public void Tool_steps_carry_their_input_result_failure_summary_and_time()
    {
        var start = Assert.IsType<ToolStarted>(Assert.Single(From("tool.start", """{"tool_id":"t1","name":"terminal","context":"ls","args":{"command":"ls -la"}}""")));
        Assert.Equal(("t1", "terminal", "ls", "ls -la"), (start.Id, start.Name, start.Detail, start.Input));
        Assert.Equal("raw args", Assert.IsType<ToolStarted>(Assert.Single(From("tool.start", """{"tool_id":"t2","name":"x","args_text":"raw args"}"""))).Input);

        var ok = Assert.IsType<ToolFinished>(Assert.Single(From("tool.complete", """{"tool_id":"t1","summary":"listed 3 files","duration_s":0.42,"result":{"output":"a\nb\nc","exit_code":0}}""")));
        Assert.Equal(("listed 3 files", 0.42, "a\nb\nc", false), (ok.Summary, ok.DurationSeconds, ok.Output, ok.Failed));
        var bad = Assert.IsType<ToolFinished>(Assert.Single(From("tool.complete", """{"tool_id":"t2","result_text":"{\"error\":\"permission denied\"}"}""")));
        Assert.True(bad.Failed);
        Assert.Empty(From("tool.complete", """{"name":"no id"}"""));                       // a step without an id matches nothing
    }

    [Fact]
    public void Only_a_high_risk_output_is_flagged_and_the_plan_parses_tolerantly()
    {
        var risk = Assert.IsType<ToolRiskFlagged>(Assert.Single(From("tool.output_risk", """{"tool_id":"t1","risk":"high","findings":["prompt injection"],"redacted":true}""")));
        Assert.Equal(["prompt injection"], risk.Findings);
        Assert.True(risk.Redacted);
        Assert.Empty(From("tool.output_risk", """{"tool_id":"t1","risk":"low"}"""));

        var plan = Assert.IsType<PlanUpdated>(Assert.Single(From("todo.updated", """
            {"revision":3,"todos":[{"id":1,"content":"List","status":"completed"},{"id":"2","content":"Read","status":"in_progress","parent":"1"},
             {"id":"3","content":"Odd","status":"weird"},{"id":"4","content":"Self","status":"pending","parent":"4"}]}
            """))).Plan;
        Assert.Equal(3, plan.Revision);
        Assert.Equal(["1", "2", "4"], plan.Items.Select(i => i.Id));                    // numeric id read, unknown status skipped
        Assert.Null(plan.Items[2].Parent);                                              // its own parent is no parent
        Assert.Equal((1, 3), (plan.Done, plan.Total));
        Assert.Equal([("1", 0), ("2", 1), ("4", 0)], plan.Tree().Select(t => (t.Item.Id, t.Depth)));
        Assert.Empty(From("todo.updated", """{"revision":0,"todos":[]}"""));             // an agent that never planned
    }

    [Fact]
    public void Status_and_notices_come_through_and_a_blank_status_clears()
    {
        Assert.Equal("Compressing context", Assert.IsType<StatusLine>(Assert.Single(From("status.update", """{"text":"Compressing context"}"""))).Text);
        Assert.Equal("", Assert.IsType<StatusLine>(Assert.Single(From("status.update", """{"text":"  "}"""))).Text);
        Assert.Equal("Agent rebuilt", Assert.IsType<AgentNotice>(Assert.Single(From("notice", """{"message":" Agent rebuilt "}"""))).Text);
    }

    [Fact]
    public async Task The_transport_passes_the_whole_turn_on_in_order_with_the_final_reasoning()
    {
        var (t, s, stop) = await Checkpoint16RewindTests.Start((m, _) => m == "prompt.submit" ? """{"status":"streaming"}""" : null, completeTurn: false);
        await using var _t = t;
        var events = new List<ChatEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var e in t.StreamChatAsync(new ChatRequest(new ProfileKey("gw1", "default"), [new ChatMessage("user", "hi")], "s1"), default))
                events.Add(e);
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!s.Sent.Any(x => x.Contains("\"prompt.submit\"")) && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(50);
        string Ev(string type, string payload) => "{\"jsonrpc\":\"2.0\",\"method\":\"event\",\"params\":{\"type\":\"" + type + "\",\"session_id\":\"s1\",\"payload\":" + payload + "}}";
        s.Push(Ev("reasoning.delta", """{"text":"Thinking it through."}"""));
        s.Push(Ev("tool.start", """{"tool_id":"t1","name":"web_search","context":"weather"}"""));
        s.Push(Ev("tool.complete", """{"tool_id":"t1","result":{"output":"sunny"}}"""));
        s.Push(Ev("message.delta", """{"text":"It is sunny."}"""));
        s.Push(Ev("message.complete", """{"text":"It is sunny.","reasoning":"Thinking it through."}"""));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        Assert.Equal([typeof(ReasoningDelta), typeof(ToolActivity), typeof(ToolStarted), typeof(ToolFinished), typeof(TokenDelta), typeof(FinalReasoning), typeof(StreamCompleted)],
            events.Where(e => e is not UserRowBound and not SessionBound).Select(e => e.GetType()));
    }

    [Fact]
    public void A_reopened_chat_keeps_each_steps_reasoning_and_a_step_with_only_reasoning_is_not_lost()
    {
        var rows = new[]
        {
            new SessionMessage("1", "user", "hi"),
            new SessionMessage("2", "assistant", "", ToolCalls: [new StoredToolCall("c1", "terminal", "ls")], Reasoning: "First, list."),
            new SessionMessage("3", "tool", "{\"output\":\"a\"}", ToolCallId: "c1"),
            new SessionMessage("4", "assistant", "Done.", Reasoning: "Then answer."),
            new SessionMessage("5", "user", "again"),
            new SessionMessage("6", "assistant", "", Reasoning: "Only thought."),
        };
        var entries = HistoryReplies.Merge(rows);
        Assert.Equal("First, list.\n\nThen answer.", entries[1].Reasoning);
        Assert.Equal("Only thought.", entries[3].Reasoning);
    }
}
