using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 19c — Inbox "Notify" on Windows. Android's rules (Inbox.kt): a job set to Notify notifies for its runs; the failures flag
/// means any failed run notifies; Quiet only lands in the Inbox. Windows has no push channel, so this polls, but only when notifying
/// was asked for. Toasts say the job name, outcome and profile, never the run's output. Deterministic delays; synthetic runs only.
/// </summary>
public class Checkpoint19InboxNotifyTests
{
    private const string Gw = "gw1";

    private static InboxRun Run(string bot, string job, string run, string status = "ok", string label = "", string text = "secret output", long at = 1000) =>
        new(bot, job, run, label, status, at, text);

    private static Following Follow(FollowMode def = FollowMode.Off, bool failures = false, params (string Job, FollowMode Mode)[] jobs) =>
        new(jobs.ToDictionary(j => j.Job, j => j.Mode), failures, def);

    private static InboxSnapshot Snap(IEnumerable<InboxRun> runs, params string[] read) =>
        new(runs.ToList(), new HashSet<string>(read), 0);

    private sealed class Rig
    {
        public readonly List<(ProfileKey Profile, string Title, string Body)> Toasts = new();
        public bool AppActive;
        public InboxNotifier Notifier;

        public Rig(bool throttled = false, Func<ProfileKey, string, string, ToastDeliveryResult>? notify = null)
        {
            Notifier = new InboxNotifier(
                notify ?? ((p, t, b) => { Toasts.Add((p, t, b)); return new ToastDeliveryResult(ToastObservation.Observed, "ok"); }),
                () => AppActive,
                throttled ? new ToastThrottle(TimeSpan.FromMinutes(1)) : new ToastThrottle(TimeSpan.Zero, int.MaxValue));
        }
    }

    // ---- notifier --------------------------------------------------------------------------------

    [Fact]
    public void The_first_refresh_only_learns_what_exists_so_old_runs_never_toast()
    {
        var rig = new Rig();
        rig.Notifier.OnRefreshed(Snap([Run("default", "j", "r1"), Run("default", "j", "r2", "failed")]), Follow(FollowMode.Notify, true), Gw);
        Assert.Empty(rig.Toasts);
    }

    [Fact]
    public void A_new_run_of_a_notify_job_toasts_job_outcome_and_profile_but_never_the_output()
    {
        var rig = new Rig();
        var f = Follow(FollowMode.Quiet, false, ("backup", FollowMode.Notify));
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("work", "backup", "r1", label: "Nightly backup", text: "password=hunter2")]), f, Gw);
        var t = Assert.Single(rig.Toasts);
        Assert.Equal(new ProfileKey(Gw, "work"), t.Profile);
        Assert.Equal(InboxNotifier.Title, t.Title);
        Assert.Contains("Nightly backup", t.Body);
        Assert.Contains("finished", t.Body);
        Assert.Contains("work", t.Body);
        Assert.DoesNotContain("hunter2", t.Title + t.Body);
    }

    [Fact]
    public void A_failed_run_says_failed()
    {
        var rig = new Rig();
        var f = Follow(FollowMode.Notify);
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("default", "j", "r1", "failed")]), f, Gw);
        Assert.Contains("failed", Assert.Single(rig.Toasts).Body);
    }

    [Fact]
    public void Quiet_and_off_jobs_do_not_toast_but_the_failures_flag_makes_any_failed_run_toast()
    {
        var rig = new Rig();
        var quiet = Follow(FollowMode.Quiet);
        rig.Notifier.OnRefreshed(Snap([]), quiet, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("default", "j", "ok1"), Run("default", "j", "bad1", "failed")]), quiet, Gw);
        Assert.Empty(rig.Toasts);                                          // quiet: Inbox only, even for a failure

        var withFailures = Follow(FollowMode.Off, failures: true);
        rig.Notifier.OnRefreshed(Snap([Run("default", "j", "ok1"), Run("default", "j", "bad1", "failed"),
            Run("default", "other", "ok2"), Run("default", "other", "bad2", "failed")]), withFailures, Gw);
        var t = Assert.Single(rig.Toasts);                                 // only the NEW failed run; the new ok run stays quiet
        Assert.Contains("failed", t.Body);
    }

    [Fact]
    public void Runs_are_announced_once_and_runs_you_already_read_are_not_announced()
    {
        var rig = new Rig();
        var f = Follow(FollowMode.Notify);
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        var r1 = Run("default", "j", "r1");
        rig.Notifier.OnRefreshed(Snap([r1]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([r1]), f, Gw);
        Assert.Single(rig.Toasts);
        var r2 = Run("default", "j", "r2");
        rig.Notifier.OnRefreshed(Snap([r1, r2], read: r2.Key), f, Gw);     // read on another client already
        Assert.Single(rig.Toasts);
    }

    [Fact]
    public void Switching_a_job_to_notify_later_does_not_replay_its_history()
    {
        var rig = new Rig();
        var runs = new[] { Run("default", "j", "r1"), Run("default", "j", "r2") };
        rig.Notifier.OnRefreshed(Snap(runs), Follow(FollowMode.Quiet), Gw);
        rig.Notifier.OnRefreshed(Snap(runs), Follow(FollowMode.Notify), Gw);   // now notifying
        Assert.Empty(rig.Toasts);
        rig.Notifier.OnRefreshed(Snap(runs.Append(Run("default", "j", "r3"))), Follow(FollowMode.Notify), Gw);
        Assert.Single(rig.Toasts);
    }

    [Fact]
    public void Several_new_results_for_one_profile_are_one_summary_and_two_profiles_are_two_toasts()
    {
        var rig = new Rig();
        var f = Follow(FollowMode.Notify);
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("a", "j", "1"), Run("a", "j", "2"), Run("a", "k", "3"), Run("b", "j", "4")]), f, Gw);
        Assert.Equal(2, rig.Toasts.Count);
        Assert.Contains("3 new results", rig.Toasts.Single(t => t.Profile.ProfileId == "a").Body);
        Assert.DoesNotContain("new results", rig.Toasts.Single(t => t.Profile.ProfileId == "b").Body);
    }

    [Fact]
    public void While_the_app_is_in_front_nothing_pops_up_and_it_is_not_announced_late()
    {
        var rig = new Rig();
        var f = Follow(FollowMode.Notify);
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        rig.AppActive = true;
        var r1 = Run("default", "j", "r1");
        rig.Notifier.OnRefreshed(Snap([r1]), f, Gw);
        rig.AppActive = false;
        rig.Notifier.OnRefreshed(Snap([r1]), f, Gw);
        Assert.Empty(rig.Toasts);
    }

    [Fact]
    public void A_burst_is_coalesced_by_the_throttle_and_a_failing_toast_never_throws()
    {
        var rig = new Rig(throttled: true);
        var f = Follow(FollowMode.Notify);
        rig.Notifier.OnRefreshed(Snap([]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("a", "j", "1")]), f, Gw);
        rig.Notifier.OnRefreshed(Snap([Run("a", "j", "1"), Run("a", "j", "2")]), f, Gw);
        Assert.Single(rig.Toasts);

        var broken = new Rig(notify: (_, _, _) => throw new InvalidOperationException("OS exploded"));
        broken.Notifier.OnRefreshed(Snap([]), f, Gw);
        broken.Notifier.OnRefreshed(Snap([Run("a", "j", "1")]), f, Gw);   // must not throw
        Assert.Contains("OS exploded", broken.Notifier.LastResult?.Detail ?? "");
    }

    [Fact]
    public void WantsNotifications_is_true_only_for_notify_jobs_a_notify_default_or_the_failures_flag()
    {
        Assert.False(InboxNotifier.WantsNotifications(Follow()));
        Assert.False(InboxNotifier.WantsNotifications(Follow(FollowMode.Quiet, false, ("a", FollowMode.Off))));
        Assert.True(InboxNotifier.WantsNotifications(Follow(FollowMode.Notify)));
        Assert.True(InboxNotifier.WantsNotifications(Follow(FollowMode.Quiet, false, ("a", FollowMode.Notify))));
        Assert.True(InboxNotifier.WantsNotifications(Follow(FollowMode.Off, true)));
    }

    // ---- poller ----------------------------------------------------------------------------------

    private sealed class Harness
    {
        public readonly List<TaskCompletionSource> Waits = new();
        public int Refreshes;
        public Following Following = Follow(FollowMode.Notify);
        public InboxConnectionState State = InboxConnectionState.Ready;
        public Exception? Throw;
        public readonly List<InboxPollResult> Delivered = new();

        public InboxNotifyPoller Poller() => new(
            async ct =>
            {
                Interlocked.Increment(ref Refreshes);
                await Task.Yield();
                if (Throw is not null) throw Throw;
                return new InboxPollResult(Snap([]), Following, State, Gw);
            },
            r => Delivered.Add(r),
            TimeSpan.FromMinutes(2),
            (d, ct) =>
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled());
                lock (Waits) Waits.Add(tcs);
                return tcs.Task;
            });

        public async Task<bool> WaitForAsync(int n)
        {
            for (var i = 0; i < 400 && Volatile.Read(ref Refreshes) < n; i++) await Task.Delay(5);
            return Volatile.Read(ref Refreshes) >= n;
        }

        /// <summary>
        /// Releases the poller's wait once it has actually registered one. A bare <see cref="Tick"/> fired before the loop reaches its
        /// delay is lost, which made the positive cases below depend on timing (they failed under a loaded full run).
        /// </summary>
        public async Task TickAsync()
        {
            for (var i = 0; i < 1000; i++)
            {
                lock (Waits) if (Waits.Count > 0) break;
                await Task.Delay(2);
            }
            Tick();
        }

        public void Tick() { lock (Waits) { foreach (var w in Waits.ToArray()) w.TrySetResult(); Waits.Clear(); } }
    }

    [Fact]
    public async Task The_first_refresh_learns_the_following_and_polling_continues_only_when_notifications_are_wanted()
    {
        var h = new Harness();
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsync(1));
        await h.TickAsync();
        Assert.True(await h.WaitForAsync(2));                               // notify job: keeps polling
        await h.TickAsync();
        Assert.True(await h.WaitForAsync(3));
        Assert.Equal(3, h.Delivered.Count);

        var quiet = new Harness { Following = Follow(FollowMode.Quiet) };
        using var q = quiet.Poller();
        q.Start();
        Assert.True(await quiet.WaitForAsync(1));
        await Task.Delay(30);
        quiet.Tick();
        await Task.Delay(80);
        Assert.Equal(1, quiet.Refreshes);                                   // no notify wanted: no background polling
    }

    [Fact]
    public async Task A_missing_plugin_ends_polling_but_a_dropped_connection_is_retried()
    {
        var h = new Harness { State = InboxConnectionState.SetupRequired };
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsync(1));
        await Task.Delay(30);
        h.Tick();
        await Task.Delay(80);
        Assert.Equal(1, h.Refreshes);                                       // setup required will not fix itself

        var d = new Harness { State = InboxConnectionState.Disconnected };
        using var dp = d.Poller();
        dp.Start();
        Assert.True(await d.WaitForAsync(1));
        await d.TickAsync();
        Assert.True(await d.WaitForAsync(2));                               // retried
    }

    [Fact]
    public async Task A_refresh_that_throws_is_survived_and_retried_while_notifications_are_wanted()
    {
        var h = new Harness();
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsync(1));
        await Task.Delay(30);
        h.Throw = new HttpRequestException("blip");
        await h.TickAsync();
        Assert.True(await h.WaitForAsync(2));
        await Task.Delay(30);
        h.Throw = null;
        await h.TickAsync();
        Assert.True(await h.WaitForAsync(3));
    }

    [Fact]
    public async Task Kick_refreshes_now_and_can_restart_a_stopped_poll_after_the_user_turns_notify_on()
    {
        var h = new Harness { Following = Follow(FollowMode.Quiet) };
        using var p = h.Poller();
        p.Start();
        Assert.True(await h.WaitForAsync(1));
        await Task.Delay(40);
        h.Following = Follow(FollowMode.Notify);                            // user saved a notify follow
        p.Kick();
        Assert.True(await h.WaitForAsync(2));
        await h.TickAsync();
        Assert.True(await h.WaitForAsync(3));                               // and polling resumed
    }

    [Fact]
    public async Task Stop_ends_polling_start_twice_is_one_loop_and_a_late_answer_is_discarded()
    {
        var gate = new TaskCompletionSource<InboxPollResult>();
        var delivered = 0;
        using var p = new InboxNotifyPoller(_ => gate.Task, _ => Interlocked.Increment(ref delivered), TimeSpan.FromMinutes(2),
            (_, ct) => Task.Delay(Timeout.Infinite, ct));
        p.Start();
        p.Start();
        await Task.Delay(30);
        p.Stop();
        gate.SetResult(new InboxPollResult(Snap([]), Follow(FollowMode.Notify), InboxConnectionState.Ready, Gw));
        await Task.Delay(60);
        Assert.Equal(0, delivered);                                         // a stopped poll never delivers a stale answer
    }
}
