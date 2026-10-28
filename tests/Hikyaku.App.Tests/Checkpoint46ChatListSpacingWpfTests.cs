using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Recent-chat rows do not keep a blank line for a preview or a pin mark that is not there.</summary>
[Collection("StaMath")]
public class Checkpoint46ChatListSpacingWpfTests
{
    [Fact]
    public void Empty_preview_and_unpinned_rows_take_no_extra_line()
    {
        RunSta(() =>
        {
            var plain = new SessionRowVM(new SessionSummary("s1", "Notes"));
            Assert.Equal("", plain.Preview);
            Assert.Equal("", plain.PinMark);
            Assert.Equal(Visibility.Collapsed, plain.PreviewVisibility);
            Assert.Equal(Visibility.Collapsed, plain.PinVisibility);

            plain.IsPinned = true;
            Assert.Equal("Pinned", plain.PinMark);
            Assert.Equal(Visibility.Visible, plain.PinVisibility);

            var preview = new SessionRowVM(new SessionSummary("s2", "Notes", Preview: "the last line"));
            Assert.Equal(Visibility.Visible, preview.PreviewVisibility);
            Assert.Equal(Visibility.Collapsed, preview.PinVisibility);

            var snippet = new SessionRowVM(new SessionSummary("s3", "Notes", Preview: "older", Snippet: "matched words"));
            Assert.Equal("matched words", snippet.Preview);
            Assert.Equal(Visibility.Visible, snippet.PreviewVisibility);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }
}
