using System.IO;
using System.Windows;
using Hikyaku.Core;
using Microsoft.Win32;

namespace Hikyaku.App;

/// <summary>Asks on this PC while the agent waits. Simple and Advanced both ask; closing the dialog answers no.</summary>
public sealed class FolderBridgePrompts(MainViewModel vm) : IFolderBridgePrompts
{
    public Task<string?> ChooseFolderAsync(string askedPath, CancellationToken ct)
    {
        if (vm.BridgeChooseFolder is { } pick) return Task.FromResult(pick(askedPath));
        return OnUi(() =>
        {
            var dlg = new OpenFolderDialog { Title = "Share a folder with Hermes" };
            var start = Directory.Exists(askedPath) ? askedPath : Path.GetDirectoryName(askedPath);
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });
    }

    public Task<bool> AllowNetworkAsync(string path, CancellationToken ct) =>
        vm.BridgeConfirm is { } network
            ? Task.FromResult(network("network", path))
            : OnUi(() => YesNo(
                "Allow this network path?",
                path + "\n\nHermes can read and write inside it while Hikyaku is open."));

    public Task<bool> AllowKeyFileAsync(string path, CancellationToken ct) =>
        vm.BridgeConfirm is { } key
            ? Task.FromResult(key("key", path))
            : OnUi(() => YesNo(
                "Allow this key file?",
                Path.GetFileName(path) + "\n\nHermes can read and write this file. Other key files stay blocked."));

    public Task<bool> ConfirmDeleteAsync(string path, bool folder, CancellationToken ct)
    {
        if (vm.BridgeConfirm is { } confirm) return Task.FromResult(confirm(folder ? "delete-folder" : "delete-file", path));
        var detail = folder
            ? "Delete this folder. Everything inside it goes."
            : "Delete this file.";
        return OnUi(() => YesNo(folder ? "Delete this folder?" : "Delete this file?", path + "\n\n" + detail));
    }

    private static bool YesNo(string title, string body) =>
        MessageBox.Show(body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static Task<T> OnUi<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return Task.FromResult(action());
        return dispatcher.InvokeAsync(action).Task;
    }
}
