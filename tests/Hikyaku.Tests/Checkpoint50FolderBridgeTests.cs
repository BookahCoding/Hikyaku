using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;
using Xunit.Abstractions;

namespace Hikyaku.Tests;

/// <summary>Stage 50 — folders the agent may use on this PC, remembered per gateway and profile.</summary>
public class Checkpoint50FolderBridgeTests
{
    private readonly ITestOutputHelper _out;
    public Checkpoint50FolderBridgeTests(ITestOutputHelper output) => _out = output;

    private static ProfileKey Key(string profile) => new("gw", profile);

    private static BridgeProfile Access(string root, bool network = false, bool keysOpen = false, params string[] keyFiles) =>
        new([new BridgeFolder(root, network, DateTimeOffset.UtcNow)],
            keyFiles.Select(p => new BridgeKeyFile(p, DateTimeOffset.UtcNow)).ToList(),
            keysOpen);

    [Fact]
    public async Task A_folder_stays_with_its_profile_and_a_second_folder_stays_too()
    {
        var mem = new BridgeMemory();
        var store = new FolderBridgeStore(mem);
        await store.AddFolderAsync(Key("a"), @"D:\work", network: false);
        await store.AddFolderAsync(Key("a"), @"\\server\share", network: true);

        var again = new FolderBridgeStore(mem);
        var a = await again.GetAsync(Key("a"));
        var b = await again.GetAsync(Key("b"));

        Assert.Equal(2, a.Folders.Count);
        Assert.Contains(a.Folders, f => f.CanonicalRoot == @"D:\work" && !f.Network);
        Assert.Contains(a.Folders, f => f.CanonicalRoot == @"\\server\share" && f.Network);
        Assert.Empty(b.Folders);

        await again.AddFolderAsync(Key("a"), @"D:\work", network: false);
        Assert.Equal(2, (await again.GetAsync(Key("a"))).Folders.Count);

        await again.RemoveFolderAsync(Key("a"), @"D:\work");
        var left = await again.GetAsync(Key("a"));
        Assert.Single(left.Folders);
        Assert.Equal(@"\\server\share", left.Folders[0].CanonicalRoot);
    }

    [Fact]
    public async Task A_key_file_allow_and_the_switch_survive_a_reload_and_can_be_taken_back()
    {
        var mem = new BridgeMemory();
        var store = new FolderBridgeStore(mem);
        await store.AllowKeyFileAsync(Key("a"), @"D:\work\id_rsa");
        await store.SetKeyFilesOpenAsync(Key("a"), true);

        var again = new FolderBridgeStore(mem);
        var open = await again.GetAsync(Key("a"));
        Assert.True(open.KeyFilesOpen);
        Assert.Contains(open.KeyFiles, f => f.CanonicalPath == @"D:\work\id_rsa");

        await again.SetKeyFilesOpenAsync(Key("a"), false);
        await again.RevokeKeyFileAsync(Key("a"), @"D:\work\id_rsa");
        var shut = await again.GetAsync(Key("a"));
        Assert.False(shut.KeyFilesOpen);
        Assert.Empty(shut.KeyFiles);
    }

    [Fact]
    public void A_read_and_a_write_keep_every_byte_and_a_new_parent_is_created()
    {
        using var dir = new TempTree();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var big = new byte[2 * 1024 * 1024];
        new Random(1).NextBytes(big);
        var access = Access(dir.Root);

        var wrotePng = FolderBridgeGate.Apply(access, "write", Path.Combine(dir.Root, "a.png"), png, false);
        var readPng = FolderBridgeGate.Apply(access, "read", Path.Combine(dir.Root, "a.png"), null, false);
        Assert.True(wrotePng.Ok);
        Assert.Equal("base64", readPng.Encoding);
        Assert.Equal(png, readPng.Bytes);

        var nested = Path.Combine(dir.Root, "sub", "big.bin");
        Assert.True(FolderBridgeGate.Apply(access, "write", nested, big, false).Ok);
        Assert.True(Directory.Exists(Path.Combine(dir.Root, "sub")));
        var readBig = FolderBridgeGate.Apply(access, "read", nested, null, false);
        Assert.Equal(big, readBig.Bytes);

        File.WriteAllText(Path.Combine(dir.Root, "notes.txt"), "the word password is just a word");
        Directory.CreateDirectory(Path.Combine(dir.Root, "child"));
        var list = FolderBridgeGate.Apply(access, "list", dir.Root, null, false);
        Assert.Contains(list.Entries!, e => e.Name == "notes.txt" && !e.Dir);
        Assert.Contains(list.Entries!, e => e.Name == "child" && e.Dir);
    }

    [Fact]
    public void A_notes_file_that_mentions_a_password_is_readable_and_a_key_file_is_not()
    {
        using var dir = new TempTree();
        var notes = Path.Combine(dir.Root, "notes.txt");
        var key = Path.Combine(dir.Root, "id_rsa");
        File.WriteAllText(notes, "the word password is just a word");
        File.WriteAllBytes(key, [1, 2, 3]);
        var closed = Access(dir.Root);

        Assert.Equal(BridgeAsk.None, FolderBridgeGate.Decide(closed, "read", notes).Ask);
        Assert.Equal(BridgeAsk.AllowKeyFile, FolderBridgeGate.Decide(closed, "read", key).Ask);
        Assert.Equal(BridgeAsk.AllowKeyFile, FolderBridgeGate.Decide(closed, "write", key).Ask);
        var blocked = FolderBridgeGate.Apply(closed, "read", key, null, false);
        Assert.Equal("blocked", blocked.Error);
        Assert.Null(blocked.Bytes);

        var allowed = Access(dir.Root, keyFiles: [key]);
        Assert.Equal(BridgeAsk.None, FolderBridgeGate.Decide(allowed, "read", key).Ask);
        Assert.Equal(BridgeAsk.None, FolderBridgeGate.Decide(Access(dir.Root, keysOpen: true), "write", key).Ask);
        Assert.Equal(new byte[] { 1, 2, 3 }, FolderBridgeGate.Apply(allowed, "read", key, null, false).Bytes);
    }

    [Fact]
    public void A_delete_asks_and_only_a_yes_removes_the_file_or_the_folder()
    {
        using var dir = new TempTree();
        var file = Path.Combine(dir.Root, "one.txt");
        var folder = Path.Combine(dir.Root, "box");
        var nested = Path.Combine(folder, "inside.txt");
        File.WriteAllText(file, "keep");
        Directory.CreateDirectory(folder);
        File.WriteAllText(nested, "nested");
        File.WriteAllBytes(Path.Combine(dir.Root, "id_rsa"), [9]);
        var access = Access(dir.Root);

        var fileAsk = FolderBridgeGate.Decide(access, "delete_file", file);
        var folderAsk = FolderBridgeGate.Decide(access, "delete_folder", folder);
        Assert.Equal(BridgeAsk.ConfirmDelete, fileAsk.Ask);
        Assert.Equal(BridgeAsk.ConfirmDelete, folderAsk.Ask);
        Assert.Contains("everything inside", folderAsk.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BridgeAsk.ConfirmDelete, FolderBridgeGate.Decide(access, "delete_file", Path.Combine(dir.Root, "id_rsa")).Ask);

        Assert.False(FolderBridgeGate.Apply(access, "delete_folder", folder, null, false).Ok);
        Assert.True(File.Exists(nested));
        Assert.True(FolderBridgeGate.Apply(access, "delete_folder", folder, null, true).Ok);
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_link_that_leaves_the_folder_is_refused_and_the_outside_file_stays()
    {
        using var dir = new TempTree();
        var outside = Path.Combine(dir.Parent, "outside");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(dir.Root, "link");
        if (!TryLink(link, outside))
        {
            _out.WriteLine("skipped link test: this process could not create a junction or symlink");
            return;
        }

        var access = Access(dir.Root);
        var read = FolderBridgeGate.Apply(access, "read", Path.Combine(link, "secret.txt"), null, false);
        var write = FolderBridgeGate.Apply(access, "write", Path.Combine(link, "new.txt"), [1], false);
        Assert.Equal("outside", read.Error);
        Assert.Equal("outside", write.Error);
        Assert.Equal("secret", File.ReadAllText(secret));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    [Fact]
    public void A_path_outside_asks_for_a_folder_or_a_network_path_and_a_missing_root_fails()
    {
        using var dir = new TempTree();
        var access = Access(dir.Root);
        var elsewhere = Path.Combine(dir.Parent, "elsewhere.txt");
        Assert.Equal(BridgeAsk.ChooseFolder, FolderBridgeGate.Decide(access, "read", elsewhere).Ask);
        Assert.Equal(BridgeAsk.AllowNetwork, FolderBridgeGate.Decide(access, "read", @"\\server\share\a.txt").Ask);

        var gone = Access(Path.Combine(dir.Parent, "does-not-exist"));
        Assert.Equal("missing", FolderBridgeGate.Apply(gone, "read", Path.Combine(dir.Parent, "does-not-exist", "a.txt"), null, false).Error);

        var ads = Path.Combine(dir.Root, "file.txt:stream");
        Assert.Equal("invalid", FolderBridgeGate.Apply(access, "write", ads, [1], false).Error);
        Assert.False(File.Exists(Path.Combine(dir.Root, "file.txt")));
    }

    [Fact]
    public async Task Corrupt_json_loads_as_an_empty_profile()
    {
        var mem = new BridgeMemory();
        await mem.SaveAsync("{");
        var store = new FolderBridgeStore(mem);
        var profile = await store.GetAsync(Key("a"));
        Assert.Empty(profile.Folders);
        Assert.Empty(profile.KeyFiles);
        Assert.False(profile.KeyFilesOpen);
    }

    [Fact]
    public async Task No_client_does_not_dispatch_and_an_idle_poll_asks_nothing()
    {
        var prompts = new FakePrompts();
        var loop = new FolderBridgeLoop(() => null, new FolderBridgeStore(new BridgeMemory()), () => Key("a"), prompts);
        await loop.TickAsync(CancellationToken.None);
        Assert.Empty(prompts.Calls);

        var peer = new ScriptPeer(_ => new JsonObject { ["ok"] = true, ["pending"] = false });
        loop = new FolderBridgeLoop(() => peer, new FolderBridgeStore(new BridgeMemory()), () => Key("a"), prompts);
        await loop.TickAsync(CancellationToken.None);
        Assert.Empty(prompts.Calls);
    }

    [Fact]
    public async Task Choosing_the_folder_that_holds_the_path_reads_it_and_keeps_the_older_folder()
    {
        using var dir = new TempTree();
        var older = Path.Combine(dir.Parent, "older");
        Directory.CreateDirectory(older);
        var file = Path.Combine(dir.Root, "note.txt");
        File.WriteAllText(file, "hello");
        var store = new FolderBridgeStore(new BridgeMemory());
        await store.AddFolderAsync(Key("a"), older, false);
        var prompts = new FakePrompts { Folder = dir.Root };
        var peer = ScriptPeer.One("read", file, "a");
        var loop = new FolderBridgeLoop(() => peer, store, () => Key("a"), prompts);
        await loop.TickAsync(CancellationToken.None);

        Assert.Equal(2, (await store.GetAsync(Key("a"))).Folders.Count);
        Assert.Contains("hello", peer.Answers.Single());
    }

    [Fact]
    public async Task Cancelling_the_folder_ask_writes_nothing()
    {
        using var dir = new TempTree();
        var file = Path.Combine(dir.Root, "note.txt");
        var store = new FolderBridgeStore(new BridgeMemory());
        var peer = ScriptPeer.One("write", file, "a", text: "nope");
        var loop = new FolderBridgeLoop(() => peer, store, () => Key("a"), new FakePrompts { Folder = null });
        await loop.TickAsync(CancellationToken.None);
        Assert.Empty((await store.GetAsync(Key("a"))).Folders);
        Assert.False(File.Exists(file));
        Assert.Contains("cancelled", peer.Answers.Single());
    }

    [Fact]
    public async Task A_chosen_folder_that_still_misses_the_path_is_kept_and_the_file_stays()
    {
        using var dir = new TempTree();
        var other = Path.Combine(dir.Parent, "other");
        Directory.CreateDirectory(other);
        var file = Path.Combine(dir.Root, "note.txt");
        File.WriteAllText(file, "stay");
        var store = new FolderBridgeStore(new BridgeMemory());
        var peer = ScriptPeer.One("read", file, "a");
        var loop = new FolderBridgeLoop(() => peer, store, () => Key("a"), new FakePrompts { Folder = other });
        await loop.TickAsync(CancellationToken.None);
        Assert.Contains((await store.GetAsync(Key("a"))).Folders, f => f.CanonicalRoot == other);
        Assert.Equal("stay", File.ReadAllText(file));
        Assert.Contains("outside", peer.Answers.Single());
    }

    [Fact]
    public async Task A_network_path_is_remembered_only_when_allowed()
    {
        var store = new FolderBridgeStore(new BridgeMemory());
        var denied = ScriptPeer.One("read", @"\\server\share\a.txt", "a");
        await new FolderBridgeLoop(() => denied, store, () => Key("a"), new FakePrompts { Network = false })
            .TickAsync(CancellationToken.None);
        Assert.Empty((await store.GetAsync(Key("a"))).Folders);
        Assert.Contains("cancelled", denied.Answers.Single());

        var allowed = ScriptPeer.One("read", @"\\server\share\a.txt", "a");
        await new FolderBridgeLoop(() => allowed, store, () => Key("a"), new FakePrompts { Network = true })
            .TickAsync(CancellationToken.None);
        Assert.Contains((await store.GetAsync(Key("a"))).Folders, f => f.Network && f.CanonicalRoot == @"\\server\share");
        Assert.Contains("missing", allowed.Answers.Single());
    }

    [Fact]
    public async Task A_key_file_is_read_only_after_it_is_allowed()
    {
        using var dir = new TempTree();
        var key = Path.Combine(dir.Root, "id_rsa");
        File.WriteAllBytes(key, [7, 7]);
        var store = new FolderBridgeStore(new BridgeMemory());
        await store.AddFolderAsync(Key("a"), dir.Root, false);
        var refused = ScriptPeer.One("read", key, "a");
        await new FolderBridgeLoop(() => refused, store, () => Key("a"), new FakePrompts { KeyFile = false })
            .TickAsync(CancellationToken.None);
        Assert.Contains("cancelled", refused.Answers.Single());
        Assert.DoesNotContain("encoding", refused.Answers.Single());

        var allowed = ScriptPeer.One("read", key, "a");
        await new FolderBridgeLoop(() => allowed, store, () => Key("a"), new FakePrompts { KeyFile = true })
            .TickAsync(CancellationToken.None);
        Assert.NotEmpty((await store.GetAsync(Key("a"))).KeyFiles);
        Assert.Contains("encoding", allowed.Answers.Single());
    }

    [Fact]
    public async Task A_folder_delete_runs_only_after_yes()
    {
        using var dir = new TempTree();
        var box = Path.Combine(dir.Root, "box");
        Directory.CreateDirectory(box);
        File.WriteAllText(Path.Combine(box, "inside.txt"), "x");
        var store = new FolderBridgeStore(new BridgeMemory());
        await store.AddFolderAsync(Key("a"), dir.Root, false);

        var no = ScriptPeer.One("delete_folder", box, "a");
        await new FolderBridgeLoop(() => no, store, () => Key("a"), new FakePrompts { Delete = false })
            .TickAsync(CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(box, "inside.txt")));
        Assert.Contains("cancelled", no.Answers.Single());

        var yes = ScriptPeer.One("delete_folder", box, "a");
        await new FolderBridgeLoop(() => yes, store, () => Key("a"), new FakePrompts { Delete = true })
            .TickAsync(CancellationToken.None);
        Assert.False(Directory.Exists(box));
        Assert.Contains("\"ok\":true", yes.Answers.Single().Replace(" ", ""));
    }

    [Fact]
    public async Task A_dropped_lease_does_not_write()
    {
        using var dir = new TempTree();
        var file = Path.Combine(dir.Root, "new.txt");
        var store = new FolderBridgeStore(new BridgeMemory());
        var peer = ScriptPeer.One("write", file, "a", text: "secret", holdOk: false);
        await new FolderBridgeLoop(() => peer, store, () => Key("a"), new FakePrompts { Folder = dir.Root })
            .TickAsync(CancellationToken.None);
        Assert.False(File.Exists(file));
        Assert.Empty(peer.Answers);
    }

    [Fact]
    public async Task A_missing_command_is_remembered_and_another_profile_is_refused()
    {
        var prompts = new FakePrompts();
        var missing = new ScriptPeer(_ => throw new RpcException(InboxApi.NotACommand, "no"));
        var loop = new FolderBridgeLoop(() => missing, new FolderBridgeStore(new BridgeMemory()), () => Key("a"), prompts);
        await loop.TickAsync(CancellationToken.None);
        Assert.True(loop.PluginMissing);

        var peer = ScriptPeer.One("read", @"D:\nope", "other");
        await new FolderBridgeLoop(() => peer, new FolderBridgeStore(new BridgeMemory()), () => Key("a"), prompts)
            .TickAsync(CancellationToken.None);
        Assert.Contains("invalid", peer.Answers.Single());
        Assert.Empty(prompts.Calls);
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch { /* Developer Mode or admin may be required. */ }

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    private sealed class TempTree : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "hikyaku-bridge-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public TempTree()
        {
            Root = Path.Combine(Parent, "root");
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            try { Directory.Delete(Parent, true); } catch { /* best effort */ }
        }
    }

    private sealed class BridgeMemory : IPreferenceStore
    {
        private string? _content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
        public Task SaveAsync(string content, CancellationToken ct = default)
        {
            _content = content;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrompts : IFolderBridgePrompts
    {
        public string? Folder { get; set; }
        public bool Network { get; set; }
        public bool KeyFile { get; set; }
        public bool Delete { get; set; }
        public List<string> Calls { get; } = [];

        public Task<string?> ChooseFolderAsync(string askedPath, CancellationToken ct)
        {
            Calls.Add("folder:" + askedPath);
            return Task.FromResult(Folder);
        }

        public Task<bool> AllowNetworkAsync(string path, CancellationToken ct)
        {
            Calls.Add("network:" + path);
            return Task.FromResult(Network);
        }

        public Task<bool> AllowKeyFileAsync(string path, CancellationToken ct)
        {
            Calls.Add("key:" + path);
            return Task.FromResult(KeyFile);
        }

        public Task<bool> ConfirmDeleteAsync(string path, bool folder, CancellationToken ct)
        {
            Calls.Add("delete:" + path + ":" + folder);
            return Task.FromResult(Delete);
        }
    }

    /// <summary>Serves command.dispatch the way the gateway wraps a plugin command.</summary>
    private sealed class ScriptPeer : IJsonRpcPeer
    {
        private readonly Func<string, JsonObject> _on;
        public List<string> Answers { get; } = [];

        public ScriptPeer(Func<string, JsonObject> on) => _on = on;

        public static ScriptPeer One(string op, string path, string profile, string? text = null, bool holdOk = true)
        {
            ScriptPeer? self = null;
            self = new ScriptPeer(arg =>
            {
                var parts = arg.Split(' ', 3);
                var verb = parts.Length > 0 ? parts[0] : "";
                if (verb == "pending")
                {
                    var body = new JsonObject
                    {
                        ["ok"] = true,
                        ["pending"] = true,
                        ["id"] = "1",
                        ["profile"] = profile,
                        ["op"] = op,
                        ["path"] = path
                    };
                    if (text is not null) body["text"] = text;
                    return body;
                }
                if (verb == "hold")
                    return holdOk
                        ? new JsonObject { ["ok"] = true }
                        : new JsonObject { ["ok"] = false, ["error"] = "not_open" };
                if (verb == "answer" && parts.Length == 3)
                {
                    self!.Answers.Add(parts[2]);
                    return new JsonObject { ["ok"] = true };
                }
                return new JsonObject { ["ok"] = false, ["error"] = "invalid" };
            });
            return self;
        }

        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var arg = parameters?["arg"]?.GetValue<string>() ?? "";
            var body = _on(arg);
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["type"] = "plugin",
                ["output"] = body.ToJsonString()
            });
        }
    }
}
