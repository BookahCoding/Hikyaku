using System.Net.Http;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// A risk that lives on the gateway, not on this PC, and that Simple view must not hide: the profile-wide approvals mode set to <b>Off</b>
/// (Hermes runs commands without asking). It is read once when Simple view starts and when the profile or the connection changes (one
/// read of one setting; it is not gated by Advanced because it only reads). Simple shows it in the risk notice with an <b>Ask me first</b>
/// button, which sets the mode back to Manual after a confirmation: an action that reduces risk stays available in Simple, while
/// choosing a less safe mode is Advanced-only (Settings → Dangerous command approvals).
/// </summary>
public sealed partial class MainViewModel
{
    private bool _approvalsOff;
    private string _approvalsProfile = "";
    public ICommand? AskFirstCommand { get; private set; }

    private void InitApprovalsRisk() => AskFirstCommand = new RelayCommand(_ => _ = AskFirstAsync(), _ => _approvalsOff);

    private void SetApprovalsOff(bool off)
    {
        if (_approvalsOff == off) return;
        _approvalsOff = off;
        (AskFirstCommand as RelayCommand)?.Refresh();
        RaiseRiskNotice();
    }

    /// <summary>Reads the active profile's approvals mode. A failed read leaves what was known (it never raises a false alarm).</summary>
    public async Task RefreshApprovalsRiskAsync()
    {
        if (_schemaApi() is not { } api) return;
        var profile = ActiveProfileId();
        if (profile is null || !CronAllowed(profile)) { SetApprovalsOff(false); return; }
        if (_approvalsProfile != profile) { _approvalsProfile = profile; SetApprovalsOff(false); }    // another profile's setting is not this one's
        try
        {
            var values = await api.GetValuesAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (ActiveProfileId() != profile) return;
            SetApprovalsOff(values.ApprovalsMode == ApprovalMode.Off);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // unknown right now: keep what was shown
        }
    }

    /// <summary>Sets approvals back to Manual for the active profile, after a yes. With no confirmation handler nothing is sent.</summary>
    private async Task AskFirstAsync()
    {
        if (!_approvalsOff || _schemaApi() is not { } api) return;
        var profile = ActiveProfileId();
        if (profile is null || !CronAllowed(profile) || profile != _approvalsProfile) return;   // only the profile whose setting was shown
        if (ConfirmConfig?.Invoke("Ask me first",
                $"Make Hermes ask you before it runs commands again?\n\nThis is a setting of the whole profile “{profile}”, so it applies to everyone who uses it. From the next chat, each command will wait for you to allow it.") != true)
            return;
        try
        {
            await api.SavePatchAsync(profile, ConfigPatch.For("approvals.mode", "manual"), CancellationToken.None).ConfigureAwait(true);
            _riskActionError = "";
            SetApprovalsOff(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            RiskActionFailed("Couldn't change it. Ask the person who looks after Hermes.");
        }
    }

    private string _riskActionError = "";
    private void RiskActionFailed(string message) { _riskActionError = message; RaiseRiskNotice(); }
}
