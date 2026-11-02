using System.Text.Json;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 43 — live findings on the rebuilt Inbox: script-only runs are output documents, not chats (a click answered 404), and
/// the maintainer asked for categories like the Android app (BookahHermes InboxPage's Folders layout).
/// </summary>
public class Checkpoint43InboxFoldersTests
{
    private static CronJob J(string id, string name, string status = "ok")
    {
        using var doc = JsonDocument.Parse($$"""{"id":"{{id}}","name":"{{name}}","prompt":"p","profile":"default","state":"scheduled","last_status":"{{status}}"}""");
        return CronJob.FromJson(doc.RootElement)!;
    }

    private static JobResult R(CronJob job, string run, long at, bool failed = false, string? source = null) =>
        new(job, new SessionSummary(run, Preview: "preview " + run, Source: source, StartedAt: at), failed);

    [Fact]
    public void A_script_only_run_is_recognised_as_output_not_a_chat()
    {
        // hermes-agent cron.py: script-only fires are "cron_output:<job>:<stamp>" rows with source "cron_output".
        var job = J("j1", "homelab-alerts");
        Assert.True(R(job, "cron_output:j1:2026-10-27_21-00-00", 1, source: "cron_output").IsScriptOutput);
        Assert.True(R(job, "cron_output:j1:latest", 1).IsScriptOutput);                // even without the source field
        Assert.False(R(job, "cron_j1_20261027_210000", 1, source: "cron").IsScriptOutput);
        Assert.False(R(job, "local-7f3a", 1, source: "cron").IsScriptOutput);
    }

    [Fact]
    public void Folders_put_failures_first_then_groups_and_jobs_most_recent_first()
    {
        var mail = J("m", "mail-reader");
        var alerts = J("a", "homelab-alerts", "error");
        var kanban = J("k", "kanban-changes");
        var results = new[] { R(mail, "m1", 100), R(alerts, "a1", 300, failed: true), R(alerts, "a0", 50), R(kanban, "k1", 200) };
        var groups = InboxGroups.Empty.WithJob(JobInbox.KeyOf(alerts), "Home").WithJob(JobInbox.KeyOf(kanban), "Home");

        var folders = InboxFolders.Of(results, groups);
        Assert.Equal(["Failed", "Home", "mail-reader"], folders.Select(f => f.Name));
        Assert.Equal(3, folders[1].Count);                                              // both jobs of the group
        Assert.Equal(InboxFolderKind.Job, folders[2].Kind);
        Assert.Equal(JobInbox.KeyOf(mail), folders[2].JobKey);

        Assert.Equal(["a1", "k1", "a0"], InboxFolders.In(results, InboxFolders.GroupKey("Home"), groups).Select(r => r.Run.Id));
        Assert.Equal(["a1"], InboxFolders.In(results, InboxFolders.FailedKey, groups).Select(r => r.Run.Id));
        Assert.Equal(["m1"], InboxFolders.In(results, InboxFolders.JobKey(JobInbox.KeyOf(mail)), groups).Select(r => r.Run.Id));
        Assert.Empty(InboxFolders.In(results, InboxFolders.JobKey(JobInbox.KeyOf(alerts)), groups));   // it lives in its group now
    }

    [Fact]
    public void No_failures_means_no_failed_folder()
    {
        var job = J("m", "mail-reader");
        Assert.DoesNotContain(InboxFolders.Of([R(job, "m1", 1)], InboxGroups.Empty), f => f.Kind == InboxFolderKind.Failed);
    }
    private sealed class FolderMemory : IPreferenceStore
    {
        private string? _value;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_value);
        public Task SaveAsync(string value, CancellationToken ct = default) { _value = value; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Named_empty_folders_are_visible_and_remembered()
    {
        var groups = new InboxGroups(["Reports"], new Dictionary<string, string>());
        var folder = Assert.Single(InboxFolders.Of([], groups));
        Assert.Equal("Reports", folder.Name);
        Assert.Equal(0, folder.Count);
        var store = new FolderMemory();
        var persisted = new InboxGroupsStore(store);
        await persisted.SaveAsync("gw", groups);
        Assert.Equal("Reports", Assert.Single((await persisted.LoadAsync("gw")).Names));
        Assert.Empty((await persisted.LoadAsync("other")).Names);
    }
    [Fact]
    public void A_folder_assignment_cannot_move_a_same_id_job_of_another_profile()
    {
        var first = J("same", "Default report");
        using var doc = JsonDocument.Parse("""{"id":"same","name":"Other report","profile":"other","state":"scheduled"}""");
        var other = CronJob.FromJson(doc.RootElement)!;
        var groups = InboxGroups.Empty.WithJob(JobInbox.KeyOf(first), "Reports");
        var results = new[] { R(first, "r1", 2), R(other, "r2", 1) };
        Assert.Equal("Default report", Assert.Single(InboxFolders.In(results, InboxFolders.GroupKey("Reports"), groups)).Title);
        Assert.Contains(InboxFolders.Of(results, groups), f => f.Name == "Other report" && f.Kind == InboxFolderKind.Job);
    }
    private sealed class GatedStore : IPreferenceStore
    {
        public string? Content;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Saves;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Content);
        public async Task SaveAsync(string value, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Saves) == 1) await Release.Task;
            Content = value;
        }
    }

    [Fact]
    public async Task Concurrent_gateway_saves_preserve_each_gateway()
    {
        var memory = new GatedStore();
        var store = new InboxGroupsStore(memory);
        var a = store.SaveAsync("a", new InboxGroups(["A"], new Dictionary<string, string>()));
        var b = store.SaveAsync("b", new InboxGroups(["B"], new Dictionary<string, string>()));
        memory.Release.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal("A", Assert.Single((await store.LoadAsync("a")).Names));
        Assert.Equal("B", Assert.Single((await store.LoadAsync("b")).Names));
    }
}
