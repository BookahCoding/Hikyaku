using System.Windows;
using System.Windows.Input;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Reading position while a chat streams: a chat opens at its newest message, a growing reply stays in view while the reader is
/// at the bottom, and scrolling up stops it (nothing yanks the reader away from earlier text). While away from the bottom a
/// "Jump to latest" button offers the way back, and says when there is new content below. The scrolling itself is the window's;
/// this holds the decisions (<see cref="TailFollower"/>).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly TailFollower _tail = new();
    private DateTime _ignoreGrowthUntil = DateTime.MinValue;
    public Func<DateTime> TailClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The window should scroll the message list to its end.</summary>
    public event Action? ScrollToEndRequested;

    public ICommand? JumpToLatestCommand { get; private set; }

    public bool FollowingLatest => _tail.Following;
    public bool HasNewBelow => _tail.NewBelow;
    public Visibility JumpToLatestVisibility => _tail.AwayFromBottom ? Visibility.Visible : Visibility.Collapsed;
    public string JumpToLatestLabel => _tail.NewBelow ? "New messages below  ↓" : "Jump to latest  ↓";

    private void InitTail() => JumpToLatestCommand = new RelayCommand(_ =>
    {
        _tail.JumpToLatest();
        RaiseTail();
        ScrollToEndRequested?.Invoke();
    });

    /// <summary>The message list scrolled or changed height. Returns true when the window should scroll to the end now.</summary>
    public bool ReportMessageScroll(double offset, double extent, double viewport, double offsetDelta, double extentDelta)
    {
        // Older messages loaded above grow the list without adding anything below the reader.
        if (!_tail.Following && TailClock() < _ignoreGrowthUntil) extentDelta = 0;
        var scroll = _tail.OnScroll(offset, extent, viewport, offsetDelta, extentDelta);
        RaiseTail();
        return scroll;
    }

    private void IgnoreTailGrowthBriefly() => _ignoreGrowthUntil = TailClock().AddMilliseconds(600);

    /// <summary>A different chat is showing (or none): start at the newest message and follow it.</summary>
    private void ResetTail()
    {
        _tail.Reset();
        RaiseTail();
        ScrollToEndRequested?.Invoke();
    }

    private void RaiseTail()
    {
        Raise(nameof(FollowingLatest));
        Raise(nameof(HasNewBelow));
        Raise(nameof(JumpToLatestVisibility));
        Raise(nameof(JumpToLatestLabel));
    }
}
