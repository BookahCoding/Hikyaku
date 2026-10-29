using System.Collections.ObjectModel;
using System.Windows;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>A model in the chat box's model menu.</summary>
public sealed record ModelChoice(string Provider, string ProviderName, string Id, bool IsCurrent);

/// <summary>
/// The chat box, redesigned after the first live test (the maintainer asked for Claude Desktop's layout): one rounded box with
/// the text, + (attach) and a microphone on the left, and a single send / stop button on the right. Model, reasoning, fast and
/// YOLO moved out of a row of chips into slash commands (with suggestions while typing "/") and a small model menu in
/// Advanced view. YOLO stays visible in the box whenever it is on.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<SlashCommand> SlashSuggestions { get; } = new();

    private System.Windows.Input.ICommand? _pickSlash;
    /// <summary>Clicking a suggestion fills the box with that command.</summary>
    public System.Windows.Input.ICommand PickSlashCommand => _pickSlash ??= new RelayCommand(p => { if (p is SlashCommand c) CompleteSlashCommand(c); });

    public Visibility SlashSuggestionsVisibility => SlashSuggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised by "/model" with no name: the window opens the model menu.</summary>
    public event Action? ModelMenuRequested;

    public string ComposerPlaceholder =>
        CurrentProfileLabel is { Length: > 0 } who && ProfileChoices.Count > 1
            ? $"Message {who}…"
            : _advanced ? "Message Hermes…" : "Write a message…";

    public Visibility ComposerPlaceholderVisibility => _input.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The arrow: shown when idle, and while a reply runs once there is text to steer it with.</summary>
    public Visibility SendButtonVisibility => !_busy || _input.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The square: only while a reply is running.</summary>
    public Visibility StopButtonVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MicIdleVisibility => _voiceRecording ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MicRecordingVisibility => _voiceRecording ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModelMenuVisibility => Shows(ExperienceFeature.ModelChip) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Short label for the model menu button: the chosen model's id, or "Default model".</summary>
    public string ModelMenuLabel => SelectedConversation?.PendingModel is { Length: > 0 } m && m != "pending-pick" ? m : "Default model";

    /// <summary>YOLO on shows as a warning pill in the box (both views); turning it off is a click, turning it on is /yolo on.</summary>
    public Visibility YoloPillVisibility => SelectedConversation?.YoloEnabled == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A chat with nothing in it yet: the greeting shows and the chat box sits in the middle (as in Claude Desktop).</summary>
    public bool IsChatEmpty => Messages.Count == 0 && !_busy;

    public Visibility EmptyChatVisibility => IsChatEmpty && ChatsVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The greeting above the chat box in an empty chat; it names the agent when more than one is ticked.</summary>
    public string EmptyChatGreeting =>
        ProfileChoices.Count > 1 && CurrentProfileLabel is { Length: > 0 } who && who != ActiveProfile.GatewayDefault
            ? $"How can {who} help today?" : "How can I help you today?";   // a bare "default" id is not a name

    private bool _emptyHooked;

    private void HookEmptyChatState()
    {
        if (_emptyHooked) return;
        _emptyHooked = true;
        Messages.CollectionChanged += (_, _) => RaiseEmptyChatState();
    }

    private void RaiseEmptyChatState()
    {
        Raise(nameof(IsChatEmpty));
        Raise(nameof(EmptyChatVisibility));
        Raise(nameof(EmptyChatGreeting));
    }

    private void RaiseComposerState()
    {
        HookEmptyChatState();
        RaiseEmptyChatState();
        Raise(nameof(ComposerPlaceholderVisibility));
        Raise(nameof(ComposerPlaceholder));
        Raise(nameof(SendButtonVisibility));
        Raise(nameof(StopButtonVisibility));
        Raise(nameof(ModelMenuLabel));
        Raise(nameof(ModelMenuVisibility));
        Raise(nameof(YoloPillVisibility));
    }

    private void UpdateSlashSuggestions()
    {
        var next = SlashCommands.Suggest(_input, _advanced);
        if (next.Count == SlashSuggestions.Count && next.SequenceEqual(SlashSuggestions)) return;
        SlashSuggestions.Clear();
        foreach (var c in next) SlashSuggestions.Add(c);
        Raise(nameof(SlashSuggestionsVisibility));
    }

    /// <summary>Fills the box with the chosen command, ready for its argument.</summary>
    public void CompleteSlashCommand(SlashCommand command) =>
        InputText = "/" + command.Name + (command.Usage.Length > 0 ? " " : "");

    /// <summary>Runs a command typed in the box. True when it was one of ours (nothing is sent to the agent then).</summary>
    private async Task<bool> TryRunSlashCommandAsync(string raw)
    {
        if (!SlashCommands.TryParse(raw, out var name, out var arg) || name is "btw" or "bg") return false;
        var command = SlashCommands.All.First(c => c.Name == name);
        var reducesRisk = name == "yolo" && SlashCommands.OnOff(arg) == false;
        if (command.AdvancedOnly && !_advanced && !reducesRisk)
        {
            SetRewindNotice($"/{name} is part of the Advanced view (Settings → View).");
            return true;                                            // the text stays in the box; nothing is sent
        }
        InputText = "";
        switch (name)
        {
            case "new":
                NewChat();
                break;
            case "stop":
                if (CancelCommand.CanExecute(null)) CancelCommand.Execute(null);
                break;
            case "branch":
                if (BranchChatCommand?.CanExecute(null) == true) BranchChatCommand.Execute(null);
                else SetRewindNotice("This chat can't be branched yet (it needs a saved message, and no reply running).");
                break;
            case "model":
                if (arg.Length == 0) ModelMenuRequested?.Invoke();
                else await PickModelByNameAsync(arg).ConfigureAwait(true);
                break;
            case "reasoning":
                if (SlashCommands.ReasoningLevels.Contains(arg.ToLowerInvariant())) SetReasoning(arg.ToLowerInvariant());
                else SetRewindNotice("Use /reasoning none, low, medium or high.");
                break;
            case "fast":
                if (SlashCommands.OnOff(arg) is { } fast) await SetFastAsync(fast).ConfigureAwait(true);
                else SetRewindNotice("Use /fast on or /fast off.");
                break;
            case "yolo":
                if (SlashCommands.OnOff(arg) is not { } on) { SetRewindNotice("Use /yolo on or /yolo off."); break; }
                if ((SelectedConversation?.YoloEnabled == true) == on) { SetRewindNotice(on ? "YOLO is already on for this chat." : "YOLO is already off for this chat."); break; }
                if (ToggleYoloCommand.CanExecute(null)) ToggleYoloCommand.Execute(null);   // turning it on still asks first
                break;
        }
        RaiseComposerState();
        return true;
    }

    /// <summary>The models the gateway offers this chat (empty without a live connection).</summary>
    public async Task<IReadOnlyList<ModelChoice>> ModelChoicesAsync()
    {
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile) || !Shows(ExperienceFeature.ModelChip)) return [];
        if (_transport() is not HermesDashboardTransport hermes) return [];
        try
        {
            await hermes.EnsureConnectedAsync(default).ConfigureAwait(true);
            var catalog = await BindModelControls(convo).OptionsAsync(convo.SessionId, convo.Profile.ProfileId).ConfigureAwait(true);
            var current = convo.PendingModel ?? catalog.CurrentModel;
            return catalog.Providers.SelectMany(p => p.Models.Select(m =>
                new ModelChoice(p.Slug, p.Name, m.Id, m.Id == current && (convo.PendingProvider ?? catalog.CurrentProvider ?? p.Slug) == p.Slug))).ToList();
        }
        catch (Exception ex)
        {
            SetRewindNotice("Couldn't load the models: " + ex.Message);
            return [];
        }
    }

    private async Task PickModelByNameAsync(string name)
    {
        var choices = await ModelChoicesAsync().ConfigureAwait(true);
        var match = choices.FirstOrDefault(c => string.Equals(c.Id, name, StringComparison.OrdinalIgnoreCase))
            ?? (choices.Where(c => c.Id.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList() is [var only] ? only : null);
        if (match is null)
        {
            SetRewindNotice(choices.Count == 0 ? "No models are listed for this chat right now." : $"No single model matches \"{name}\". Type /model to pick from the list.");
            return;
        }
        await PickModelAsync(match).ConfigureAwait(true);
    }

    /// <summary>Uses the chosen model for this chat (for a new chat, from its first message). A paid switch the gateway wants confirmed is never made silently.</summary>
    public async Task PickModelAsync(ModelChoice choice)
    {
        var convo = SelectedConversation;
        if (convo is null || !_gate.Allow(Surface.Conversation, convo.Profile) || !Shows(ExperienceFeature.ModelChip)) return;
        try
        {
            var controls = BindModelControls(convo);
            if (string.IsNullOrEmpty(convo.SessionId) && string.IsNullOrEmpty(convo.StoredSessionId))
            {
                controls.RememberPending(choice.Id, choice.Provider);
                convo.PendingModel = choice.Id;
                convo.PendingProvider = choice.Provider;
                convo.ModelLabel = $"Model: {choice.Id} ({choice.Provider}, pending new chat)";
            }
            else
            {
                switch (await controls.SetModelAsync(choice.Id, choice.Provider).ConfigureAwait(true))
                {
                    case ModelSwitchNeedsConfirmation conf:
                        SetRewindNotice("Not switched: " + conf.Message);
                        break;
                    case ModelSwitchDone:
                        convo.PendingModel = choice.Id;
                        convo.PendingProvider = choice.Provider;
                        convo.ModelLabel = $"Model: {choice.Id} ({choice.Provider})";
                        break;
                    case ModelSwitchFailed fail:
                        SetRewindNotice("Couldn't switch the model: " + (fail.Error ?? "the gateway refused."));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            SetRewindNotice("Couldn't switch the model: " + ex.Message);
        }
        Raise(nameof(SelectedModelLabel));
        Raise(nameof(ModelMenuLabel));
    }
}
