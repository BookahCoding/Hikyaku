using System.Text.Json.Nodes;
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
    public void A_status_that_says_failed_is_failed_and_a_suppressed_or_gated_run_is_silent()
    {
        var monitor = CronRunText.Parse("# Cron Job: watch\n\n**Job ID:** j\n**Run Time:** t\n**Mode:** monitor\n**Status:** monitor source failed\n\nconnection reset\n");
        Assert.Equal(RunStatus.Failed, monitor.Status);
        Assert.Equal("monitor source failed", monitor.Reason);
        Assert.Contains("connection reset", monitor.Text);

        var folded = CronRunText.Parse("# Cron Job: watch **Job ID:** j **Run Time:** t **Mode:** monitor **Status:** no_change (agent run suppressed)");
        Assert.Equal(new RunText(RunStatus.Silent, "", null), folded);

        var gate = CronRunText.Parse("# Cron Job: digest\n\n**Job ID:** j1\n**Run Time:** 2026-10-28 09:50:49\n\nScript gate returned `wakeAgent=false` — agent skipped.\n");
        Assert.Equal(RunStatus.Silent, gate.Status);
        Assert.Equal("", gate.Text);

        var broken = CronRunText.Parse("# Cron Job: digest\n\nError: config.yaml is not valid\n");
        Assert.Equal(RunStatus.Failed, broken.Status);
        Assert.Contains("config.yaml is not valid", broken.Text);
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

    // ---- JobRunDetails ----

    private static CronJob Job(string id = "j1") =>
        new(id, "mail", "/bookahmail-mail", "every 1h", "scheduled", true, null, null, "ok", null, null, null, null, "default", null, null, "every 1h");

    private static JobResult AgentRun(string sessionId = "s1") => new(Job(), new SessionSummary(sessionId, "run", "/bookahmail-mail", "cron"), false);

    private static JobResult ScriptRun(string preview = "# Cron Job: mail **Job ID:** j1 **Run Time:** now **Mode:** no_agent (script) --- short preview") =>
        new(Job(), new SessionSummary("cron_output:j1:2026-10-28_09-50-49", preview, preview, "cron_output"), false);

    private sealed class FakeRpc(Func<string, JsonObject?, Task<JsonNode?>> answer) : IJsonRpcPeer
    {
        public readonly List<string> Args = new();
        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Assert.Equal("command.dispatch", method);
            Args.Add(parameters!["name"]!.GetValue<string>() + " " + parameters["arg"]!.GetValue<string>());
            return answer(method, parameters);
        }
    }

    private static Task<JsonNode?> Plugin(string output) => Task.FromResult<JsonNode?>(new JsonObject { ["type"] = "plugin", ["output"] = output });

    [Fact]
    public async Task An_agent_runs_result_is_its_last_answer_and_none_yet_means_no_detail()
    {
        IReadOnlyList<SessionMessage> rows =
        [
            new("1", "user", "/bookahmail-mail"), new("2", "assistant", "Checking."), new("3", "tool", "{}"),
            new("4", "assistant", "You have 2 important mails."), new("5", "assistant", "  ")
        ];
        var details = new JobRunDetails((id, profile, limit, ct) => Task.FromResult(rows), () => null);
        var d = await details.LoadAsync(AgentRun(), default);
        Assert.Equal(new RunText(RunStatus.Ok, "You have 2 important mails.", null), d!.Result);
        Assert.False(d.FromPlugin);

        var none = new JobRunDetails((id, profile, limit, ct) => Task.FromResult<IReadOnlyList<SessionMessage>>([new("1", "user", "go")]), () => null);
        Assert.Null(await none.LoadAsync(AgentRun(), default));
    }

    [Fact]
    public async Task A_script_runs_output_comes_from_the_plugin_when_it_is_there()
    {
        var rpc = new FakeRpc((_, _) => Plugin("""{"ok":true,"text":"# Cron Job: mail\n\n**Job ID:** j1\n**Run Time:** now\n**Mode:** no_agent (script)\n\n---\n\nThe whole output.\n","truncated":false}"""));
        var details = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException("not a chat"), () => rpc);
        var d = await details.LoadAsync(ScriptRun(), default);
        Assert.Equal("The whole output.", d!.Result.Text);
        Assert.True(d.FromPlugin);
        Assert.Equal(["hikyaku-cron output default j1 2026-10-28_09-50-49"], rpc.Args);
        Assert.True(details.PluginPresent);
    }

    [Fact]
    public async Task Without_the_plugin_the_cleaned_preview_is_used_and_the_absence_is_remembered()
    {
        var rpc = new FakeRpc((_, _) => throw new RpcException(InboxApi.NotACommand, "not a command"));
        var details = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => rpc);
        var d = await details.LoadAsync(ScriptRun(), default);
        Assert.Equal("short preview", d!.Result.Text);
        Assert.False(d.FromPlugin);
        Assert.False(details.PluginPresent);
        await details.LoadAsync(ScriptRun(), default);
        Assert.Single(rpc.Args);                                               // not asked again

        var garbled = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => new FakeRpc((_, _) => Plugin("not json")));
        Assert.Equal("short preview", (await garbled.LoadAsync(ScriptRun(), default))!.Result.Text);
        Assert.False(garbled.PluginPresent);

        var missing = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => new FakeRpc((_, _) => Plugin("""{"ok":false,"error":"not_found"}""")));
        Assert.Equal("short preview", (await missing.LoadAsync(ScriptRun(), default))!.Result.Text);
        Assert.True(missing.PluginPresent);                                    // the plugin is there; this run's file is not
    }

    [Fact]
    public async Task A_plugin_that_does_not_answer_in_time_is_treated_as_absent()
    {
        var rpc = new FakeRpc((_, _) => throw new RpcTimeoutException("command.dispatch"));
        var details = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => rpc);
        var d = await details.LoadAsync(ScriptRun(), default);
        Assert.Equal("short preview", d!.Result.Text);
        Assert.False(details.PluginPresent);
        await details.LoadAsync(ScriptRun(), default);
        Assert.Single(rpc.Args);                                                 // not asked again on every rebuild
    }

    [Fact]
    public async Task A_dropped_connection_does_not_mark_the_plugin_absent()
    {
        var calls = 0;
        var rpc = new FakeRpc((_, _) =>
        {
            calls++;
            if (calls == 1) throw new ObjectDisposedException("JsonRpcClient");
            return Plugin("""{"ok":true,"text":"# Cron Job: mail\n\n---\n\nThe whole output.\n","truncated":false}""");
        });
        var details = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => rpc);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => details.LoadAsync(ScriptRun(), default));
        Assert.Null(details.PluginPresent);
        var d = await details.LoadAsync(ScriptRun(), default);
        Assert.Equal("The whole output.", d!.Result.Text);
        Assert.True(d.FromPlugin);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Another_gateways_error_does_not_mark_the_plugin_absent()
    {
        var calls = 0;
        var rpc = new FakeRpc((_, _) =>
        {
            calls++;
            if (calls == 1) throw new RpcException(-32000, "internal");
            return Plugin("""{"ok":true,"text":"# Cron Job: mail\n\n---\n\nThe whole output.\n","truncated":false}""");
        });
        var details = new JobRunDetails((_, _, _, _) => throw new InvalidOperationException(), () => rpc);
        await Assert.ThrowsAsync<RpcException>(() => details.LoadAsync(ScriptRun(), default));
        Assert.Null(details.PluginPresent);
        Assert.True((await details.LoadAsync(ScriptRun(), default))!.FromPlugin);
    }

    [Fact]
    public async Task An_agent_that_chose_not_to_reply_is_nothing_new()
    {
        JobRunDetails Details(string content) => new((id, profile, limit, ct) =>
            Task.FromResult<IReadOnlyList<SessionMessage>>([new("1", "assistant", content)]), () => null);

        Assert.Equal(RunStatus.Silent, (await Details("[SILENT]").LoadAsync(AgentRun(), default))!.Result.Status);
        Assert.Equal("", (await Details("[SILENT]").LoadAsync(AgentRun(), default))!.Result.Text);
        Assert.Equal(RunStatus.Silent, (await Details("NO_REPLY").LoadAsync(AgentRun(), default))!.Result.Status);
        Assert.Equal(RunStatus.Silent, (await Details("no reply").LoadAsync(AgentRun(), default))!.Result.Status);

        var prose = await Details("The report mentions NO_REPLY only in passing.").LoadAsync(AgentRun(), default);
        Assert.Equal(RunStatus.Ok, prose!.Result.Status);
        Assert.Contains("NO_REPLY", prose.Result.Text);
    }

    // ---- InboxReadStore ----

    private sealed class MemPrefs : IPreferenceStore
    {
        public string? Content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
        public Task SaveAsync(string content, CancellationToken ct = default) { Content = content; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Read_marks_survive_a_reload_and_stay_with_their_gateway()
    {
        var file = new MemPrefs();
        var reads = new InboxReadStore(file);
        await reads.LoadAsync();
        await reads.MarkReadAsync("home", ["default/j1/s1", "default/j1/s2"]);
        var again = new InboxReadStore(file);
        await again.LoadAsync();
        Assert.True(again.IsRead("home", "default/j1/s1"));
        Assert.False(again.IsRead("work", "default/j1/s1"));                 // the same job ids on another gateway
        Assert.False(again.IsRead("home", "default/j1/s3"));
    }

    [Fact]
    public async Task Only_the_newest_marks_are_kept_and_a_corrupt_file_reads_as_none()
    {
        var file = new MemPrefs();
        var reads = new InboxReadStore(file);
        await reads.MarkReadAsync("home", Enumerable.Range(0, InboxReadStore.Max + 10).Select(i => "run" + i));
        Assert.False(reads.IsRead("home", "run0"));
        Assert.True(reads.IsRead("home", "run" + (InboxReadStore.Max + 9)));

        var corrupt = new InboxReadStore(new MemPrefs { Content = "{not json" });
        await corrupt.LoadAsync();
        Assert.False(corrupt.IsRead("home", "run1"));
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
