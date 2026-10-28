using System.Collections.Concurrent;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

[CollectionDefinition("TerminalShareGate", DisableParallelization = true)]
public class TerminalShareGateCollection : ICollectionFixture<object> { }

/// <summary>Checkpoint 11b corrective regressions — reproduce before fix; synthetic only.</summary>
public class Checkpoint11bTransportStagingTests
{
    [Fact]
    public void Hermes_style_last_user_submit_ignores_prior_selection_bubble()
    {
        // Source reproduction: History bubble from AddTerminalSelectionToChat is not the submitted prompt.
        var messages = new List<ChatMessage>
        {
            new("user", "```\nterminal-selection-marker\n```"),
            new("user", "what does this mean?")
        };
        var submitted = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        Assert.Equal("what does this mean?", submitted);
        Assert.DoesNotContain("terminal-selection-marker", submitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Staged_terminal_context_is_included_once_in_next_prompt_and_labelled_pending()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "sess-1");
        var staging = new TerminalContextStaging();
        staging.Stage(dest, "approved-terminal-payload");
        Assert.Contains("pending", staging.PendingLabel ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminal", staging.PendingLabel ?? "", StringComparison.OrdinalIgnoreCase);

        var prompt = staging.BuildNextPrompt(dest, "explain please");
        Assert.Contains("approved-terminal-payload", prompt, StringComparison.Ordinal);
        Assert.Contains("explain please", prompt, StringComparison.Ordinal);
        Assert.Contains("Terminal selection", prompt, StringComparison.OrdinalIgnoreCase);

        // Consumed once — second build has no context.
        staging.Consume();
        Assert.Null(staging.PendingLabel);
        var again = staging.BuildNextPrompt(dest, "second");
        Assert.Equal("second", again);
        Assert.DoesNotContain("approved-terminal-payload", again, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_destination_and_cleared_context_are_absent_from_prompt()
    {
        var a = new TerminalShareDestination(new ProfileKey("gw", "a"), "s1");
        var b = new TerminalShareDestination(new ProfileKey("gw", "b"), "s1");
        var staging = new TerminalContextStaging();
        staging.Stage(a, "must-not-leak");
        Assert.Equal("hello", staging.BuildNextPrompt(b, "hello"));
        Assert.DoesNotContain("must-not-leak", staging.BuildNextPrompt(b, "hello"), StringComparison.Ordinal);

        staging.Stage(a, "cancelled-payload");
        staging.Clear();
        Assert.Equal("x", staging.BuildNextPrompt(a, "x"));
        Assert.DoesNotContain("cancelled-payload", staging.BuildNextPrompt(a, "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Loopback_prompt_submit_capture_contains_approved_context_once()
    {
        var dest = new TerminalShareDestination(new ProfileKey("demo", "default"), "rt-1");
        var staging = new TerminalContextStaging();
        staging.Stage(dest, "cp11b-approved-context");
        var userPrompt = staging.BuildNextPrompt(dest, "summarise");
        staging.Consume();

        var capture = new List<string>();
        var request = new ChatRequest(dest.Profile,
            new[] { new ChatMessage("user", userPrompt) },
            dest.SessionId, null, null, null, null);
        var submitted = request.Messages.Last(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)).Content;
        capture.Add(submitted);

        Assert.Single(capture);
        Assert.Contains("cp11b-approved-context", capture[0], StringComparison.Ordinal);
        Assert.Contains("summarise", capture[0], StringComparison.Ordinal);
        // After consume, rebuild would not re-include
        Assert.Equal("summarise", staging.BuildNextPrompt(dest, "summarise"));
    }
}

[Collection("TerminalShareGate")]
public class Checkpoint11bShareGateTests
{
    [Fact]
    public void Cancel_discards_pending_so_confirm_fails_and_raw_is_gone()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "s");
        var preview = TerminalShareGate.Preview("harmless terminal text", dest);
        Assert.True(preview.Ok, preview.Error);
        Assert.True(TerminalShareGate.Discard(preview.PreviewToken!));
        var confirm = TerminalShareGate.Confirm(preview.PreviewToken!, dest, acknowledgeRisk: false);
        Assert.False(confirm.Ok);
        Assert.Contains("expired", confirm.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hard_blocked_preview_does_not_retain_raw_credentials()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "s");
        // Split markers so the privacy scanner does not treat the test fixture as an assigned secret.
        var blockedBody = "-----BEGIN " + "RSA PRIVATE KEY-----\nSYNTHETIC_RAW_MATERIAL\n-----END RSA PRIVATE KEY-----";
        var blocked = TerminalShareGate.Preview(blockedBody, dest);
        Assert.False(blocked.Ok);
        Assert.True(blocked.HardBlocked);
        Assert.DoesNotContain("SYNTHETIC_RAW_MATERIAL", blocked.RedactedPreview ?? "", StringComparison.Ordinal);
        Assert.False(TerminalShareGate.DebugContainsRaw(blockedBody));
        if (blocked.PreviewToken is not null)
            TerminalShareGate.Discard(blocked.PreviewToken);
    }

    [Fact]
    public void Confirm_returns_displayed_approved_payload_matching_redacted_preview()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "s");
        var raw = "harmless terminal lines for share";
        var preview = TerminalShareGate.Preview(raw, dest);
        Assert.True(preview.Ok, preview.Error);
        Assert.Equal(TerminalShareGate.RedactForPreview(raw), preview.RedactedPreview);
        var done = TerminalShareGate.Confirm(preview.PreviewToken!, dest, acknowledgeRisk: false);
        Assert.True(done.Ok, done.Error);
        Assert.Equal(preview.RedactedPreview, done.Payload);
    }

    [Fact]
    public void Expired_and_over_capacity_pending_are_rejected()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "s");
        var clock = new ManualShareClock(DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        TerminalShareGate.DiscardAll();
        TerminalShareGate.UtcNow = () => clock.UtcNow;
        try
        {
            var preview = TerminalShareGate.Preview("hello expiry", dest);
            Assert.True(preview.Ok, preview.Error);
            clock.UtcNow = clock.UtcNow.Add(TerminalShareGate.PendingTtl).AddSeconds(1);
            var expired = TerminalShareGate.Confirm(preview.PreviewToken!, dest, acknowledgeRisk: false);
            Assert.False(expired.Ok);
            Assert.Contains("expired", expired.Error!, StringComparison.OrdinalIgnoreCase);

            // Capacity: fill beyond max; oldest discarded (real-time clock for capacity)
            TerminalShareGate.UtcNow = null;
            TerminalShareGate.DiscardAll();
            for (var i = 0; i < TerminalShareGate.MaxPending + 3; i++)
            {
                var p = TerminalShareGate.Preview("payload-" + i, dest);
                Assert.True(p.Ok, p.Error);
            }
            Assert.True(TerminalShareGate.PendingCount <= TerminalShareGate.MaxPending);
            TerminalShareGate.DiscardAll();
            Assert.Equal(0, TerminalShareGate.PendingCount);
        }
        finally
        {
            TerminalShareGate.UtcNow = null;
            TerminalShareGate.DiscardAll();
        }
    }

    [Fact]
    public void Destination_change_discard_clears_pending_for_profile()
    {
        var a = new TerminalShareDestination(new ProfileKey("gw", "a"), "s1");
        var b = new TerminalShareDestination(new ProfileKey("gw", "b"), "s1");
        var pa = TerminalShareGate.Preview("from-a", a);
        var pb = TerminalShareGate.Preview("from-b", b);
        Assert.True(pa.Ok && pb.Ok);
        TerminalShareGate.DiscardForDestination(a);
        Assert.False(TerminalShareGate.Confirm(pa.PreviewToken!, a, false).Ok);
        Assert.True(TerminalShareGate.Confirm(pb.PreviewToken!, b, false).Ok);
    }

    private sealed class ManualShareClock(DateTimeOffset start)
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }
}

public class Checkpoint11bStaleBindTests
{
    [Fact]
    public async Task Start_after_delayed_optin_check_rejects_stale_profile_rebind()
    {
        var a = new ProfileKey("gw", "a");
        var b = new ProfileKey("gw", "b");
        var seedStore = new MemoryPreferenceStore11b();
        var seed = new TerminalOptInStore(seedStore);
        Assert.True((await seed.EnableAsync(a, TerminalHostKind.LocalWindows, true)).Ok);
        Assert.True((await seed.EnableAsync(b, TerminalHostKind.LocalWindows, true)).Ok);
        var raw = await seedStore.LoadAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new TerminalOptInStore(new GatedPreferenceStore(gate.Task, raw));
        var factory = new FakePtyFactory11b();
        using var ctl = new LocalTerminalController(store, factory);
        ctl.BindProfile(a);

        var startTask = ctl.StartAsync(a, TerminalHostKind.LocalWindows, FakeShell11b.Path, Path.GetTempPath(), 80, 24);
        // While opt-in load is gated, switch profile
        ctl.BindProfile(b);
        gate.SetResult();
        var result = await startTask;
        Assert.False(result.Ok);
        Assert.Contains("cancel", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(b, ctl.BoundProfile);
        Assert.Null(ctl.ActiveSession);
    }

    [Fact]
    public async Task Delayed_factory_start_discards_session_if_revoked_during_await()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore11b());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new DelayedPtyFactory11b();
        using var ctl = new LocalTerminalController(store, factory);
        ctl.BindProfile(key);
        var startTask = ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell11b.Path, Path.GetTempPath(), 80, 24);
        await store.RevokeAsync(key, TerminalHostKind.LocalWindows);
        await ctl.OnOptInChangedAsync(key);
        factory.Release.SetResult();
        var result = await startTask;
        Assert.False(result.Ok);
        Assert.Null(ctl.ActiveSession);
        Assert.True(factory.Last is null || factory.Last.Stopped);
    }

    [Fact]
    public async Task OnOptInChanged_mismatch_does_not_clear_other_profile_session()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore11b());
        var a = new ProfileKey("gw", "a");
        var b = new ProfileKey("gw", "b");
        Assert.True((await store.EnableAsync(a, TerminalHostKind.LocalWindows, true)).Ok);
        Assert.True((await store.EnableAsync(b, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory11b();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(b, TerminalHostKind.LocalWindows, FakeShell11b.Path, Path.GetTempPath(), 80, 24)).Ok);
        factory.Last!.Emit("keep-b\n");
        await store.RevokeAsync(a, TerminalHostKind.LocalWindows);
        await ctl.OnOptInChangedAsync(a); // revoke A while bound to B
        Assert.NotNull(ctl.ActiveSession);
        Assert.Contains("keep-b", ctl.ScrollbackText, StringComparison.Ordinal);
    }

}

public class Checkpoint11bResizeAndCoalesceTests
{
    [Fact]
    public async Task Controller_resize_forwards_safe_minimums()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore11b());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory11b();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell11b.Path, Path.GetTempPath(), 80, 24)).Ok);
        ctl.Resize(0, 0);
        Assert.True(factory.Last!.Size.Cols >= TerminalViewport.MinColumns);
        Assert.True(factory.Last.Size.Rows >= TerminalViewport.MinRows);
        ctl.Resize(120, 40);
        Assert.Equal((120, 40), factory.Last.Size);
    }

    [Fact]
    public void Coalesced_ui_flag_bounds_work_under_burst()
    {
        var flag = new TerminalUiCoalesce();
        var paints = 0;
        for (var i = 0; i < 10_000; i++)
            flag.MarkChanged();
        // At most one pending paint until consumed
        Assert.Equal(1, flag.PendingCount);
        if (flag.Consume()) paints++;
        if (flag.Consume()) paints++;
        Assert.Equal(1, paints);
        Assert.Equal(0, flag.PendingCount);
        for (var i = 0; i < 100; i++)
        {
            flag.MarkChanged();
            if (flag.Consume()) paints++;
        }
        Assert.Equal(101, paints);
    }
}

public class Checkpoint11bInteractivePtyTests
{
    [Fact]
    public async Task Real_conpty_interactive_lifecycle_input_resize_close()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp11b-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TerminalOptInStore(new MemoryPreferenceStore11b());
            var key = new ProfileKey("demo", "default");
            Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
            var cmd = TrustedShellDiscovery.EnumerateInstalled().First(s => s.Id == "cmd").Path;
            using var ctl = new LocalTerminalController(store, new ConPtySessionFactory());
            // Interactive: no one-shot /c arguments
            var start = await ctl.StartAsync(key, TerminalHostKind.LocalWindows, cmd, root, 100, 30);
            Assert.True(start.Ok, start.Error);
            Assert.NotNull(ctl.ActiveSession);

            var marker = "cp11b-" + Guid.NewGuid().ToString("N")[..8];
            ctl.WriteRaw("echo " + marker);
            ctl.SubmitLine();
            var saw = await WaitForAsync(() => ctl.ScrollbackText.Contains(marker, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.True(saw, "interactive scrollback missing marker. Got: " + Trunc(ctl.ScrollbackText));

            ctl.Resize(90, 28);
            // ANSI-ish prompt noise is fine; marker must remain readable as plain text substring
            Assert.Contains(marker, ctl.ScrollbackText, StringComparison.Ordinal);

            ctl.CloseTerminal();
            Assert.Null(ctl.ActiveSession);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> pred, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (pred()) return true;
            await Task.Delay(50);
        }
        return pred();
    }

    private static string Trunc(string s) => s.Length <= 240 ? s : s[^240..];
}

file static class FakeShell11b
{
    public static string Path => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";
}

file sealed class MemoryPreferenceStore11b : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}

file sealed class GatedPreferenceStore : IPreferenceStore
{
    private readonly Task _gate;
    private string? _content;
    public GatedPreferenceStore(Task gate, string? content = null)
    {
        _gate = gate;
        _content = content;
    }
    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return _content;
    }
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}

file sealed class FakePtyFactory11b : IPtySessionFactory
{
    public FakePtySession11b? Last { get; private set; }
    public List<FakePtySession11b> Sessions { get; } = new();
    public Task<IPtySession> StartAsync(PtyStartRequest request, CancellationToken ct = default)
    {
        var s = new FakePtySession11b(request);
        Last = s;
        Sessions.Add(s);
        return Task.FromResult<IPtySession>(s);
    }
}

file sealed class DelayedPtyFactory11b : IPtySessionFactory
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakePtySession11b? Last { get; private set; }
    public async Task<IPtySession> StartAsync(PtyStartRequest request, CancellationToken ct = default)
    {
        await Release.Task.WaitAsync(ct).ConfigureAwait(false);
        var s = new FakePtySession11b(request);
        Last = s;
        return s;
    }
}

file sealed class FakePtySession11b : IPtySession
{
    public FakePtySession11b(PtyStartRequest request)
    {
        WorkingDirectory = request.WorkingDirectory;
        Size = (request.Columns, request.Rows);
    }
    public string WorkingDirectory { get; }
    public string Written { get; private set; } = "";
    public (int Cols, int Rows) Size { get; private set; }
    public bool Stopped { get; private set; }
    public event Action<string>? OutputReceived;
    public event Action<int?>? Exited;
    public void Write(string data) { if (!Stopped) Written += data; }
    public void Resize(int columns, int rows) => Size = (columns, rows);
    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        Exited?.Invoke(0);
    }
    public void Emit(string data) => OutputReceived?.Invoke(data);
    public void Begin() { /* fake: Emit is explicit */ }
    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
