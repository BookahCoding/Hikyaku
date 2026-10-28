using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One folder or key file this profile has shared from this PC.</summary>
public sealed class BridgeShareRowVM(string path, bool folder)
{
    public string Path { get; } = path;
    public bool Folder { get; } = folder;
    public string Label { get; } = folder ? path : path + " (key file)";
}

public sealed partial class MainViewModel
{
    private FolderBridgeStore? _bridgeStore;
    private FolderBridgeLoop? _bridgeLoop;
    private CancellationTokenSource? _bridgeCts;
    private int _bridgeEpoch;
    private bool _folderKeyFilesOpen;

    /// <summary>Test seam and a stand-in for the folder picker. Null uses the real picker.</summary>
    public Func<string, string?>? BridgeChooseFolder { get; set; }

    /// <summary>Test seam for the yes/no asks. The kind is network, key, delete-file, or delete-folder.</summary>
    public Func<string, string, bool>? BridgeConfirm { get; set; }

    public ObservableCollection<BridgeShareRowVM> BridgeShares { get; } = new();
    public ICommand? RemoveBridgeShareCommand { get; private set; }

    public bool FolderKeyFilesOpen => _folderKeyFilesOpen;
    public Visibility FolderKeyFilesVisibility => Vis(ExperienceFeature.SettingsFolderKeyFiles);

    public string FolderBridgePluginHint =>
        _bridgeLoop?.PluginMissing == true && Shows(ExperienceFeature.SettingsFolderKeyFiles)
            ? "hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-files --enable"
            : "";

    public Visibility FolderBridgePluginHintVisibility =>
        FolderBridgePluginHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void AttachFolderBridge(FolderBridgeStore store)
    {
        _bridgeStore = store;
        _bridgeLoop = new FolderBridgeLoop(_rpc, store, ActiveProfileKey, new FolderBridgePrompts(this));
        RemoveBridgeShareCommand = new RelayCommand(p => _ = RemoveBridgeShareAsync(p));
        Raise(nameof(RemoveBridgeShareCommand));
        _ = RefreshBridgeSharesAsync();
    }

    /// <summary>Polls while the gateway client is connected. A missing client makes the tick do nothing.</summary>
    public void StartFolderBridge()
    {
        if (_bridgeLoop is null || _bridgeCts is not null) return;
        var cts = new CancellationTokenSource();
        _bridgeCts = cts;
        _ = PollBridgeAsync(cts.Token);
    }

    public void StopFolderBridge()
    {
        var cts = _bridgeCts;
        _bridgeCts = null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
    }

    public Task TickFolderBridgeAsync(CancellationToken ct = default) =>
        _bridgeLoop?.TickAsync(ct) ?? Task.CompletedTask;

    public async Task SetFolderKeyFilesOpenAsync(bool open)
    {
        Interlocked.Increment(ref _bridgeEpoch);
        _folderKeyFilesOpen = open;
        Raise(nameof(FolderKeyFilesOpen));
        if (_bridgeStore is null || ActiveProfileKey() is not { } key) return;
        await _bridgeStore.SetKeyFilesOpenAsync(key, open).ConfigureAwait(true);
    }

    public async Task RefreshBridgeSharesAsync()
    {
        var epoch = Interlocked.Increment(ref _bridgeEpoch);
        BridgeProfile? profile = null;
        if (_bridgeStore is not null && ActiveProfileKey() is { } key)
            profile = await _bridgeStore.GetAsync(key).ConfigureAwait(true);
        if (epoch != Volatile.Read(ref _bridgeEpoch)) return;
        BridgeShares.Clear();
        if (profile is not null)
        {
            foreach (var folder in profile.Folders)
                BridgeShares.Add(new BridgeShareRowVM(folder.CanonicalRoot, true));
            foreach (var file in profile.KeyFiles)
                BridgeShares.Add(new BridgeShareRowVM(file.CanonicalPath, false));
            _folderKeyFilesOpen = profile.KeyFilesOpen;
            Raise(nameof(FolderKeyFilesOpen));
        }
        RaiseBridgeSurfaces();
    }

    private async Task RemoveBridgeShareAsync(object? row)
    {
        if (row is not BridgeShareRowVM share || _bridgeStore is null || ActiveProfileKey() is not { } key) return;
        if (share.Folder) await _bridgeStore.RemoveFolderAsync(key, share.Path).ConfigureAwait(true);
        else await _bridgeStore.RevokeKeyFileAsync(key, share.Path).ConfigureAwait(true);
        await RefreshBridgeSharesAsync().ConfigureAwait(true);
    }

    private async Task PollBridgeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_bridgeLoop is { } loop) await loop.TickAsync(ct).ConfigureAwait(true);
                RaiseBridgeSurfaces();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* the next poll asks again */ }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RaiseBridgeSurfaces()
    {
        Raise(nameof(FolderBridgePluginHint));
        Raise(nameof(FolderBridgePluginHintVisibility));
        Raise(nameof(FolderKeyFilesVisibility));
        RaiseRiskNotice();
    }
}
