using System.Text;

namespace Hikyaku.Core;

public sealed record TerminalStartResult(bool Ok, string? Error = null);

/// <summary>
/// Owns at most one interactive local terminal session for the bound profile/gateway.
/// Hidden drawer does not kill; Close / revoke / profile-or-gateway switch does.
/// Opt-in never auto-launches. Not a folder sandbox. Not a gateway shell bridge.
/// </summary>
public sealed class LocalTerminalController : IDisposable, IAsyncDisposable
{
    private readonly TerminalOptInStore _optIn;
    private readonly IPtySessionFactory _factory;
    private readonly int _maxScrollbackChars;
    private readonly object _gate = new();
    private readonly StringBuilder _scrollback = new();
    private IPtySession? _session;
    private ProfileKey? _bound;
    private TerminalHostKind _host = TerminalHostKind.LocalWindows;
    private long _ownership;
    private long _outputGeneration;
    private bool _drawerVisible;
    private string _workingDirectory = "";
    private string _shellPath = "";
    private string? _status;

    public LocalTerminalController(TerminalOptInStore optIn, IPtySessionFactory factory, int maxScrollbackChars = 200_000)
    {
        _optIn = optIn;
        _factory = factory;
        _maxScrollbackChars = Math.Max(1, maxScrollbackChars);
        DidLaunchOnConstruction = false;
    }

    public bool DidLaunchOnConstruction { get; }
    public IPtySession? ActiveSession { get { lock (_gate) return _session; } }
    public string ScrollbackText { get { lock (_gate) return _scrollback.ToString(); } }
    public long OutputGeneration { get { lock (_gate) return _outputGeneration; } }
    public string WorkingDirectory { get { lock (_gate) return _workingDirectory; } }
    public string ShellPath { get { lock (_gate) return _shellPath; } }
    public string HostLabel => "Local Windows";
    public string? Status { get { lock (_gate) return _status; } }
    public bool DrawerVisible { get { lock (_gate) return _drawerVisible; } }
    public ProfileKey? BoundProfile { get { lock (_gate) return _bound; } }

    public event Action? Changed;

    public void SetDrawerVisible(bool visible)
    {
        lock (_gate) _drawerVisible = visible;
        // Intentionally does not stop the session
        Raise();
    }

    public void BindProfile(ProfileKey? profile)
    {
        long oldOwnership;
        IPtySession? toStop = null;
        lock (_gate)
        {
            if (_bound is { } cur && profile is { } next
                && cur.GatewayId == next.GatewayId && cur.ProfileId == next.ProfileId)
                return;

            oldOwnership = _ownership;
            _ownership++;
            _outputGeneration++;
            _scrollback.Clear();
            _workingDirectory = "";
            _shellPath = "";
            _status = profile is null ? "No profile selected." : null;
            toStop = _session;
            _session = null;
            _bound = profile;
        }
        _ = StopSessionAsync(toStop, oldOwnership);
        Raise();
    }

    public async Task OnOptInChangedAsync(ProfileKey key, CancellationToken ct = default)
    {
        if (await _optIn.IsEnabledAsync(key, TerminalHostKind.LocalWindows, ct).ConfigureAwait(false))
            return;

        long own;
        IPtySession? toStop;
        lock (_gate)
        {
            // Only revoke/clear when this controller is bound to the revoked key.
            if (_bound is null
                || _bound.Value.GatewayId != key.GatewayId
                || _bound.Value.ProfileId != key.ProfileId)
                return;

            own = _ownership;
            _ownership++;
            _outputGeneration++;
            _scrollback.Clear();
            _workingDirectory = "";
            _shellPath = "";
            _status = "Terminal opt-in revoked.";
            toStop = _session;
            _session = null;
        }
        await StopSessionAsync(toStop, own).ConfigureAwait(false);
        Raise();
    }

    public async Task<TerminalStartResult> StartAsync(
        ProfileKey key,
        TerminalHostKind host,
        string shellPath,
        string workingDirectory,
        int columns,
        int rows,
        CancellationToken ct = default,
        string? arguments = null)
    {
        if (host != TerminalHostKind.LocalWindows)
            return new TerminalStartResult(false, TerminalOptInStore.RemoteUnavailableCopy);

        if (!TrustedShellDiscovery.IsTrustedInstalled(shellPath))
            return new TerminalStartResult(false, "Choose a supported installed shell.");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new TerminalStartResult(false, "Working directory is missing.");

        long ownershipBeforeAwait;
        lock (_gate)
        {
            // Bound profile must already match the start key (set by Sync/Bind). Refuse stale rebind.
            if (_bound is { } bound
                && (bound.GatewayId != key.GatewayId || bound.ProfileId != key.ProfileId))
                return new TerminalStartResult(false, "Start cancelled — profile or gateway changed.");
            ownershipBeforeAwait = _ownership;
        }

        if (!await _optIn.IsEnabledAsync(key, host, ct).ConfigureAwait(false))
            return new TerminalStartResult(false, "Enable local terminal opt-in for this gateway and profile first.");

        var (cols, rowCount) = TerminalViewport.Clamp(columns, rows);

        long ownership;
        lock (_gate)
        {
            if (_ownership != ownershipBeforeAwait)
                return new TerminalStartResult(false, "Start cancelled — profile or gateway changed.");
            if (_bound is { } bound
                && (bound.GatewayId != key.GatewayId || bound.ProfileId != key.ProfileId))
                return new TerminalStartResult(false, "Start cancelled — profile or gateway changed.");

            // Replace any existing session for this bind
            var prev = _session;
            _session = null;
            if (prev is not null)
            {
                ownership = _ownership;
                _ownership++;
                _ = StopSessionAsync(prev, ownership);
            }
            _bound = key;
            _host = host;
            _ownership++;
            ownership = _ownership;
            _scrollback.Clear();
            _outputGeneration++;
            _workingDirectory = Path.GetFullPath(workingDirectory);
            _shellPath = shellPath;
            _status = "Starting…";
        }
        Raise();

        try
        {
            var session = await _factory.StartAsync(
                new PtyStartRequest(shellPath, Path.GetFullPath(workingDirectory), cols, rowCount, arguments), ct)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (ownership != _ownership
                    || _bound is not { } bound
                    || bound.GatewayId != key.GatewayId
                    || bound.ProfileId != key.ProfileId)
                {
                    // Stale — ownership moved while starting
                    _ = session.DisposeAsync();
                    return new TerminalStartResult(false, "Start cancelled — profile or gateway changed.");
                }
                _session = session;
                _status = null;
                session.OutputReceived += chunk => OnOutput(ownership, chunk);
                session.Exited += code => OnExited(ownership, code);
            }
            // Begin I/O after handlers are installed (avoids startup subscription race).
            session.Begin();
            Raise();
            return new TerminalStartResult(true);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ownership == _ownership)
                {
                    _session = null;
                    _status = "Start failed: " + ex.Message;
                }
            }
            Raise();
            return new TerminalStartResult(false, ex.Message);
        }
    }

    public void WriteRaw(string data)
    {
        IPtySession? s;
        lock (_gate) s = _session;
        s?.Write(data);
    }

    /// <summary>Paste inserts text only — never appends Enter.</summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var sanitized = text.Replace("\r\n", "\n").Replace('\n', ' ').Replace("\r", "");
        WriteRaw(sanitized);
    }

    /// <summary>Submit the current line (CR). Distinct from paste.</summary>
    public void SubmitLine() => WriteRaw("\r");

    public void Resize(int columns, int rows)
    {
        var (cols, rowCount) = TerminalViewport.Clamp(columns, rows);
        IPtySession? s;
        lock (_gate) s = _session;
        s?.Resize(cols, rowCount);
    }

    public void CloseTerminal()
    {
        long own;
        IPtySession? toStop;
        lock (_gate)
        {
            own = _ownership;
            _ownership++;
            _outputGeneration++;
            toStop = _session;
            _session = null;
            _status = "Terminal closed.";
            // Keep scrollback cleared on explicit close to avoid lingering secrets in UI buffer
            _scrollback.Clear();
            _workingDirectory = "";
            _shellPath = "";
        }
        _ = StopSessionAsync(toStop, own);
        Raise();
    }

    public void ClearScrollbackForPrivacy()
    {
        lock (_gate)
        {
            _scrollback.Clear();
            _outputGeneration++;
        }
        Raise();
    }

    public ValueTask DisposeAsync()
    {
        CloseTerminal();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => CloseTerminal();

    private void OnOutput(long ownership, string chunk)
    {
        lock (_gate)
        {
            // Ownership alone gates acceptance: after Exited clears ActiveSession the
            // ConPTY read loop may still deliver the final chunk (one-shot /c race).
            // Close / revoke / rebind bump ownership and stop accepting.
            if (ownership != _ownership) return;
            _scrollback.Append(chunk);
            if (_scrollback.Length > _maxScrollbackChars)
            {
                var overflow = _scrollback.Length - _maxScrollbackChars;
                _scrollback.Remove(0, overflow);
            }
            _outputGeneration++;
        }
        Raise();
    }

    private void OnExited(long ownership, int? code)
    {
        lock (_gate)
        {
            if (ownership != _ownership) return;
            _session = null;
            _status = code is null ? "Shell exited." : $"Shell exited ({code}).";
            _outputGeneration++;
        }
        Raise();
    }

    private static async Task StopSessionAsync(IPtySession? session, long _)
    {
        if (session is null) return;
        try { session.Stop(); } catch { /* ignore */ }
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
    }

    private void Raise() => Changed?.Invoke();
}
