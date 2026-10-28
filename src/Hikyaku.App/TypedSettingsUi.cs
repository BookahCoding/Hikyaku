using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>One setting in the typed editor. The edit lives in <see cref="EditText"/> until it is saved.</summary>
public sealed class SettingRowVM : Observable
{
    private string _editText;
    private string _baselineText;
    private string _error = "";

    public SettingRowVM(SettingEntry entry, MainViewModel owner)
    {
        Entry = entry;
        _baselineText = entry.IsSecret ? "" : TypedSettings.Display(entry.Value);
        _editText = _baselineText;
        SaveCommand = new RelayCommand(_ => _ = owner.SaveSettingAsync(this), _ => Entry.IsEditable && IsDirty && !owner.SettingsBusy);
    }

    public SettingEntry Entry { get; private set; }
    public string Path => Entry.Path;
    public string Category => Entry.Category;
    public string Description => Entry.Description;
    public bool IsBool => Entry.IsEditable && Entry.Type == "boolean";
    public bool IsSelect => Entry.IsEditable && Entry.Type == "select";
    public bool IsText => Entry.IsEditable && Entry.Type is "string" or "number";
    public IReadOnlyList<string> Options => Entry.Options;
    public Visibility BoolVisibility => IsBool ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectVisibility => IsSelect ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => IsText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => Entry.IsEditable ? Visibility.Collapsed : Visibility.Visible;
    public string ReadOnlyText => Entry.IsSecret
        ? "(hidden) Secrets are not shown or edited here."
        : (Entry.DisplayValue.Length > 0 ? Entry.DisplayValue + "  ·  " : "") + "edit it in the Config tab";
    public bool Dirty => IsDirty;
    public Visibility SaveVisibility => Entry.IsEditable && IsDirty ? Visibility.Visible : Visibility.Collapsed;

    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value ?? "";
            Raise(); Raise(nameof(EditBool)); Raise(nameof(Dirty)); Raise(nameof(SaveVisibility));
            Error = "";
            ((RelayCommand)SaveCommand).Refresh();
        }
    }

    public bool EditBool
    {
        get => _editText == "true";
        set => EditText = value ? "true" : "false";
    }

    public string Error { get => _error; internal set { Set(ref _error, value); Raise(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => _error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    internal bool IsDirty => _editText != _baselineText;
    public string BaselineText => _baselineText;
    public ICommand SaveCommand { get; }

    /// <summary>The gateway now holds the saved value: it becomes the new baseline.</summary>
    internal void Accept(SettingEntry saved)
    {
        Entry = saved;
        _baselineText = TypedSettings.Display(saved.Value);
        _editText = _baselineText;
        Raise(nameof(EditText)); Raise(nameof(EditBool)); Raise(nameof(Dirty)); Raise(nameof(SaveVisibility));
        Error = "";
        ((RelayCommand)SaveCommand).Refresh();
    }
}

/// <summary>
/// The schema-driven settings editor (Capabilities → Settings; <b>Advanced view only</b>, <see cref="ExperienceFeature.ConfigEditor"/>). Each setting of the
/// active profile is a row you can change and save on its own: a switch, a list of the gateway's own choices, or a checked number / text. A save
/// is confirmed in words naming the profile, the setting, the old and new value (with an extra warning for settings that govern what the agent may
/// do), sends a patch holding just that one key, then reads the value back. Secrets (keys, tokens, passwords) are never shown or edited here, lists are
/// edited in the Config tab, and with no confirmation handler nothing is sent.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<ConfigApi?> _schemaApi = () => null;
    private IReadOnlyList<SettingEntry> _allSettings = [];
    private string _settingsQuery = "", _settingsStatus = "", _settingsProfile = "";
    private bool _settingsLoading, _settingsBusy;
    private int _settingsLoadId;

    public ObservableCollection<SettingRowVM> SettingRows { get; } = new();
    public ICommand? LoadSettingsCommand { get; private set; }

    public void AttachTypedSettings(Func<ConfigApi?> schemaApi) => _schemaApi = schemaApi;

    public string SettingsQuery { get => _settingsQuery; set { Set(ref _settingsQuery, value ?? ""); ApplySettingsFilter(); } }
    public string SettingsStatus { get => _settingsStatus; private set { Set(ref _settingsStatus, value); Raise(nameof(SettingsStatusVisibility)); } }
    public Visibility SettingsStatusVisibility => _settingsStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool SettingsLoading { get => _settingsLoading; private set { Set(ref _settingsLoading, value); (LoadSettingsCommand as RelayCommand)?.Refresh(); } }
    public bool SettingsBusy { get => _settingsBusy; private set { Set(ref _settingsBusy, value); foreach (var r in SettingRows) ((RelayCommand)r.SaveCommand).Refresh(); } }

    /// <summary>Synthetic UI acceptance only: shows settings without any gateway.</summary>
    internal void AcceptanceSetSettings(IEnumerable<SettingEntry> entries)
    {
        _allSettings = entries.ToList();
        ApplySettingsFilter();
        SettingsStatus = $"{_allSettings.Count} settings for profile default. (synthetic)";
    }

    private void InitTypedSettings() =>
        LoadSettingsCommand = new RelayCommand(_ => _ = LoadSettingsAsync(), _ => !_settingsLoading);

    private bool SettingsProfile(out GatewayConfigEditor editor, out ConfigApi schema, out string profile, bool forAction = false)
    {
        editor = null!; schema = null!; profile = "";
        if (!Shows(ExperienceFeature.ConfigEditor)) return false;                       // Simple view never reaches it, whoever asks
        if (_configApi() is not { } e || _schemaApi() is not { } s) { SettingsStatus = "Connect to a gateway to edit its settings."; return false; }
        var p = ActiveProfileId();
        if (p is null || !CronAllowed(p)) { SettingsStatus = "Choose a profile first."; return false; }
        if (_settingsProfile != p)
        {
            var switched = _settingsProfile.Length > 0;
            if (switched) { _allSettings = []; SettingRows.Clear(); }                       // never show one profile's settings under another
            _settingsProfile = p;
            if (forAction && switched) { SettingsStatus = "The profile changed, so the list was cleared. Load the settings again."; return false; }   // a row from the old profile is never saved
        }
        editor = e; schema = s; profile = p;
        return true;
    }

    private void ApplySettingsFilter()
    {
        SettingRows.Clear();
        foreach (var e in TypedSettings.Filter(_allSettings, _settingsQuery).Take(500)) SettingRows.Add(new SettingRowVM(e, this));
    }

    public async Task LoadSettingsAsync()
    {
        if (!SettingsProfile(out var editor, out var schemaApi, out var profile)) return;
        var id = ++_settingsLoadId;
        SettingsLoading = true;
        SettingsStatus = "Loading…";
        try
        {
            var schema = await schemaApi.GetSchemaAsync(profile, CancellationToken.None).ConfigureAwait(true);
            var values = await editor.ValuesAsync(profile, CancellationToken.None).ConfigureAwait(true);
            if (id != _settingsLoadId) return;
            _allSettings = TypedSettings.Flatten(schema, values);
            ApplySettingsFilter();
            SettingsStatus = _allSettings.Count == 0 ? "The gateway lists no settings." : $"{_allSettings.Count} settings for profile {profile}.";
        }
        catch (RestException ex) { if (id == _settingsLoadId) SettingsStatus = ConfigReason(ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { if (id == _settingsLoadId) SettingsStatus = "Couldn't reach the gateway: " + ex.Message; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        { if (id == _settingsLoadId) SettingsStatus = "The gateway's settings could not be read (unexpected answer)."; }
        finally { if (id == _settingsLoadId) SettingsLoading = false; }
    }

    /// <summary>Saves one setting after a yes. Nothing is sent for an invalid or unchanged value, for a secret or a list, or when a save is already running.</summary>
    public async Task SaveSettingAsync(SettingRowVM row)
    {
        if (_settingsBusy || !row.Entry.IsEditable || !SettingRows.Contains(row) || !SettingsProfile(out var editor, out _, out var profile, forAction: true)) return;
        var edit = TypedSettings.ParseEdit(row.Entry, row.EditText);
        if (!edit.Ok) { row.Error = edit.Problem!; return; }
        if (TypedSettings.Unchanged(row.Entry, edit.Value)) { row.Error = ""; SettingsStatus = "No change."; return; }

        var risk = TypedSettings.RiskWarning(row.Path);
        var message = $"Change the setting “{row.Path}” in profile “{profile}”?\n\nFrom: {(row.BaselineText.Length > 0 ? row.BaselineText : "(not set)")}\nTo: {(row.EditText.Length > 0 ? row.EditText : "(empty)")}\n\n" +
                      "It is written into config.yaml on the gateway and applies from the next chat." + (risk is null ? "" : "\n\n⚠ " + risk);
        if (ConfirmConfig?.Invoke("Change setting", message) != true) { SettingsStatus = "Nothing was saved."; return; }

        SettingsBusy = true;
        SettingsStatus = $"Saving {row.Path}…";
        try
        {
            await editor.SavePatchAsync(profile, TypedSettings.PatchFor(row.Path, edit.Value!), CancellationToken.None).ConfigureAwait(true);
            // Read it back: the gateway may have settled more than was asked (and this shows what it actually holds).
            var after = await editor.ValuesAsync(profile, CancellationToken.None).ConfigureAwait(true);
            row.Accept(row.Entry with { Value = TypedSettings.ValueAt(after, row.Path) ?? edit.Value });
            SettingsStatus = $"Saved {row.Path}. It applies from the next chat.";
        }
        catch (RestException ex) { row.Error = ConfigReason(ex); SettingsStatus = $"Couldn't save {row.Path}."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        { row.Error = "Couldn't reach the gateway: " + ex.Message; SettingsStatus = $"Couldn't save {row.Path}."; }
        finally { SettingsBusy = false; }
    }
}
