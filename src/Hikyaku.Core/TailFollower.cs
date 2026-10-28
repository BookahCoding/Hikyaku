namespace Hikyaku.Core;

/// <summary>
/// "Follow the newest message" for a chat that grows while it streams. The reader is following while they are at the bottom; a
/// reply that grows keeps the bottom in view; scrolling up stops following (so reading earlier text is never yanked away), and
/// scrolling back down to the bottom resumes it. While not following, new content only raises <see cref="NewBelow"/> so the window
/// can offer "Jump to latest". Pure state: the window feeds it scroll changes and does the scrolling it asks for.
/// </summary>
public sealed class TailFollower
{
    /// <summary>How close to the end still counts as "at the bottom" (pixels), so a half-line of slack does not lose the tail.</summary>
    public const double BottomSlack = 24;

    /// <summary>Movement smaller than this is layout noise, not the reader scrolling.</summary>
    private const double Noise = 1;

    public bool Following { get; private set; } = true;

    /// <summary>The content grew below the reader while they were not following.</summary>
    public bool NewBelow { get; private set; }

    /// <summary>The reader is looking at something other than the newest content.</summary>
    public bool AwayFromBottom { get; private set; }

    /// <summary>A different chat was opened (or this one was cleared): start at the newest message and follow.</summary>
    public void Reset()
    {
        Following = true;
        NewBelow = false;
        AwayFromBottom = false;
    }

    /// <summary>The reader asked to jump to the latest message: follow again.</summary>
    public void JumpToLatest()
    {
        Following = true;
        NewBelow = false;
        AwayFromBottom = false;
    }

    /// <summary>
    /// A scroll or layout change. <paramref name="offsetDelta"/> / <paramref name="extentDelta"/> are the changes since the last
    /// report. Returns true when the window should scroll to the end now.
    /// </summary>
    public bool OnScroll(double offset, double extent, double viewport, double offsetDelta, double extentDelta)
    {
        var atBottom = extent - (offset + viewport) <= BottomSlack;

        if (offsetDelta < -Noise)
        {
            // The reader moved up (even while the extent estimate shifted under a virtualized list): they decide, not the growth.
            Following = atBottom;
        }
        else if (Math.Abs(extentDelta) > Noise)
        {
            // Content changed height. Following keeps the end in view; otherwise remember there is more below.
            if (Following) { AwayFromBottom = false; NewBelow = false; return !atBottom || extentDelta > 0; }
            if (extentDelta > 0) NewBelow = true;
        }
        else
        {
            // A plain scroll: following is exactly "at the bottom".
            Following = atBottom;
        }

        AwayFromBottom = !atBottom && !Following;
        if (atBottom) NewBelow = false;
        return false;
    }
}
