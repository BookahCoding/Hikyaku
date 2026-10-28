using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 50 — folders the agent may use on this PC, remembered per gateway and profile.</summary>
public class Checkpoint50FolderBridgeTests
{
    private static ProfileKey Key(string profile) => new("gw", profile);

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
}
