using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 49 (0.7.0 Inbox results) — what a scheduled run found, read from the run documents hermes-agent writes
/// (cron/scheduler.py, cron/scheduler_prompt.py: <c>_job_doc_header</c>, the <c>---</c> separator, <c>**Status:**</c>, the
/// <c>## Prompt</c> / <c>## Response</c> frames), and from the gateway's whitespace-folded 180-character preview of one.
/// </summary>
public class Checkpoint49InboxResultsTests
{
    private const string Header = "# Cron Job: mail\n\n**Job ID:** abc\n**Run Time:** 2026-10-28T09:50:49\n**Mode:** no_agent (script)\n";

    [Fact]
    public void A_script_runs_output_comes_after_the_separator()
    {
        var r = CronRunText.Parse(Header + "\n---\n\nThree new mails.\n");
        Assert.Equal(new RunText(RunStatus.Ok, "Three new mails.", null), r);
    }

    [Fact]
    public void A_failed_or_blocked_run_is_failed_with_its_reason_and_a_silent_one_is_silent()
    {
        var failed = CronRunText.Parse(Header + "**Status:** script failed\n\nTraceback (most recent call last):\n  boom\n");
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Equal("script failed", failed.Reason);
        Assert.StartsWith("Traceback", failed.Text);

        var blocked = CronRunText.Parse("# Cron Job: mail\n\n**Job ID:** abc\n**Run Time:** now\n**Status:** BLOCKED\n\nThe assembled prompt tripped the scanner.");
        Assert.Equal(RunStatus.Failed, blocked.Status);
        Assert.Equal("BLOCKED", blocked.Reason);

        var silent = CronRunText.Parse(Header + "**Status:** silent (empty output)\n");
        Assert.Equal(new RunText(RunStatus.Silent, "", null), silent);
    }

    [Fact]
    public void An_agent_documents_response_is_its_framed_section()
    {
        const string doc = "# Cron Job: digest\n\n**Job ID:** j1\n**Run Time:** now\n**Schedule:** every 1h\n\n"
                           + "**Prompt Characters:** 5\n## Prompt\n\nhello\n\n**Response Characters:** 11\n## Response\n\nAll is well\n";
        Assert.Equal(new RunText(RunStatus.Ok, "All is well", null), CronRunText.Parse(doc));
        // A response that itself contains a "## Response" heading is cut by its frame length, not by the heading.
        const string tricky = "# Cron Job: d\n\n**Job ID:** j\n**Run Time:** now\n\n**Response Characters:** 24\n## Response\n\nSee ## Response below ok\n";
        Assert.Equal("See ## Response below ok", CronRunText.Parse(tricky).Text);
    }

    [Fact]
    public void The_gateways_folded_preview_loses_its_header_too()
    {
        var r = CronRunText.Parse("# Cron Job: shiwake-private-issue-monitor **Job ID:** 6516cca0727c **Run Time:** 2026-10-28 09:50:49 **Mode:** no_agent (script) --- Found 2 issues…");
        Assert.Equal(new RunText(RunStatus.Ok, "Found 2 issues…", null), r);
        var onlyHeader = CronRunText.Parse("# Cron Job: shiwake-private-issue-monitor **Job ID:** 6516cca0727c **Run Time:** 2026-10-28 09:50:49 **Mode:*…");
        Assert.Equal(RunStatus.Silent, onlyHeader.Status);                  // nothing but a cut-off header
        Assert.Equal("", onlyHeader.Text);
    }

    [Fact]
    public void Anything_else_is_shown_as_it_is_and_nothing_throws()
    {
        Assert.Equal(new RunText(RunStatus.Silent, "", null), CronRunText.Parse(""));
        Assert.Equal(new RunText(RunStatus.Ok, "plain text", null), CronRunText.Parse("plain text"));
        Assert.Equal("/bookahmail-mail", CronRunText.Parse("/bookahmail-mail").Text);
        var huge = new string('x', 2_000_000);
        Assert.Equal(RunStatus.Ok, CronRunText.Parse(huge).Status);
        Assert.Equal(RunStatus.Ok, CronRunText.Parse(Header + "**Response Characters:** 999999\n## Response\n\nshort\n").Status);
    }

    [Fact]
    public void An_excerpt_is_the_first_few_lines_cut_with_an_ellipsis()
    {
        Assert.Equal("one\ntwo\nthree…", CronRunText.Excerpt("one\n\ntwo\nthree\nfour"));
        Assert.Equal("short", CronRunText.Excerpt("short"));
        var cut = CronRunText.Excerpt(new string('a', 400));
        Assert.Equal(280, cut.Length);
        Assert.EndsWith("…", cut);
    }
}
