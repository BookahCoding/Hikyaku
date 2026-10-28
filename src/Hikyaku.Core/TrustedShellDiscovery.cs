namespace Hikyaku.Core;

public sealed record TrustedShell(string Id, string DisplayName, string Path);

/// <summary>Only well-known installed shells under System32 / Program Files — never arbitrary user paths from clipboard.</summary>
public static class TrustedShellDiscovery
{
    public static IReadOnlyList<TrustedShell> EnumerateInstalled()
    {
        var list = new List<TrustedShell>();
        void Add(string id, string display, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                if (!File.Exists(full)) return;
                if (full.Contains("..", StringComparison.Ordinal)) return;
                list.Add(new TrustedShell(id, display, full));
            }
            catch { /* skip */ }
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Add("cmd", "Command Prompt (cmd)", System.IO.Path.Combine(system, "cmd.exe"));
        Add("powershell", "Windows PowerShell", System.IO.Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Add("pwsh", "PowerShell 7+", System.IO.Path.Combine(pf, "PowerShell", "7", "pwsh.exe"));

        var gitBash = System.IO.Path.Combine(pf, "Git", "bin", "bash.exe");
        Add("git-bash", "Git Bash", gitBash);

        return list;
    }

    public static string? ResolvePreferred(IEnumerable<string?> candidates)
    {
        var installed = EnumerateInstalled().Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                var full = System.IO.Path.GetFullPath(c);
                if (installed.Contains(full)) return full;
            }
            catch { /* skip */ }
        }
        return EnumerateInstalled().FirstOrDefault()?.Path;
    }

    public static bool IsTrustedInstalled(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            return EnumerateInstalled().Any(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
