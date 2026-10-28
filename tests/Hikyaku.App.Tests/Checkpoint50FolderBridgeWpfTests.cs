using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 50 — the open app asks before a folder, a network path, a key file, or a delete, in both views.
/// The key-file switch is Advanced only. Projects can take one share back. Simple still says a folder is shared.
/// </summary>
[Collection("StaMath")]
public class Checkpoint50FolderBridgeWpfTests
{
    private sealed class Mem : IPreferenceStore
    {
        private string? _content;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
        public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
    }

    private sealed class BridgePeer : IJsonRpcPeer
    {
        public string Op { get; set; } = "read";
        public string Path { get; set; } = "";
        public string Profile { get; set; } = "default";

        public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var arg = parameters?["arg"]?.GetValue<string>() ?? "";
            var verb = arg.Split(' ', 2)[0];
            var body = verb == "pending"
                ? new JsonObject
                {
                    ["ok"] = true,
                    ["pending"] = true,
                    ["id"] = "1",
                    ["profile"] = Profile,
                    ["op"] = Op,
                    ["path"] = Path
                }
                : new JsonObject { ["ok"] = true };
            return Task.FromResult<JsonNode?>(new JsonObject { ["type"] = "plugin", ["output"] = body.ToJsonString() });
        }
    }

    private static (MainViewModel Vm, FolderBridgeStore Store, BridgePeer Peer) Make(bool advanced)
    {
        var selection = new ProfileSelectionService(new Mem());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var peer = new BridgePeer();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null,
            rpc: () => peer);
        vm.ApplyExperience(advanced);
        var store = new FolderBridgeStore(new Mem());
        vm.AttachFolderBridge(store);
        return (vm, store, peer);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static bool PumpUntil(Func<bool> done, int ms = 5000)
    {
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (done() || DateTime.UtcNow - started > TimeSpan.FromMilliseconds(ms)) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return done();
    }

    private static void Tick(MainViewModel vm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = vm.TickFolderBridgeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
            else done.TrySetResult();
        });
        Assert.True(PumpUntil(() => done.Task.IsCompleted));
        if (done.Task.IsFaulted) throw done.Task.Exception!;
    }

    [Fact]
    public void Asks_reach_the_open_app_in_simple_and_in_advanced()
    {
        RunSta(() =>
        {
            using var dir = new TempDir();
            foreach (var advanced in new[] { false, true })
            {
                var (vm, store, peer) = Make(advanced);
                var calls = new List<string>();
                vm.BridgeChooseFolder = path => { calls.Add("folder:" + path); return null; };
                vm.BridgeConfirm = (kind, path) => { calls.Add(kind + ":" + path); return false; };

                peer.Op = "read";
                peer.Path = Path.Combine(dir.Root, "note.txt");
                Tick(vm);
                Assert.Contains(calls, c => c.StartsWith("folder:"));

                calls.Clear();
                peer.Op = "read";
                peer.Path = @"\\server\share\a.txt";
                Tick(vm);
                Assert.Contains(calls, c => c.StartsWith("network:"));

                store.AddFolderAsync(new ProfileKey("gw", "default"), dir.Root, false).GetAwaiter().GetResult();
                calls.Clear();
                peer.Op = "read";
                peer.Path = Path.Combine(dir.Root, "id_rsa");
                Tick(vm);
                Assert.Contains(calls, c => c.StartsWith("key:"));

                calls.Clear();
                peer.Op = "delete_file";
                peer.Path = Path.Combine(dir.Root, "note.txt");
                Tick(vm);
                Assert.Contains(calls, c => c.StartsWith("delete-file:"));

                calls.Clear();
                peer.Op = "delete_folder";
                peer.Path = Path.Combine(dir.Root, "box");
                Tick(vm);
                Assert.Contains(calls, c => c.StartsWith("delete-folder:"));
            }
        });
    }

    [Fact]
    public void The_key_file_switch_is_advanced_only_and_it_is_remembered()
    {
        RunSta(() =>
        {
            Assert.False(SettingsSections.For(false, false).FolderKeyFiles);
            Assert.False(SettingsSections.For(false, true).FolderKeyFiles);
            Assert.True(SettingsSections.For(true, false).FolderKeyFiles);

            var simple = Make(false).Vm;
            var (advanced, store, _) = Make(true);
            Assert.Equal(Visibility.Collapsed, simple.FolderKeyFilesVisibility);
            Assert.Equal(Visibility.Visible, advanced.FolderKeyFilesVisibility);

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = advanced.SetFolderKeyFilesOpenAsync(true).ContinueWith(t =>
            {
                if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
                else done.TrySetResult();
            });
            Assert.True(PumpUntil(() => done.Task.IsCompleted));
            Assert.True(store.GetAsync(new ProfileKey("gw", "default")).GetAwaiter().GetResult().KeyFilesOpen);
        });
    }

    [Fact]
    public void Removing_one_shared_folder_leaves_the_other()
    {
        RunSta(() =>
        {
            var (vm, store, _) = Make(true);
            var key = new ProfileKey("gw", "default");
            var first = Path.Combine(Path.GetTempPath(), "hikyaku-bridge-a-" + Guid.NewGuid().ToString("N"));
            var second = Path.Combine(Path.GetTempPath(), "hikyaku-bridge-b-" + Guid.NewGuid().ToString("N"));
            store.AddFolderAsync(key, first, false).GetAwaiter().GetResult();
            store.AddFolderAsync(key, second, false).GetAwaiter().GetResult();
            vm.RefreshBridgeSharesAsync().GetAwaiter().GetResult();
            Assert.Equal(2, vm.BridgeShares.Count);

            vm.RemoveBridgeShareCommand!.Execute(vm.BridgeShares[0]);
            Assert.True(PumpUntil(() => store.GetAsync(key).GetAwaiter().GetResult().Folders.Count == 1));
            var left = store.GetAsync(key).GetAwaiter().GetResult().Folders;
            Assert.Equal(second, left[0].CanonicalRoot);
            Assert.DoesNotContain(left, f => f.CanonicalRoot == first);
        });
    }

    [Fact]
    public void Simple_says_a_folder_is_shared_when_the_bridge_has_one()
    {
        RunSta(() =>
        {
            var (vm, store, _) = Make(false);
            var key = new ProfileKey("gw", "default");
            Assert.Equal("", vm.RiskNotice);
            var folder = Path.Combine(Path.GetTempPath(), "hikyaku-bridge-risk-" + Guid.NewGuid().ToString("N"));
            store.AddFolderAsync(key, folder, false).GetAwaiter().GetResult();
            vm.RefreshBridgeSharesAsync().GetAwaiter().GetResult();
            Assert.Contains("A folder on this computer is shared with Hermes.", vm.RiskNotice);

            vm.CurrentProjectPathHint = Path.Combine(Path.GetTempPath(), "git-grant");
            vm.RaiseRiskNotice();
            Assert.Contains("git-grant", vm.RiskNotice);
            Assert.Contains("A folder on this computer is shared with Hermes.", vm.RiskNotice);
        });
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hikyaku-bridge-ui-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
    }
}
