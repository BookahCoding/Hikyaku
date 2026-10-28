namespace Hikyaku.Core;

public enum UpdatePhase { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// The update screen's state machine. Nothing happens without an explicit call: Check (manual), Prepare
/// (explicit "Download and verify", never when no publisher key is embedded), then the caller asks for a second
/// confirmation before launching the installer. Re-entrant calls while busy are ignored.
/// </summary>
public sealed class UpdateFlow(
    Func<CancellationToken, Task<UpdateCheckResult>> check,
    Func<UpdateRelease, IProgress<long>?, CancellationToken, Task<StagedUpdate>> prepare,
    bool installerTrusted)
{
    public const string NotTrustedCopy =
        "Installing is disabled in this build: no publisher key is embedded yet, so a download could not be verified. " +
        "You can open the release page and install manually.";

    private readonly object _gate = new();
    private UpdatePhase _phase = UpdatePhase.Idle;
    private UpdateRelease? _release;
    private StagedUpdate? _staged;
    private string _message = "";

    public event Action? Changed;

    public UpdatePhase Phase { get { lock (_gate) return _phase; } }
    public UpdateRelease? Release { get { lock (_gate) return _release; } }
    public StagedUpdate? Staged { get { lock (_gate) return _staged; } }
    public string Message { get { lock (_gate) return _message; } }
    public bool InstallerTrusted => installerTrusted;
    public bool CanCheck { get { lock (_gate) return _phase is not (UpdatePhase.Checking or UpdatePhase.Downloading); } }
    public bool CanDownload { get { lock (_gate) return _phase == UpdatePhase.Available && installerTrusted; } }
    public bool CanInstall { get { lock (_gate) return _phase == UpdatePhase.Ready && _staged is not null; } }

    public async Task CheckAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_phase is UpdatePhase.Checking or UpdatePhase.Downloading) return;
            _phase = UpdatePhase.Checking;
            _message = "Checking for updates…";
        }
        Changed?.Invoke();
        UpdateCheckResult result;
        try { result = await check(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Set(UpdatePhase.Idle, "Check cancelled.");
            return;
        }
        catch (Exception ex)
        {
            // The buttons await this from an async void click handler: anything let out here would close the app.
            Set(UpdatePhase.Failed, "Could not check for updates: " + ex.Message);
            return;
        }
        switch (result)
        {
            case UpdateUpToDate u:
                Set(UpdatePhase.UpToDate, u.Latest is { } l ? $"You have the latest version (latest release: {l})." : "You have the latest version.");
                break;
            case UpdateAvailable a:
                lock (_gate) { _release = a.Release; _staged = null; }
                Set(UpdatePhase.Available, installerTrusted
                    ? $"Version {a.Release.Version} is available."
                    : $"Version {a.Release.Version} is available. " + NotTrustedCopy);
                break;
            case UpdateCheckFailed f:
                Set(UpdatePhase.Failed, f.Message);
                break;
        }
    }

    public async Task PrepareAsync(IProgress<long>? progress, CancellationToken ct)
    {
        UpdateRelease release;
        lock (_gate)
        {
            if (_phase != UpdatePhase.Available || _release is null) return;
            if (!installerTrusted) { _message = NotTrustedCopy; }
            else { _phase = UpdatePhase.Downloading; _message = "Downloading and verifying…"; }
            release = _release;
        }
        Changed?.Invoke();
        if (!installerTrusted) return;
        try
        {
            var staged = await prepare(release, progress, ct).ConfigureAwait(false);
            lock (_gate) _staged = staged;
            Set(UpdatePhase.Ready, $"Version {staged.Version} is downloaded and its signature verified. Nothing has been installed.");
        }
        catch (OperationCanceledException)
        {
            Set(UpdatePhase.Available, "Download cancelled.");
        }
        catch (UpdateException ex)
        {
            Set(UpdatePhase.Available, ex.Message);
        }
        catch (Exception ex)
        {
            // A dropped connection, a full disk or a closed window: report it and allow a retry; never let it close the app.
            Set(UpdatePhase.Available, "The download did not finish: " + ex.Message + " Nothing was installed.");
        }
    }

    /// <summary>Drops a prepared update (e.g. the user declined the final confirmation).</summary>
    public void Discard()
    {
        lock (_gate)
        {
            if (_phase != UpdatePhase.Ready) return;
            _staged = null;
        }
        Set(UpdatePhase.Available, "The prepared update was discarded.");
    }

    private void Set(UpdatePhase phase, string message)
    {
        lock (_gate) { _phase = phase; _message = message; }
        Changed?.Invoke();
    }
}
