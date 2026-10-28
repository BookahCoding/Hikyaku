using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 28 — follow the newest message while a reply streams, without ever yanking a reader away from earlier text.</summary>
public class Checkpoint28TailFollowerTests
{
    private const double View = 500;

    [Fact]
    public void A_new_follower_follows_and_a_growing_reply_keeps_the_end_in_view()
    {
        var f = new TailFollower();
        Assert.True(f.Following);
        // The reply grew by 60px while the reader sat at the bottom: scroll to the end.
        Assert.True(f.OnScroll(offset: 500, extent: 1060, viewport: View, offsetDelta: 0, extentDelta: 60));
        Assert.True(f.Following);
        Assert.False(f.NewBelow);
        // The window's own scroll to the end then reports a plain move to the bottom: still following, nothing asked.
        Assert.False(f.OnScroll(offset: 560, extent: 1060, viewport: View, offsetDelta: 60, extentDelta: 0));
        Assert.True(f.Following);
    }

    [Fact]
    public void Scrolling_up_stops_following_and_growth_then_only_flags_new_content_below()
    {
        var f = new TailFollower();
        Assert.False(f.OnScroll(offset: 300, extent: 1000, viewport: View, offsetDelta: -200, extentDelta: 0));
        Assert.False(f.Following);
        Assert.True(f.AwayFromBottom);

        // The reply keeps growing: no scrolling is requested, but the window can offer "Jump to latest".
        Assert.False(f.OnScroll(offset: 300, extent: 1080, viewport: View, offsetDelta: 0, extentDelta: 80));
        Assert.False(f.Following);
        Assert.True(f.NewBelow);
        Assert.True(f.AwayFromBottom);
    }

    [Fact]
    public void Scrolling_up_while_the_extent_estimate_shifts_is_still_the_readers_choice()
    {
        // A virtualized list re-estimates its height as the reader moves; that must not snap them back to the end.
        var f = new TailFollower();
        Assert.False(f.OnScroll(offset: 700, extent: 1300, viewport: View, offsetDelta: -150, extentDelta: 120));
        Assert.False(f.Following);
    }

    [Fact]
    public void Scrolling_back_to_the_bottom_resumes_following_and_clears_the_flag()
    {
        var f = new TailFollower();
        f.OnScroll(300, 1000, View, -200, 0);
        f.OnScroll(300, 1100, View, 0, 100);
        Assert.True(f.NewBelow);
        Assert.False(f.OnScroll(offset: 600, extent: 1100, viewport: View, offsetDelta: 300, extentDelta: 0));
        Assert.True(f.Following);
        Assert.False(f.NewBelow);
        Assert.False(f.AwayFromBottom);
    }

    [Fact]
    public void A_little_slack_at_the_bottom_does_not_lose_the_tail()
    {
        var f = new TailFollower();
        Assert.False(f.OnScroll(offset: 480, extent: 1000, viewport: View, offsetDelta: -20, extentDelta: 0));   // 20px short of the end
        Assert.True(f.Following);
        f.OnScroll(offset: 400, extent: 1000, viewport: View, offsetDelta: -80, extentDelta: 0);                 // 100px short
        Assert.False(f.Following);
    }

    [Fact]
    public void Jump_to_latest_and_opening_a_chat_start_following_again()
    {
        var f = new TailFollower();
        f.OnScroll(100, 1000, View, -400, 0);
        f.OnScroll(100, 1100, View, 0, 100);
        f.JumpToLatest();
        Assert.True(f.Following);
        Assert.False(f.NewBelow);
        Assert.False(f.AwayFromBottom);

        f.OnScroll(100, 1100, View, -400, 0);
        f.Reset();
        Assert.True(f.Following);
        Assert.False(f.AwayFromBottom);
    }

    [Fact]
    public void Tiny_layout_noise_is_not_a_scroll_or_a_growth()
    {
        var f = new TailFollower();
        Assert.False(f.OnScroll(offset: 500, extent: 1000.4, viewport: View, offsetDelta: -0.5, extentDelta: 0.4));
        Assert.True(f.Following);
    }

    [Fact]
    public void Shrinking_content_at_the_bottom_asks_for_nothing_extra_but_keeps_following()
    {
        var f = new TailFollower();
        Assert.False(f.OnScroll(offset: 500, extent: 1000, viewport: View, offsetDelta: 0, extentDelta: -80));
        Assert.True(f.Following);
    }
}
