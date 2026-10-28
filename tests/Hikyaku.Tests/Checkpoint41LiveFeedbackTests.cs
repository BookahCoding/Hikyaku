using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 41 — fixes from the first live test against a real gateway (the maintainer, 2026-10-27).
/// </summary>
public class Checkpoint41LiveFeedbackTests
{
    private static readonly Uri Gw = new("https://hermes.example/");

    private static ConnectionIdentity Id(string url = "https://hermes.example/", string gw = "home", string user = "u", string? pw = "pw",
        NativeSession? browser = null, AccessToken? access = null) =>
        ConnectionIdentity.Of(new Uri(url), gw, user, pw, browser, access);

    [Fact]
    public void Closing_settings_without_changing_the_connection_keeps_the_same_identity()
    {
        Assert.Equal(Id(), Id());
        // A browser sign-in whose tokens were refreshed meanwhile is still the same sign-in.
        Assert.Equal(Id(browser: new NativeSession("AT1", "RT1", 1, "p", "user-1", Gw.ToString())),
                     Id(browser: new NativeSession("AT2", "RT2", 2, "p", "user-1", Gw.ToString())));
        Assert.Equal(Id(gw: "home "), Id(gw: "home"));                                   // surrounding spaces are not a change
    }

    [Fact]
    public void Any_change_that_affects_the_connection_gives_a_new_identity()
    {
        var baseline = Id();
        Assert.NotEqual(baseline, Id(url: "https://other.example/"));
        Assert.NotEqual(baseline, Id(gw: "work"));
        Assert.NotEqual(baseline, Id(user: "someone-else"));
        Assert.NotEqual(baseline, Id(pw: "new-password"));
        Assert.NotEqual(baseline, Id(pw: null));
        Assert.NotEqual(baseline, Id(browser: new NativeSession("AT", "RT", 1, "p", "user-1", Gw.ToString())));
        Assert.NotEqual(Id(browser: new NativeSession("AT", "RT", 1, "p", "user-1", Gw.ToString())),
                        Id(browser: new NativeSession("AT", "RT", 1, "p", "user-2", Gw.ToString())));
        Assert.NotEqual(baseline, Id(access: new AccessToken("id.access", "s1")));
        Assert.NotEqual(Id(access: new AccessToken("id.access", "s1")), Id(access: new AccessToken("id.access", "s2")));
    }

    private static ProfileKey P(string id) => new("home", id);

    [Fact]
    public void The_active_profile_is_the_gateway_default_not_whichever_id_sorts_first()
    {
        // Live finding: with "bookahmail", "default" and "zeta" ticked, New chat went to "bookahmail".
        var ticked = new[] { P("zeta"), P("default"), P("bookahmail") };
        Assert.Equal(P("default"), ActiveProfile.Pick(ticked, null));
        Assert.Equal(P("zeta"), ActiveProfile.Pick(ticked, "zeta"));                 // the person's own choice wins
        Assert.Equal(P("default"), ActiveProfile.Pick(ticked, "unticked-one"));      // never a profile that is not ticked
        Assert.Equal(P("alpha"), ActiveProfile.Pick([P("beta"), P("alpha")], null)); // no "default" ticked: stable first
        Assert.Null(ActiveProfile.Pick([], "default"));
    }

    [Fact]
    public void A_profile_is_shown_by_its_gateway_display_name_when_known()
    {
        var names = new Dictionary<string, string> { ["default"] = "Shisha", ["blank"] = "  " };
        Assert.Equal("Shisha", ActiveProfile.Label(P("default"), names));
        Assert.Equal("bookahmail", ActiveProfile.Label(P("bookahmail"), names));
        Assert.Equal("blank", ActiveProfile.Label(P("blank"), names));
        Assert.Equal("default", ActiveProfile.Label(P("default"), null));
    }

    [Fact]
    public async Task A_session_title_event_names_the_chat_and_is_never_reported_as_unknown()
    {
        // Live finding: the reply showed "Unknown gateway event: session.title" (BookahHermes ChatReducer: title = payload.title).
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
        await Task.Delay(50);                                                              // the submit answer is read first
        s.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"session.title","session_id":"s1","payload":{"session_id":"s1","title":"Testing Hikyaku"}}}""");
        s.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"session.title","session_id":"s1","payload":{"title":"  "}}}""");
        s.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"some.future.event","session_id":"s1","payload":{}}}""");
        s.Push("""{"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"s1","payload":{}}}""");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        Assert.Equal("Testing Hikyaku", Assert.Single(events.OfType<SessionTitled>()).Title);   // a blank title is ignored
        Assert.Contains(events, e => e is StreamCompleted);
    }

    [Fact]
    public void The_advanced_status_line_is_short_status_not_developer_notes()
    {
        // Live finding: "[Connected] Dashboard credentials stored (DPAPI). Protocol: Hermes JSON-RPC /api/ws (pinned …)".
        Assert.Equal("", PlainStatus.ForAdvanced(ConnectionKind.Connected));
        Assert.Equal("Connection lost, reconnecting… (socket closed)", PlainStatus.ForAdvanced(ConnectionKind.Reconnecting, "socket closed"));
        Assert.Equal("Can't connect (4403 guard)", PlainStatus.ForAdvanced(ConnectionKind.Failed, " 4403 guard "));
        foreach (var kind in Enum.GetValues<ConnectionKind>())
        {
            var text = PlainStatus.ForAdvanced(kind);
            foreach (var jargon in new[] { "DPAPI", "JSON-RPC", "/api/ws", "pinned", "herald-push", "loop", "attempt" })
                Assert.DoesNotContain(jargon, text, StringComparison.OrdinalIgnoreCase);
            Assert.True(text.Length <= 80, kind + ": " + text);
        }
    }

    [Theory]
    [InlineData("/model", "model", "")]
    [InlineData("  /MODEL  gpt-x  ", "model", "gpt-x")]
    [InlineData("/effort high", "reasoning", "high")]                          // alias
    [InlineData("/fork", "branch", "")]
    [InlineData("/yolo on", "yolo", "on")]
    [InlineData("/btw what time is it?", "btw", "what time is it?")]
    public void Known_slash_commands_are_recognised(string input, string name, string argument)
    {
        Assert.True(SlashCommands.TryParse(input, out var n, out var a));
        Assert.Equal(name, n);
        Assert.Equal(argument, a);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/")]
    [InlineData("/unknowncommand")]
    [InlineData("path /model is not a command")]
    [InlineData("")]
    public void Plain_text_and_unknown_commands_are_not_taken_as_commands(string input) =>
        Assert.False(SlashCommands.TryParse(input, out _, out _));

    [Fact]
    public void Suggestions_follow_the_typed_prefix_and_simple_view_sees_only_everyday_commands()
    {
        Assert.Equal(["model"], SlashCommands.Suggest("/mo", advanced: true).Select(c => c.Name));
        Assert.Empty(SlashCommands.Suggest("/mo", advanced: false));                // model choice is Advanced-only
        Assert.Contains(SlashCommands.Suggest("/", advanced: false), c => c.Name == "btw");
        Assert.DoesNotContain(SlashCommands.Suggest("/", advanced: false), c => c.AdvancedOnly);
        Assert.Empty(SlashCommands.Suggest("/model gpt", advanced: true));          // an argument has started
        Assert.Empty(SlashCommands.Suggest("hello", advanced: true));
    }

    [Fact]
    public void On_and_off_are_read_strictly()
    {
        Assert.True(SlashCommands.OnOff(" ON "));
        Assert.False(SlashCommands.OnOff("off"));
        Assert.Null(SlashCommands.OnOff(""));
        Assert.Null(SlashCommands.OnOff("maybe"));
    }

    /// <summary>
    /// Answers <c>/herald-push cron …</c> the way the plugin does (hermes-herald-push <c>commands.py</c> / <c>cron.py</c> /
    /// <c>store.py</c>): every request needs a string <c>device_id</c>, and following a job needs one of hex digits and dashes.
    /// </summary>
    private static string? HeraldPushLike(string method, System.Text.Json.JsonDocument doc)
    {
        if (method != "command.dispatch") return null;
        var arg = doc.RootElement.GetProperty("params").GetProperty("arg").GetString()!;     // "cron <op> <json>"
        var rest = arg["cron ".Length..];
        var op = rest[..rest.IndexOf(' ')];
        using var body = System.Text.Json.JsonDocument.Parse(rest[(op.Length + 1)..]);
        string output;
        if (!body.RootElement.TryGetProperty("device_id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
            output = """{"ok":false,"error":"bad device_id"}""";
        else if (op == "follow" && !System.Text.RegularExpressions.Regex.IsMatch(id.GetString()!, "^[0-9a-fA-F-]{8,64}$"))
            output = """{"ok":false,"error":"bad device_id"}""";
        else output = op switch
        {
            "inbox" => """{"ok":true,"runs":[]}""",
            "read" => """{"ok":false,"error":"no such run"}""",
            _ => """{"ok":true,"jobs":{},"failures":false,"default":"off"}"""
        };
        return System.Text.Json.JsonSerializer.Serialize(new { type = "plugin", output });
    }

    [Fact]
    public async Task Every_inbox_request_names_this_device_as_the_herald_push_plugin_requires()
    {
        // Live finding: the Inbox said "Error — bad device_id". Listing runs and reading one never sent device_id.
        var (t, _, stop) = await Checkpoint16RewindTests.Start(HeraldPushLike);
        await using var _t = t;
        var api = new InboxApi(() => t.Client!, InboxDevice.NewId());
        Assert.Empty(await api.RunsAsync());                                              // threw "bad device_id" before the fix
        Assert.Null(await api.ReadAsync(new InboxRun("default", "job", "run")));
        await api.FollowingAsync();
        stop.Cancel();
    }

    [Fact]
    public void The_device_id_is_one_the_plugin_accepts_and_an_old_one_is_replaced()
    {
        var id = InboxDevice.NewId();
        Assert.Matches("^[0-9a-fA-F-]{8,64}$", id);
        Assert.NotEqual(id, InboxDevice.NewId());
        Assert.True(InboxDevice.IsValid(id));
        Assert.False(InboxDevice.IsValid("desktop-0123456789abcdef0123456789abcdef"));      // the old format: letters outside hex
        Assert.False(InboxDevice.IsValid(""));
        Assert.Equal(id, InboxDevice.Ensure(id));
        Assert.True(InboxDevice.IsValid(InboxDevice.Ensure("desktop-0123456789abcdef0123456789abcdef")));
    }

    [Fact]
    public void The_identity_never_carries_a_secret_in_readable_form()
    {
        var id = Id(pw: "hunter2-password", access: new AccessToken("id.access", "cf-secret-value"));
        var text = id.ToString();
        Assert.DoesNotContain("hunter2-password", text);
        Assert.DoesNotContain("cf-secret-value", text);
        Assert.Equal(64, id.SignInDigest.Length);
    }
}
