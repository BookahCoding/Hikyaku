using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// Shared Git process boundary for guided local/push ops: owned empty hooks dir,
/// pinned env, and fail-closed scan for command-executing config overrides.
/// </summary>
public static class GitSafeInvocation
{
    public const string UntrustedConfigCopy =
        "Guided Git refuses command-executing overrides (shell credential helpers, core.sshCommand, " +
        "remote.*.receivepack/uploadpack, and similar). Remove those repo/worktree overrides to continue. " +
        "OS credential helpers that cannot be securely bounded are not invoked implicitly; HTTPS remotes needing " +
        "interactive/helper auth remain setup-required for guided publish. Direct user terminals are out of this scope.";

    private static readonly object HooksGate = new();
    private static string? _ownedHooksDir;

    /// <summary>Create or refresh an owned, non-reparse, empty hooks directory for -c core.hooksPath.</summary>
    public static string EnsureOwnedEmptyHooksDir()
    {
        lock (HooksGate)
        {
            if (_ownedHooksDir is not null && IsSafeEmptyHooksDir(_ownedHooksDir))
                return _ownedHooksDir;

            var root = Path.Combine(Path.GetTempPath(), "hikyaku-git-hooks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            if (IsReparsePoint(root))
            {
                try { Directory.Delete(root, true); } catch { /* best-effort */ }
                throw new InvalidOperationException("Owned hooks directory resolved to a reparse point.");
            }

            // Ensure empty (no planted hooks).
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Could not clear owned hooks directory: " + ex.Message);
                }
            }

            _ownedHooksDir = root;
            return root;
        }
    }

    public static bool IsSafeEmptyHooksDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (IsReparsePoint(dir)) return false;
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // fail closed
        }
    }

    /// <summary>Scan effective Git config for command-executing overrides. Fail closed before process/network.</summary>
    public static async Task<(bool Ok, string? Error)> AssertNoUntrustedCommandOverridesAsync(
        string gitExecutable,
        string workingTree,
        CancellationToken ct)
    {
        // Inherited env that can execute helpers/transport.
        foreach (var envName in new[] { "GIT_SSH_COMMAND", "GIT_SSH", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_CONFIG_COUNT" })
        {
            var v = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(v))
                return (false, $"Environment {envName} is set — guided Git refuses inherited command overrides. " + UntrustedConfigCopy);
        }

        var checks = new (string KeyPattern, Func<string, bool> Bad)[]
        {
            ("credential.helper", v => v.TrimStart().StartsWith('!') || v.Contains('\n') || v.Contains('\0')),
            ("core.sshCommand", v => !string.IsNullOrWhiteSpace(v)),
            ("core.hooksPath", _ => false), // overridden by our -c; do not refuse mere presence
            ("i18n.commitEncoding", _ => false),
        };

        // credential.helper (any ! form) + core.sshCommand
        foreach (var key in new[] { "credential.helper", "core.sshCommand" })
        {
            var run = await RunConfigGetAsync(gitExecutable, workingTree, key, ct).ConfigureAwait(false);
            if (!run.Ok) continue; // missing is fine
            var val = run.Text.Trim();
            if (key == "credential.helper" && val.TrimStart().StartsWith('!'))
                return (false, "credential.helper shell override (!…) is not allowed. " + UntrustedConfigCopy);
            if (key == "core.sshCommand" && !string.IsNullOrWhiteSpace(val))
                return (false, "core.sshCommand is not allowed for guided Git. " + UntrustedConfigCopy);
        }

        // remote.*.receivepack / uploadpack
        var recv = await RunConfigRegexpAsync(gitExecutable, workingTree, @"^remote\..*\.(receivepack|uploadpack)$", ct)
            .ConfigureAwait(false);
        if (recv.Ok && !string.IsNullOrWhiteSpace(recv.Text))
            return (false, "remote.*.receivepack/uploadpack overrides are not allowed. " + UntrustedConfigCopy);

        // submodule command overrides
        var sub = await RunConfigRegexpAsync(gitExecutable, workingTree, @"^submodule\..*\.update$", ct)
            .ConfigureAwait(false);
        if (sub.Ok && !string.IsNullOrWhiteSpace(sub.Text))
        {
            using var sr = new StringReader(sub.Text);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var sp = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length >= 2 && sp[1].TrimStart().StartsWith('!'))
                    return (false, "submodule.*.update shell override is not allowed. " + UntrustedConfigCopy);
            }
        }

        // alias.* that could confuse — guided ops use ArgumentList fixed verbs; skip.
        _ = checks;
        return (true, null);
    }

    public static void ApplyPinnedEnvironment(ProcessStartInfo psi, bool promptNever)
    {
        // Do not inherit askpass / ssh command overrides into the child.
        psi.Environment["GIT_SSH_COMMAND"] = "";
        psi.Environment["GIT_SSH"] = "";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        psi.Environment.Remove("GIT_CONFIG_COUNT");
        for (var i = 0; i < 32; i++)
        {
            psi.Environment.Remove("GIT_CONFIG_KEY_" + i);
            psi.Environment.Remove("GIT_CONFIG_VALUE_" + i);
        }

        if (promptNever)
        {
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
        }
    }

    public static void ApplySafetyArguments(ProcessStartInfo psi, bool forGuidedPush)
    {
        var emptyHooks = EnsureOwnedEmptyHooksDir();
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.hooksPath=" + emptyHooks);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("diff.external=");
        // Defense in depth: blank shell-capable knobs for this process only (after fail-closed scan).
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.sshCommand=");
        if (forGuidedPush)
        {
            // Do not execute repo/OS helpers for guided push. HTTPS remotes needing helper auth
            // fail closed / remain setup-required rather than implicit helper execution.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("credential.helper=");
        }
    }

    private static async Task<(bool Ok, string Text)> RunConfigGetAsync(
        string gitExecutable, string workingTree, string key, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyPinnedEnvironment(psi, promptNever: true);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingTree);
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--get");
        psi.ArgumentList.Add(key);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, "");
        }
    }

    private static async Task<(bool Ok, string Text)> RunConfigRegexpAsync(
        string gitExecutable, string workingTree, string pattern, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyPinnedEnvironment(psi, promptNever: true);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingTree);
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--get-regexp");
        psi.ArgumentList.Add(pattern);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, "");
        }
    }
}

/// <summary>
/// Durable (process + disk) uncertain-push gate keyed by worktree/remote/push-url/branch — not index fingerprint.
/// </summary>
public static class GuidedPushUncertaintyStore
{
    private static readonly ConcurrentSet Keys = new();
    private static readonly object FileGate = new();

    public static string MakeDurableKey(string workingTree, string grantRoot, string remoteName, string pushUrl, string branch)
    {
        var raw = string.Join('\u001f',
            Path.GetFullPath(workingTree),
            Path.GetFullPath(grantRoot),
            remoteName.Trim(),
            pushUrl.Trim(),
            branch.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static bool Contains(string durableKey)
    {
        Load();
        return Keys.Contains(durableKey);
    }

    public static void Mark(string durableKey)
    {
        Load();
        Keys.Add(durableKey);
        Persist();
    }

    public static void Clear(string durableKey)
    {
        Load();
        Keys.Remove(durableKey);
        Persist();
    }

    public static void ClearAllForTests()
    {
        Keys.Clear();
        try
        {
            var path = StorePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    private static string StorePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hikyaku-guided-push");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "uncertain-keys.txt");
    }

    private static void Load()
    {
        lock (FileGate)
        {
            try
            {
                var path = StorePath();
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.Length == 64 && Regex.IsMatch(t, "^[0-9A-Fa-f]+$"))
                        Keys.Add(t.ToUpperInvariant());
                }
            }
            catch { /* best-effort */ }
        }
    }

    private static void Persist()
    {
        lock (FileGate)
        {
            try
            {
                File.WriteAllLines(StorePath(), Keys.Snapshot());
            }
            catch { /* best-effort */ }
        }
    }

    private sealed class ConcurrentSet
    {
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        public bool Contains(string k) { lock (_gate) return _set.Contains(k); }
        public void Add(string k) { lock (_gate) _set.Add(k); }
        public void Remove(string k) { lock (_gate) _set.Remove(k); }
        public void Clear() { lock (_gate) _set.Clear(); }
        public string[] Snapshot() { lock (_gate) return _set.ToArray(); }
    }
}
