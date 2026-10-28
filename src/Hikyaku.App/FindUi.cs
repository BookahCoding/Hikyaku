using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Find in chat: a literal search over the messages loaded in the open chat, with Next/Previous and a message-level mark
/// (no per-word highlight). Purely local: it never edits the transcript or the composer and sends nothing.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _findOpen;
    private string _findText = "";
    private string _findStatus = "";
    private List<MessageVM> _findHits = new();
    private int _findIndex = -1;
    private bool _findHooked;

    public ICommand? OpenFindCommand { get; private set; }
    public ICommand? CloseFindCommand { get; private set; }
    public ICommand? FindNextCommand { get; private set; }
    public ICommand? FindPreviousCommand { get; private set; }

    /// <summary>Raised when the current match should be scrolled into view.</summary>
    public event Action<MessageVM>? FindScrollRequested;

    public bool IsFindOpen => _findOpen;
    public Visibility FindVisibility => _findOpen ? Visibility.Visible : Visibility.Collapsed;
    public string FindStatus => _findStatus;

    /// <summary>The honest scope: history loads in pages, so older messages are not searched until loaded.</summary>
    public string FindScopeNote => "Searches the messages loaded in this chat view.";

    public string FindText
    {
        get => _findText;
        set
        {
            if (_findText == value) return;
            _findText = value;
            Raise();
            RecomputeFind(keepCurrent: false);
            if (_findIndex >= 0) FindScrollRequested?.Invoke(_findHits[_findIndex]);
        }
    }

    private void InitFindCommands()
    {
        OpenFindCommand = new RelayCommand(_ => OpenFind(), _ => SelectedConversation is not null);
        CloseFindCommand = new RelayCommand(_ => ResetFind(), _ => _findOpen);
        FindNextCommand = new RelayCommand(_ => StepFind(next: true), _ => _findHits.Count > 0);
        FindPreviousCommand = new RelayCommand(_ => StepFind(next: false), _ => _findHits.Count > 0);
    }

    private void OpenFind()
    {
        if (SelectedConversation is null) return;
        if (!_findHooked)
        {
            Messages.CollectionChanged += OnMessagesChangedForFind;
            _findHooked = true;
        }
        _findOpen = true;
        Raise(nameof(IsFindOpen));
        Raise(nameof(FindVisibility));
        (CloseFindCommand as RelayCommand)?.Refresh();
    }

    private void OnMessagesChangedForFind(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_findOpen && e.Action != NotifyCollectionChangedAction.Reset) RecomputeFind(keepCurrent: true);
    }

    private void StepFind(bool next)
    {
        // Streamed text changes without a collection change, so count again before moving.
        RecomputeFind(keepCurrent: true);
        if (_findHits.Count == 0) return;
        SetFindIndex(next ? TranscriptFinder.Next(_findIndex, _findHits.Count) : TranscriptFinder.Previous(_findIndex, _findHits.Count));
        FindScrollRequested?.Invoke(_findHits[_findIndex]);
    }

    private void RecomputeFind(bool keepCurrent)
    {
        var previous = keepCurrent && _findIndex >= 0 && _findIndex < _findHits.Count ? _findHits[_findIndex] : null;
        foreach (var m in _findHits) { m.IsFindMatch = false; m.IsFindCurrent = false; }

        var snapshot = Messages.ToList();
        var idx = TranscriptFinder.Find(snapshot.Select(m => m.Text).ToList(), _findText);
        _findHits = idx.Select(i => snapshot[i]).ToList();
        foreach (var m in _findHits) m.IsFindMatch = true;

        _findIndex = _findHits.Count == 0 ? -1 : Math.Max(0, previous is null ? 0 : _findHits.IndexOf(previous));
        ApplyFindIndex();
    }

    private void SetFindIndex(int index)
    {
        if (_findIndex >= 0 && _findIndex < _findHits.Count) _findHits[_findIndex].IsFindCurrent = false;
        _findIndex = index;
        ApplyFindIndex();
    }

    private void ApplyFindIndex()
    {
        if (_findIndex >= 0 && _findIndex < _findHits.Count) _findHits[_findIndex].IsFindCurrent = true;
        var query = _findText.Trim();
        _findStatus = query.Length == 0 ? ""
            : _findHits.Count == 0 ? (SelectedConversation is { HasEarlier: true } ? "No matches in loaded messages" : "No matches")
            : $"{_findIndex + 1} of {_findHits.Count}" + (_findHits.Count >= TranscriptFinder.MaxMatches ? "+" : "");
        Raise(nameof(FindStatus));
        (FindNextCommand as RelayCommand)?.Refresh();
        (FindPreviousCommand as RelayCommand)?.Refresh();
    }

    /// <summary>Closes find and removes every mark. Also runs when the open chat changes.</summary>
    private void ResetFind()
    {
        foreach (var m in _findHits) { m.IsFindMatch = false; m.IsFindCurrent = false; }
        _findHits = new();
        _findIndex = -1;
        _findText = "";
        _findStatus = "";
        _findOpen = false;
        Raise(nameof(FindText));
        Raise(nameof(FindStatus));
        Raise(nameof(IsFindOpen));
        Raise(nameof(FindVisibility));
        (OpenFindCommand as RelayCommand)?.Refresh();
        (CloseFindCommand as RelayCommand)?.Refresh();
        (FindNextCommand as RelayCommand)?.Refresh();
        (FindPreviousCommand as RelayCommand)?.Refresh();
    }
}
