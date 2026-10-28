namespace Hikyaku.Core;

/// <summary>Raw persistence for per-installation preferences. Never holds secrets.</summary>
public interface IPreferenceStore
{
    Task<string?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(string content, CancellationToken ct = default);
}

public sealed class FilePreferenceStore(string path) : IPreferenceStore
{
    public async Task<string?> LoadAsync(CancellationToken ct = default)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;

    public async Task SaveAsync(string content, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true); // atomic replace: a crash never leaves a half-written file
    }
}
