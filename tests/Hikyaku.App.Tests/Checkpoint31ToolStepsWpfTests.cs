using System.Net;
using System.Text;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 31 through the app's own mapping: one bubble per reply, tool steps attached, no row shown twice.</summary>
public class Checkpoint31ToolStepsWpfTests
{
    private static SessionMessage Row(string id, string role, string content, IReadOnlyList<StoredToolCall>? calls = null, string? callId = null) =>
        new(id, role, content, ToolCalls: calls, ToolCallId: callId);

    private static readonly SessionMessage[] Turn =
    [
        Row("1", "user", "list files"),
        Row("2", "assistant", "", [new StoredToolCall("c1", "terminal", "ls")]),
        Row("3", "tool", "{\"output\":\"a.txt\"}", callId: "c1"),
        Row("4", "assistant", "There is one file."),
    ];

    [Fact]
    public void A_turn_is_two_bubbles_and_the_reply_carries_its_tool_step()
    {
        var bubbles = HistoryMapping.ToMessages(Turn);
        Assert.Equal(2, bubbles.Count);
        Assert.Equal("You", bubbles[0].Role);
        var reply = bubbles[1];
        Assert.Equal("There is one file.", reply.Text);
        Assert.Equal(4L, reply.RowId);                       // branch cuts after the whole reply
        Assert.Equal([2L, 4L], reply.CoveredRowIds);
        var step = Assert.Single(reply.Tools);
        Assert.Equal("terminal", step.Name);
        Assert.Equal("a.txt", step.Output);
        Assert.False(step.IsExpanded);                       // collapsed until opened
        Assert.True(step.HasInput && step.HasOutput);
    }

    [Fact]
    public void A_failed_tool_says_so_in_words_in_its_header()
    {
        var step = new ToolStepVM("read_file", "x", "denied", failed: true);
        Assert.Equal("read_file (reported an error)", step.Header);
        Assert.Equal("terminal", new ToolStepVM("terminal", null, null, false).Header);
        Assert.False(new ToolStepVM("terminal", null, null, false).HasInput);
    }

    [Fact]
    public void A_user_prompt_keeps_its_own_row_and_is_rewindable()
    {
        var prompt = HistoryMapping.ToMessages(Turn)[0];
        Assert.Equal(1L, prompt.RowId);
        Assert.True(prompt.IsRewindable);
        Assert.Empty(prompt.Tools);
    }

    [Fact]
    public void A_reply_made_only_of_tool_calls_still_appears_with_its_steps()
    {
        var bubbles = HistoryMapping.ToMessages(
        [
            Row("1", "user", "go"),
            Row("2", "assistant", "", [new StoredToolCall("a", "terminal", "ls"), new StoredToolCall("b", "web_search", "q")]),
        ]);
        var reply = bubbles[1];
        Assert.Equal("", reply.Text);
        Assert.Equal(["terminal", "web_search"], reply.Tools.Select(t => t.Name));
        Assert.Equal(System.Windows.Visibility.Collapsed, reply.ReadAloudVisibility);   // no text to read
    }
}
