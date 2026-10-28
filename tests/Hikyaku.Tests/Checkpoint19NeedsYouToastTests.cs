using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 19b — a toast when a Needs-you request arrives: kind and profile only (never the command, question or secret prompt),
/// profile-gated by the service, throttled, silent while the app is in front, and never repeated for a request already announced.
/// </summary>
public class Checkpoint19NeedsYouToastTests
{
    private sealed class Store : IPreferenceStore
    {
        private string? _c;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_c);
        public Task SaveAsync(string content, CancellationToken ct = default) { _c = content; return Task.CompletedTask; }
    }

    private static async Task<(NeedsYouService Needs, List<(ProfileKey, string, string)> Toasts, ProfileSelectionService Selection, Box Box)> Make(
        bool throttle = true)
    {
        var selection = new ProfileSelectionService(new Store());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default"), new ProfileKey("gw1", "work") });
        await selection.LoadAsync();
        var needs = new NeedsYouService(new NotificationGate(selection), selection, "gw1", () => null);
        var toasts = new List<(ProfileKey, string, string)>();
        var box = new Box();
        var notifier = new NeedsYouNotifier(needs,
            (p, t, b) => { toasts.Add((p, t, b)); return new ToastDeliveryResult(ToastObservation.Observed, "ok"); },
            suppressWhile: () => box.AppActive,
            throttle: throttle ? new ToastThrottle(TimeSpan.FromSeconds(15), clock: () => box.Clock) : new ToastThrottle(TimeSpan.Zero, int.MaxValue));
        box.Notifier = notifier;
        return (needs, toasts, selection, box);
    }

    private sealed class Box
    {
        public DateTimeOffset Clock = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        public bool AppActive;
        public NeedsYouNotifier? Notifier;
    }

    private static GatewayEvent Request(string id, string method, string json) =>
        new("server.request:" + method, JsonNode.Parse(json) as JsonObject, "sess-" + id, null, id);

    [Fact]
    public async Task An_approval_request_toasts_the_kind_and_profile_but_never_the_command()
    {
        var (needs, toasts, _, box) = await Make();
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"rm -rf /secret/path","description":"delete the vault","profile":"work"}"""));
        var t = Assert.Single(toasts);
        Assert.Equal(new ProfileKey("gw1", "work"), t.Item1);
        Assert.Equal("Hermes needs you", t.Item2);
        Assert.Contains("Approval", t.Item3);
        Assert.Contains("work", t.Item3);
        Assert.DoesNotContain("rm -rf", t.Item2 + t.Item3);
        Assert.DoesNotContain("vault", t.Item2 + t.Item3);
        GC.KeepAlive(box);
    }

    [Theory]
    [InlineData("clarify", """{"questions":[{"qid":"q1","question":"Which private account?","choices":["a","b"]}],"profile":"default"}""", "question")]
    [InlineData("secret", """{"env_var":"STRIPE_KEY","prompt":"Paste the live key","profile":"default"}""", "Input")]
    public async Task Questions_and_secret_prompts_say_only_what_kind_of_attention_is_needed(string method, string json, string word)
    {
        var (needs, toasts, _, _) = await Make();
        needs.HandleGatewayEvent(Request("r", method, json));
        var t = Assert.Single(toasts);
        Assert.Contains(word, t.Item3, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private account", t.Item2 + t.Item3);
        Assert.DoesNotContain("STRIPE", t.Item2 + t.Item3);
        Assert.DoesNotContain("live key", t.Item2 + t.Item3);
    }

    [Fact]
    public async Task A_burst_is_one_toast_then_a_later_request_toasts_again()
    {
        var (needs, toasts, _, box) = await Make();
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}"""));
        needs.HandleGatewayEvent(Request("r2", "approval", """{"command":"b","profile":"default"}"""));
        needs.HandleGatewayEvent(Request("r3", "approval", """{"command":"c","profile":"default"}"""));
        Assert.Single(toasts);                                            // coalesced; the others are in the Needs-you list
        box.Clock = box.Clock.AddSeconds(20);
        needs.HandleGatewayEvent(Request("r4", "approval", """{"command":"d","profile":"default"}"""));
        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public async Task A_request_already_announced_is_not_announced_again_on_unrelated_changes()
    {
        var (needs, toasts, _, box) = await Make(throttle: false);
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}"""));
        needs.HandleGatewayEvent(Request("r2", "approval", """{"command":"b","profile":"default"}"""));   // r1 still pending
        Assert.Equal(2, toasts.Count);                                    // r2's arrival announces r2 only
        needs.Clear("r2");                                                // a Changed with nothing new
        Assert.Equal(2, toasts.Count);
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}"""));  // same id re-delivered
        Assert.Equal(2, toasts.Count);
        GC.KeepAlive(box);
    }

    [Fact]
    public async Task While_the_app_is_in_front_nothing_pops_up_but_the_request_is_still_pending()
    {
        var (needs, toasts, _, box) = await Make();
        box.AppActive = true;
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}"""));
        Assert.Empty(toasts);
        Assert.Single(needs.Pending);
        box.AppActive = false;
        needs.HandleGatewayEvent(Request("r2", "approval", """{"command":"b","profile":"default"}"""));
        Assert.Single(toasts);                                            // the in-front one is not announced late
        Assert.Contains("Approval", toasts[0].Item3);
    }

    [Fact]
    public async Task Deselected_profiles_and_unattributable_requests_never_toast()
    {
        var (needs, toasts, selection, _) = await Make(throttle: false);
        needs.HandleGatewayEvent(Request("r0", "approval", """{"command":"a"}"""));          // no profile in the payload
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"work"}"""));   // work was deselected
        Assert.Empty(toasts);
        Assert.DoesNotContain(needs.Pending, r => r.Profile?.ProfileId == "work");
    }

    [Fact]
    public async Task Several_new_requests_at_once_are_summarised_by_count_for_one_profile()
    {
        var (needs, toasts, _, _) = await Make(throttle: false);
        var notifier = new NeedsYouNotifier(needs, (p, t, b) => { toasts.Add((p, t, b)); return new ToastDeliveryResult(ToastObservation.Observed, "ok"); },
            () => false, new ToastThrottle(TimeSpan.Zero, int.MaxValue));
        toasts.Clear();
        notifier.Announce(new NeedsYouRequest[]
        {
            new ApprovalRequest("a", null, new ProfileKey("gw1", "work"), "x", "", null, []),
            new ClarifyRequest("b", null, new ProfileKey("gw1", "work"), []),
        });
        var t = Assert.Single(toasts);
        Assert.Contains("2 requests", t.Item3);
        Assert.Contains("work", t.Item3);
    }

    [Fact]
    public async Task Disposing_the_notifier_stops_toasts()
    {
        var (needs, toasts, _, box) = await Make();
        box.Notifier!.Dispose();
        needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}"""));
        Assert.Empty(toasts);
    }

    [Fact]
    public async Task A_toast_failure_never_breaks_request_handling()
    {
        var selection = new ProfileSelectionService(new Store());
        await selection.SetSelectedAsync(new[] { new ProfileKey("gw1", "default") });
        await selection.LoadAsync();
        var needs = new NeedsYouService(new NotificationGate(selection), selection, "gw1", () => null);
        using var notifier = new NeedsYouNotifier(needs, (_, _, _) => throw new InvalidOperationException("OS exploded"), () => false,
            new ToastThrottle(TimeSpan.Zero, int.MaxValue));
        Assert.True(needs.HandleGatewayEvent(Request("r1", "approval", """{"command":"a","profile":"default"}""")));
        Assert.Single(needs.Pending);
        Assert.Contains("OS exploded", notifier.LastResult?.Detail ?? "");
    }
}
