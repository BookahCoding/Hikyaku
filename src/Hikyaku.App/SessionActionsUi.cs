using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Actions on a gateway chat in the sidebar list (Hermes Desktop's session context menu): rename, pin, archive / unarchive, mark
/// read / unread, delete, and an Archived view. They go through the gateway's mutation handshake (<see cref="SessionActionsApi"/>)
/// for the sidebar's profile only. Delete is confirmed in words; nothing is retried by the window; a row changes only after the
/// gateway accepted the write; opening a chat marks it read (best effort, never blocks opening).
/// </summary>
public sealed partial class MainViewModel
{
    private Func<SessionActionsApi?> _sessionActions = () => null;
    private string _sessionActionStatus = "";

    public void AttachSessionActions(Func<SessionActionsApi?> api) => _sessionActions = api;

    /// <summary>Asks for text (title, current value); null means cancelled.</summary>
    public Func<string, string, string?>? PromptText { get; set; }

    /// <summary>Asks before something that cannot be undone (title, message) → true only on an explicit yes. Unset means no.</summary>
    public Func<string, string, bool>? ConfirmSessionAction { get; set; }

    public string SessionActionStatus { get => _sessionActionStatus; private set { Set(ref _sessionActionStatus, value); Raise(nameof(SessionActionStatusVisibility)); } }
    public Visibility SessionActionStatusVisibility => _sessionActionStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand? ToggleArchivedViewCommand { get; private set; }
    public bool ArchivedView => _sessions()?.ShowArchived == true;
    public string ArchivedViewLabel => ArchivedView ? "Archived ✓" : "Archived";

    private void InitSessionActions() =>
        ToggleArchivedViewCommand = new RelayCommand(_ =>
        {
            if (_sessions() is not { } c) return;
            c.ShowArchived = !c.ShowArchived;
            Raise(nameof(ArchivedView));
            Raise(nameof(ArchivedViewLabel));
            SessionActionStatus = c.ShowArchived ? "Showing archived chats." : "";
            _ = RefreshSessionsAsync();
        });

    /// <summary>The verbs the sidebar menu offers for a row, with their labels (so the window builds nothing the model did not allow).</summary>
    public IReadOnlyList<(string Verb, string Label)> SessionMenuFor(SessionRowVM row)
    {
        var menu = new List<(string Verb, string Label)>
        {
            ("rename", "Rename…"),
            ("pin", row.IsPinned ? "Unpin" : "Pin")
        };
        // Simple view has no Archived list to bring a chat back from, so it does not offer to archive one (a chat already archived can still be restored).
        if (_advanced || row.ArchivedView) menu.Add(("archive", row.ArchivedView ? "Unarchive" : "Archive"));
        menu.Add(("unread", row.IsUnread ? "Mark as read" : "Mark as unread"));
        menu.Add(("delete", "Delete…"));
        return menu;
    }

    private string? SessionActionProfile()
    {
        var p = _sessions()?.ActiveProfile;
        return p is { } k && _gate.Allow(Surface.Navigation, k) ? k.ProfileId : null;
    }

    public async Task RunSessionActionAsync(SessionRowVM row, string verb)
    {
        if (_sessionActions() is not { } api) { SessionActionStatus = "Connect to a gateway first."; return; }
        if (SessionActionProfile() is not { } profile) { SessionActionStatus = "Choose a profile first."; return; }
        if (row.LoadedFor is { } listedFor && listedFor.ProfileId != profile) { SessionActionStatus = "The profile changed since this list was loaded. Refresh the list, then try again."; return; }
        var id = row.Summary.Id;
        var name = row.Title;
        try
        {
            switch (verb)
            {
                case "rename":
                {
                    var next = PromptText?.Invoke("Rename chat", row.Summary.Title ?? "");
                    if (next is null) return;                                // cancelled (or no prompt available)
                    next = next.Trim();
                    if (next.Length > 200) { SessionActionStatus = "That title is longer than 200 characters."; return; }
                    if (next == (row.Summary.Title ?? "")) return;
                    await api.RenameAsync(id, profile, next, CancellationToken.None).ConfigureAwait(true);
                    row.Title = next.Length > 0 ? next : row.Summary.Id;
                    SessionActionStatus = next.Length > 0 ? $"Renamed to “{next}”." : "Title cleared.";
                    break;
                }
                case "pin":
                    await api.SetPinnedAsync(id, profile, !row.IsPinned, CancellationToken.None).ConfigureAwait(true);
                    row.IsPinned = !row.IsPinned;
                    SessionActionStatus = row.IsPinned ? $"Pinned “{name}”." : $"Unpinned “{name}”.";
                    break;
                case "archive":
                {
                    var archive = !row.ArchivedView;
                    await api.SetArchivedAsync(id, profile, archive, CancellationToken.None).ConfigureAwait(true);
                    SessionRows.Remove(row);                                  // it moves to the other view
                    SessionActionStatus = archive ? $"Archived “{name}”. Show it again with Archived." : $"Moved “{name}” back to your chats.";
                    break;
                }
                case "unread":
                    await api.SetUnreadAsync(id, profile, !row.IsUnread, CancellationToken.None).ConfigureAwait(true);
                    row.IsUnread = !row.IsUnread;
                    SessionActionStatus = row.IsUnread ? $"Marked “{name}” unread." : $"Marked “{name}” read.";
                    break;
                case "delete":
                    if (ConfirmSessionAction?.Invoke("Delete chat",
                            $"Delete “{name}”?\n\nThe chat and its messages are removed from the gateway for every device. This can't be undone.") != true)
                        return;
                    await api.DeleteAsync(id, profile, CancellationToken.None).ConfigureAwait(true);
                    SessionRows.Remove(row);
                    SessionActionStatus = $"Deleted “{name}”.";
                    break;
            }
        }
        catch (RestException ex)
        {
            SessionActionStatus = ex.Message;
            if (ex.Failure == RestFailure.NotFound) { SessionRows.Remove(row); _ = RefreshSessionsAsync(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            SessionActionStatus = "Couldn't reach the gateway: " + ex.Message;
        }
    }

    /// <summary>Opening a chat reads it: the gateway's shared unread flag is cleared, best effort, without ever blocking the open.</summary>
    public async Task MarkReadOnOpenAsync(SessionRowVM row)
    {
        if (!row.IsUnread || _sessionActions() is not { } api || SessionActionProfile() is not { } profile) return;
        if (row.LoadedFor is { } listedFor && listedFor.ProfileId != profile) return;
        try
        {
            await api.SetUnreadAsync(row.Summary.Id, profile, false, CancellationToken.None).ConfigureAwait(true);
            row.IsUnread = false;
        }
        catch (Exception ex) when (ex is RestException or HttpRequestException or TaskCanceledException or AuthFailedException)
        {
            // A failed write leaves the flag as it was; the chat is open either way.
        }
    }
}
