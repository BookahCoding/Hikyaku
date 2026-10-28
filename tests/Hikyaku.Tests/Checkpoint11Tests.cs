using System.Collections.Concurrent;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 11: explicit local terminal opt-in + ConPTY — synthetic temps only.</summary>
public class Checkpoint11OptInPolicyTests
{
    [Fact]
    public async Task Terminal_defaults_off_and_does_not_launch()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.False(await store.IsEnabledAsync(key, TerminalHostKind.LocalWindows));
        Assert.False(TerminalLaunchPolicy.MayAutoStart(await store.GetAsync(key, TerminalHostKind.LocalWindows)));
        Assert.False(TerminalLaunchPolicy.MayAutoStartFromSavedPreference);
    }

    [Fact]
    public async Task Opt_in_requires_warning_ack_and_exact_scope()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw-a", "prof");
        var denied = await store.EnableAsync(key, TerminalHostKind.LocalWindows, warningAcknowledged: false);
        Assert.False(denied.Ok);
        Assert.Contains("warning", denied.Error!, StringComparison.OrdinalIgnoreCase);

        var remote = await store.EnableAsync(key, TerminalHostKind.Remote, warningAcknowledged: true);
        Assert.False(remote.Ok);
        Assert.Contains(TerminalOptInStore.RemoteUnavailableCopy, remote.Error!, StringComparison.Ordinal);

        var ok = await store.EnableAsync(key, TerminalHostKind.LocalWindows, warningAcknowledged: true);
        Assert.True(ok.Ok, ok.Error);
        Assert.True(await store.IsEnabledAsync(key, TerminalHostKind.LocalWindows));
        Assert.False(await store.IsEnabledAsync(new ProfileKey("gw-b", "prof"), TerminalHostKind.LocalWindows));
        Assert.False(await store.IsEnabledAsync(new ProfileKey("gw-a", "other"), TerminalHostKind.LocalWindows));
        Assert.Contains("Windows user", TerminalOptInStore.WarningCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not", TerminalOptInStore.WarningCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("folder", TerminalOptInStore.WarningCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT", TerminalOptInStore.NoGatewayBridgeCopy, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_grant_yolo_and_advanced_do_not_authorise_terminal()
    {
        Assert.False(TerminalAuthorisation.FolderGrantAuthorisesShell);
        Assert.False(TerminalAuthorisation.YoloAuthorisesShell);
        Assert.False(TerminalAuthorisation.AdvancedVisibilityAuthorisesShell);
        Assert.False(TerminalAuthorisation.OptInCreatesGatewayShellBridge);
        Assert.False(TerminalAuthorisation.WorkingDirectoryIsSandbox);
    }

    [Fact]
    public async Task Revoke_clears_opt_in()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, warningAcknowledged: true)).Ok);
        await store.RevokeAsync(key, TerminalHostKind.LocalWindows);
        Assert.False(await store.IsEnabledAsync(key, TerminalHostKind.LocalWindows));
    }
}

public class Checkpoint11SessionControllerTests
{
    [Fact]
    public async Task Controller_refuses_start_without_opt_in_and_never_auto_starts()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        using var ctl = new LocalTerminalController(store, new FakePtyFactory());
        var start = await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24);
        Assert.False(start.Ok);
        Assert.Null(ctl.ActiveSession);
        Assert.False(ctl.DidLaunchOnConstruction);
    }

    [Fact]
    public async Task Hidden_drawer_preserves_session_close_kills()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        Assert.NotNull(ctl.ActiveSession);
        ctl.SetDrawerVisible(false);
        Assert.NotNull(ctl.ActiveSession);
        Assert.False(factory.Last!.Stopped);
        ctl.CloseTerminal();
        Assert.Null(ctl.ActiveSession);
        Assert.True(factory.Last.Stopped);
    }

    [Fact]
    public async Task Revoke_and_profile_switch_stop_and_clear_without_leak()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var a = new ProfileKey("gw", "a");
        var b = new ProfileKey("gw", "b");
        Assert.True((await store.EnableAsync(a, TerminalHostKind.LocalWindows, true)).Ok);
        Assert.True((await store.EnableAsync(b, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(a, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        factory.Last!.Emit("secret-scrollback\n");
        Assert.Contains("secret-scrollback", ctl.ScrollbackText);

        await store.RevokeAsync(a, TerminalHostKind.LocalWindows);
        await ctl.OnOptInChangedAsync(a);
        Assert.Null(ctl.ActiveSession);
        Assert.Equal("", ctl.ScrollbackText);
        Assert.True(factory.Last.Stopped);

        Assert.True((await store.EnableAsync(a, TerminalHostKind.LocalWindows, true)).Ok);
        Assert.True((await ctl.StartAsync(a, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        factory.Last!.Emit("from-a\n");
        var gen = ctl.OutputGeneration;
        ctl.BindProfile(b);
        Assert.Equal("", ctl.ScrollbackText);
        Assert.Null(ctl.ActiveSession);
        // Late output from old ownership must be ignored
        factory.Sessions[0].Emit("late-leak\n");
        Assert.DoesNotContain("late-leak", ctl.ScrollbackText);
        Assert.NotEqual(gen, ctl.OutputGeneration);
    }

    [Fact]
    public async Task Gateway_switch_clears_and_rejects_stale_callbacks()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var a = new ProfileKey("gw1", "p");
        Assert.True((await store.EnableAsync(a, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(a, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        factory.Last!.Emit("gw1-out\n");
        ctl.BindProfile(new ProfileKey("gw2", "p"));
        Assert.Equal("", ctl.ScrollbackText);
        factory.Sessions[0].Emit("stale\n");
        Assert.DoesNotContain("stale", ctl.ScrollbackText);
    }

    [Fact]
    public async Task Paste_does_not_submit_enter_and_output_is_bounded()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory, maxScrollbackChars: 64);
        Assert.True((await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        ctl.Paste("echo hello");
        Assert.Equal("echo hello", factory.Last!.Written);
        Assert.DoesNotContain("\r", factory.Last.Written);
        Assert.DoesNotContain("\n", factory.Last.Written);
        ctl.SubmitLine();
        Assert.EndsWith("\r", factory.Last.Written.Replace("\n", "\r"));

        factory.Last.Emit(new string('x', 200));
        Assert.True(ctl.ScrollbackText.Length <= 64);
    }

    [Fact]
    public async Task Resize_and_cancel_stop_are_forwarded()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);
        ctl.Resize(120, 40);
        Assert.Equal((120, 40), factory.Last!.Size);
        ctl.CloseTerminal();
        Assert.True(factory.Last.Stopped);
    }

    [Fact]
    public async Task Startup_failure_is_surfaced_without_orphan_session()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory { FailNext = true };
        using var ctl = new LocalTerminalController(store, factory);
        var r = await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24);
        Assert.False(r.Ok);
        Assert.Null(ctl.ActiveSession);
        Assert.Contains("fail", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One-shot ConPTY can exit before the final output chunk is delivered.
    /// Scrollback must keep draining for that ownership until Close/rebind.
    /// </summary>
    [Fact]
    public async Task Late_output_after_exit_still_drains_until_close()
    {
        var store = new TerminalOptInStore(new MemoryPreferenceStore());
        var key = new ProfileKey("gw", "p1");
        Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);
        var factory = new FakePtyFactory();
        using var ctl = new LocalTerminalController(store, factory);
        Assert.True((await ctl.StartAsync(key, TerminalHostKind.LocalWindows, FakeShell.Path, Path.GetTempPath(), 80, 24)).Ok);

        // Synthetic VT mode noise seen in real ConPTY failures, then exit, then marker.
        factory.Last!.Emit("\u001b[?9001h\u001b[?1004h");
        factory.Last.EmitExit(0);
        Assert.Null(ctl.ActiveSession);
        factory.Last.Emit("cp11-ok-drain\r\n");
        Assert.Contains("cp11-ok-drain", ctl.ScrollbackText, StringComparison.Ordinal);

        ctl.CloseTerminal();
        factory.Last.Emit("after-close-must-drop\r\n");
        Assert.DoesNotContain("after-close-must-drop", ctl.ScrollbackText, StringComparison.Ordinal);
    }
}

[Collection("TerminalShareGate")]
public class Checkpoint11ShareTests
{
    [Fact]
    public void Add_to_chat_requires_selection_preview_and_blocks_secrets()
    {
        TerminalShareGate.UtcNow = null;
        TerminalShareGate.DiscardAll();
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "sess-1");
        var empty = TerminalShareGate.Preview("", dest);
        Assert.False(empty.Ok);
        Assert.Contains("select", empty.Error!, StringComparison.OrdinalIgnoreCase);

        var keyBlock = "-----BEGIN " + "RSA PRIVATE KEY-----\nMII\n-----END RSA PRIVATE KEY-----";
        var blocked = TerminalShareGate.Preview(keyBlock, dest);
        Assert.False(blocked.Ok);
        Assert.True(blocked.HardBlocked);
        Assert.DoesNotContain("MII", blocked.RedactedPreview ?? "", StringComparison.Ordinal);

        var ack = TerminalShareGate.Confirm(blocked.PreviewToken!, dest, acknowledgeRisk: true);
        Assert.False(ack.Ok);
        Assert.True(ack.HardBlocked);

        var okPrev = TerminalShareGate.Preview("hello from terminal", dest);
        Assert.True(okPrev.Ok, okPrev.Error);
        Assert.Contains("hello", okPrev.RedactedPreview!, StringComparison.Ordinal);
        Assert.Contains("gw", okPrev.DestinationLabel, StringComparison.Ordinal);

        var staleDest = new TerminalShareDestination(new ProfileKey("gw", "other"), "sess-1");
        var stale = TerminalShareGate.Confirm(okPrev.PreviewToken!, staleDest, acknowledgeRisk: false);
        Assert.False(stale.Ok);
        Assert.Contains("destination", stale.Error!, StringComparison.OrdinalIgnoreCase);

        var confirmed = TerminalShareGate.Confirm(okPrev.PreviewToken!, dest, acknowledgeRisk: false);
        Assert.True(confirmed.Ok, confirmed.Error);
        Assert.Equal("hello from terminal", confirmed.Payload);
    }

    [Fact]
    public void Credential_leaf_shaped_selection_stays_hard_blocked()
    {
        var dest = new TerminalShareDestination(new ProfileKey("gw", "p1"), "s");
        // Content that looks like reading a .env — still hard-blocked by heuristic
        var body = "API_KEY=\"sk-" + new string('a', 24) + "\"";
        var p = TerminalShareGate.Preview(body, dest);
        Assert.False(p.Ok);
        Assert.True(p.HardBlocked);
    }
}

public class Checkpoint11RealPtyTests
{
    [Fact]
    public async Task Real_conpty_runs_harmless_command_in_disposable_dir()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows-only surface

        var root = Path.Combine(Path.GetTempPath(), "hikyaku-cp11-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TerminalOptInStore(new MemoryPreferenceStore());
            var key = new ProfileKey("demo", "default");
            Assert.True((await store.EnableAsync(key, TerminalHostKind.LocalWindows, true)).Ok);

            var shell = TrustedShellDiscovery.ResolvePreferred(TrustedShellDiscovery.EnumerateInstalled().Select(s => s.Path));
            Assert.False(string.IsNullOrWhiteSpace(shell), "expected at least cmd.exe");

            var marker = "cp11-ok-" + Guid.NewGuid().ToString("N")[..8];
            // One-shot cmd via ConPTY — real PTY, no network/admin; disposable cwd only.
            var cmd = TrustedShellDiscovery.EnumerateInstalled().First(s => s.Id == "cmd").Path;
            using var ctl = new LocalTerminalController(store, new ConPtySessionFactory());
            var start = await ctl.StartAsync(
                key, TerminalHostKind.LocalWindows, cmd, root, 100, 30,
                arguments: "/d /c echo " + marker);
            Assert.True(start.Ok, start.Error);
            Assert.NotNull(ctl.ActiveSession);
            Assert.Equal(Path.GetFullPath(root), ctl.WorkingDirectory);
            Assert.Contains("Local", ctl.HostLabel, StringComparison.OrdinalIgnoreCase);

            var saw = await WaitForAsync(() => ctl.ScrollbackText.Contains(marker, StringComparison.Ordinal), TimeSpan.FromSeconds(8));
            Assert.True(saw, "PTY scrollback missing marker. Got: " + Trunc(ctl.ScrollbackText));

            ctl.CloseTerminal();
            Assert.Null(ctl.ActiveSession);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Trusted_shells_are_installed_paths_only()
    {
        var shells = TrustedShellDiscovery.EnumerateInstalled().ToList();
        Assert.Contains(shells, s => s.Id == "cmd");
        foreach (var s in shells)
        {
            Assert.True(File.Exists(s.Path), s.Path);
            Assert.False(s.Path.Contains("..", StringComparison.Ordinal));
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

public class Checkpoint11DocsTests
{
    [Fact]
    public void Handoff_and_parity_mention_terminal_as_implemented_boundary()
    {
        var root = FindRepoRoot();
        var handoff = File.ReadAllText(Path.Combine(root, "docs", "HANDOFF.md"));
        Assert.Contains("ConPTY", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Push", handoff, StringComparison.OrdinalIgnoreCase);
        var desktop = File.ReadAllText(Path.Combine(root, "docs", "DESKTOP-PARITY.md"));
        Assert.Contains("implemented + fixture-tested", desktop, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("| **missing** | needs-new-boundary; **not** folder-gated |", desktop, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Hikyaku.sln")) || File.Exists(Path.Combine(dir, "Directory.Build.props")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}

file static class FakeShell
{
    public static string Path => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";
}

file sealed class MemoryPreferenceStore : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default)
    {
        _content = content;
        return Task.CompletedTask;
    }
}

file sealed class FakePtyFactory : IPtySessionFactory
{
    public bool FailNext { get; set; }
    public FakePtySession? Last { get; private set; }
    public List<FakePtySession> Sessions { get; } = new();

    public Task<IPtySession> StartAsync(PtyStartRequest request, CancellationToken ct = default)
    {
        if (FailNext)
            throw new InvalidOperationException("synthetic start failure");
        var s = new FakePtySession(request);
        Last = s;
        Sessions.Add(s);
        return Task.FromResult<IPtySession>(s);
    }
}

file sealed class FakePtySession : IPtySession
{
    private readonly ConcurrentQueue<string> _pending = new();
    public FakePtySession(PtyStartRequest request)
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

    public void Write(string data)
    {
        if (Stopped) return;
        Written += data;
    }

    public void Resize(int columns, int rows) => Size = (columns, rows);

    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        Exited?.Invoke(0);
    }

    public void Emit(string data) => OutputReceived?.Invoke(data);

    /// <summary>Simulate child exit without tearing down the pipe (drain race).</summary>
    public void EmitExit(int? code) => Exited?.Invoke(code);

    public void Begin() { /* fake: Emit is explicit */ }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
