using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16d — live session status for the Running filter (BookahHermes ActiveSessions.kt @ 5f00986b):
/// <c>session.active_list</c> rows <c>{session_key, id, status, title}</c>, polling, event nudges, and the
/// METHOD_NOT_FOUND "stop asking" rule. Deterministic delays; no live gateway.
/// </summary>
public class Checkpoint16LiveSessionsTests
{
    private static JsonNode Rows(params (string? Key, string? Id, string? Status, string? Title)[] rows) =>
        new JsonObject
        {
            ["sessions"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["session_key"] = r.Key, ["id"] = r.Id, ["status"] = r.Status, ["title"] = r.Title
            }).ToArray())
        };

    // ---- parser ----------------------------------------------------------------------------------

    [Fact]
    public void Parser_maps_statuses_and_runtime_ids_by_stored_session_key()
    {
        var parsed = ActiveListParser.Parse(Rows(
            ("a", "rt-a", "working", "Chat A"), ("b", "rt-b", "waiting", null), ("c", "rt-c", "streaming", ""),
            ("d", "rt-d", "resuming", "D"), ("e", "rt-e", "starting", "E"), ("f", "rt-f", "idle", "F"),
            ("g", "rt-g", "who-knows", "G")));
        Assert.Equal(LiveStatus.Working, parsed["a"].Status);
        Assert.Equal("rt-a", parsed["a"].RuntimeId);
        Assert.Equal(LiveStatus.Waiting, parsed["b"].Status);
        Assert.Equal("", parsed["b"].Title);
        Assert.Equal(LiveStatus.Working, parsed["c"].Status);      // streaming / resuming are a turn going
        Assert.Equal(LiveStatus.Working, parsed["d"].Status);
        Assert.Equal(LiveStatus.Starting, parsed["e"].Status);
        Assert.Equal(LiveStatus.Idle, parsed["f"].Status);
        Assert.Equal(LiveStatus.Idle, parsed["g"].Status);         // unknown wire value is not "running"
    }

    [Fact]
    public void Only_working_and_waiting_count_as_running_not_starting()
    {
        Assert.True(LiveStatus.Working.IsRunning());
        Assert.True(LiveStatus.Waiting.IsRunning());
        Assert.False(LiveStatus.Starting.IsRunning());             // a cold resume builds an agent: not a turn
        Assert.False(LiveStatus.Idle.IsRunning());
    }

    [Fact]
    public void Parser_skips_rows_without_a_key_and_tolerates_junk()
    {
        var parsed = ActiveListParser.Parse(Rows((null, "rt", "working", "x"), ("", "rt2", "working", "y"), ("k", null, "working", "z")));
        Assert.Equal(new[] { "k" }, parsed.Keys);
        Assert.Equal("k", parsed["k"].RuntimeId);                  // falls back to the key when no id
        Assert.Empty(ActiveListParser.Parse(null));
        Assert.Empty(ActiveListParser.Parse(new JsonObject()));
        Assert.Empty(ActiveListParser.Parse(new JsonObject { ["sessions"] = "nope" }));
        Assert.Empty(ActiveListParser.Parse(new JsonObject { ["sessions"] = new JsonArray(1, "x", null) }));
    }

    // ---- poller ----------------------------------------------------------------------------------

    private sealed class Harness
    {
        public readonly List<TaskCompletionSource> Waits = new();
        public int Asks;
        public Func<int, JsonNode?> Answer = _ => Rows(("a", "rt-a", "working", "A"));
        public Exception? Throw;

        public LiveSessionsPoller Poller() => new(
            async ct =>
            {
                Interlocked.Increment(ref Asks);
                await Task.Yield();
                if (Throw is not null) throw Throw;
                return Answer(Asks);
            },
            poll: TimeSpan.FromSeconds(10), settle: TimeSpan.FromMilliseconds(500),
            clock: () => DateTimeOffset.Parse("2026-10-26T12:00:00Z"),
            delay: (d, ct) =>
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled());
                lock (Waits) Waits.Add(tcs);
                return tcs.Task;
            });

        public async Task<bool> WaitForAsksAsync(int n)
        {
            for (var i = 0; i < 400 && Volatile.Read(ref Asks) < n; i++) await Task.Delay(5);
            return Volatile.Read(ref Asks) >= n;
        }

        /// <summary>The poller has registered its delay. Firing before that is a no-op and the next ask never starts.</summary>
        public async Task<bool> WaitUntilParkedAsync()
        {
            for (var i = 0; i < 400; i++)
            {
                lock (Waits)
                    if (Waits.Count > 0) return true;
                await Task.Delay(5);
            }
            return false;
        }

        public void FireTimer() { lock (Waits) { foreach (var w in Waits.ToArray()) w.TrySetResult(); Waits.Clear(); } }
    }

    [Fact]
    public async Task Start_asks_publishes_the_snapshot_and_raises_changed_once_per_change()
    {
        var h = new Harness();
        using var p = h.Poller();
        var changed = 0;
        p.Changed += () => Interlocked.Increment(ref changed);
        Assert.False(p.Current.HasAnswer);
        p.Start();
        Assert.True(await h.WaitForAsksAsync(1));
        Assert.True(await h.WaitUntilParkedAsync());
        Assert.True(p.Current.HasAnswer);
        Assert.Equal(LiveStatus.Working, p.Current.Rows["a"].Status);
        Assert.Equal(1, changed);

        h.FireTimer();                                       // same answer again: no UI churn
        Assert.True(await h.WaitForAsksAsync(2));
        Assert.True(await h.WaitUntilParkedAsync());
        Assert.Equal(1, changed);

        h.Answer = _ => Rows(("a", "rt-a", "idle", "A"));
        h.FireTimer();
        Assert.True(await h.WaitForAsksAsync(3));
        Assert.True(await h.WaitUntilParkedAsync());
        Assert.Equal(2, changed);
        Assert.Equal(LiveStatus.Idle, p.Current.Rows["a"].Status);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_last_answer_and_recovers()
    {
        var h = new Harness();
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsksAsync(1));
        Assert.True(await h.WaitUntilParkedAsync());

        h.Throw = new HttpRequestException("blip");
        h.FireTimer();
        Assert.True(await h.WaitForAsksAsync(2));
        Assert.True(await h.WaitUntilParkedAsync());
        Assert.NotNull(p.Current.Error);
        Assert.Equal(LiveStatus.Working, p.Current.Rows["a"].Status);   // last answer stands
        Assert.False(p.Current.Unsupported);

        h.Throw = null;
        h.FireTimer();
        Assert.True(await h.WaitForAsksAsync(3));
        Assert.True(await h.WaitUntilParkedAsync());
        Assert.Null(p.Current.Error);
    }

    [Fact]
    public async Task A_gateway_without_the_method_is_reported_unsupported_and_never_asked_again()
    {
        var h = new Harness { Throw = new RpcException(-32601, "method not found") };
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsksAsync(1));
        await Task.Delay(30);
        Assert.True(p.Current.Unsupported);
        h.FireTimer();
        p.Nudge();
        await Task.Delay(100);
        Assert.Equal(1, h.Asks);
    }

    [Fact]
    public async Task A_nudge_asks_again_after_the_settle_delay_without_waiting_for_the_poll_interval()
    {
        var h = new Harness();
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsksAsync(1));
        await Task.Delay(30);
        p.Nudge();                                           // e.g. message.complete from another client
        await Task.Delay(30);
        h.FireTimer();                                       // fires the poll wait AND the settle wait in turn
        for (var i = 0; i < 50 && h.Asks < 2; i++) { await Task.Delay(10); h.FireTimer(); }
        Assert.True(h.Asks >= 2);
    }

    [Fact]
    public async Task Stop_ends_polling_and_start_twice_runs_one_loop()
    {
        var h = new Harness();
        using var p = h.Poller();
        p.Start();
        p.Start();
        Assert.True(await h.WaitForAsksAsync(1));
        await Task.Delay(30);
        Assert.Equal(1, h.Asks);                             // not two loops
        p.Stop();
        h.FireTimer();
        await Task.Delay(80);
        Assert.Equal(1, h.Asks);
        p.Start();                                           // restartable
        Assert.True(await h.WaitForAsksAsync(2));
    }

    [Fact]
    public async Task Stop_discards_an_answer_that_arrives_afterwards()
    {
        var gate = new TaskCompletionSource<JsonNode?>();
        using var p = new LiveSessionsPoller(_ => gate.Task, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        p.Start();
        await Task.Delay(30);
        p.Stop();
        gate.SetResult(Rows(("late", "rt", "working", "x")));
        await Task.Delay(50);
        Assert.False(p.Current.HasAnswer);                   // a stopped poller never publishes stale rows
    }

    [Fact]
    public void Clear_forgets_everything_so_nothing_old_shows_as_live()
    {
        var p = new LiveSessionsPoller(_ => Task.FromResult<JsonNode?>(Rows(("a", "r", "working", "A"))));
        p.Start();
        for (var i = 0; i < 100 && !p.Current.HasAnswer; i++) Thread.Sleep(10);
        Assert.True(p.Current.HasAnswer);
        p.Stop();
        p.Clear();
        Assert.False(p.Current.HasAnswer);
        Assert.Empty(p.Current.Rows);
        p.Dispose();
    }
}
