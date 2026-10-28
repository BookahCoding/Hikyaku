using System.Xml.Linq;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 19a — toast XML from untrusted text, the app-id validator and the throttle (no OS involved).</summary>
public class Checkpoint19ToastSupportTests
{
    [Fact]
    public void Markup_in_text_is_escaped_and_the_result_is_well_formed_xml()
    {
        var xml = ToastXml.Build("<b>Alert</b> & \"co\"", "x </text><text>injected</text> <script>", "app://open?x=1&y=\"2\"");
        var doc = XDocument.Parse(xml);                                   // would throw if the markup escaped its element
        var texts = doc.Descendants("text").Select(t => t.Value).ToList();
        Assert.Equal(2, texts.Count);                                     // no injected extra <text>
        Assert.Contains("<b>Alert</b>", texts[0]);
        Assert.Contains("</text><text>injected", texts[1]);
        Assert.Equal("app://open?x=1&y=\"2\"", doc.Root!.Attribute("launch")!.Value);
        Assert.Single(doc.Descendants("binding"));
    }

    [Fact]
    public void Control_characters_newlines_and_runs_of_spaces_become_single_spaces()
    {
        Assert.Equal("a b c", ToastXml.Clean("a\r\n\tb\u0007   c\u001b", 100));
        Assert.Equal("", ToastXml.Clean(" \n\t ", 10));
        Assert.Equal("", ToastXml.Clean(null, 10));
    }

    [Fact]
    public void Long_text_is_truncated_with_an_ellipsis()
    {
        var t = ToastXml.Clean(new string('x', 1000), ToastXml.MaxBody);
        Assert.Equal(ToastXml.MaxBody, t.Length);
        Assert.EndsWith("…", t);
        var doc = XDocument.Parse(ToastXml.Build(new string('t', 500), new string('b', 500), new string('l', 500)));
        Assert.True(doc.Descendants("text").First().Value.Length <= ToastXml.MaxTitle);
        Assert.True(doc.Root!.Attribute("launch")!.Value.Length <= ToastXml.MaxLaunch);
    }

    [Fact]
    public void An_empty_title_gets_the_app_name_and_an_empty_body_adds_no_second_line()
    {
        var doc = XDocument.Parse(ToastXml.Build("  ", "", null));
        Assert.Equal("Hikyaku", Assert.Single(doc.Descendants("text")).Value);
        Assert.Null(doc.Root!.Attribute("launch"));
    }

    [Theory]
    [InlineData("Hikyaku.Desktop", true)]
    [InlineData("Hikyaku.Desktop.Acceptance-1", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("..\\Run", false)]
    [InlineData("Hikyaku/Desktop", false)]
    [InlineData("Hikyaku Desktop", false)]
    [InlineData("-leading", false)]
    [InlineData("HKEY_LOCAL_MACHINE\\x", false)]
    public void App_ids_are_short_dotted_tokens_that_cannot_address_another_registry_path(string? id, bool valid)
    {
        Assert.Equal(valid, ToastAppId.IsValid(id));
        if (valid) Assert.Equal(id, ToastAppId.Require(id));
        else Assert.Throws<ArgumentException>(() => ToastAppId.Require(id));
    }

    [Fact]
    public void The_throttle_allows_one_per_window_per_key_then_again_after_the_window()
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        var t = new ToastThrottle(TimeSpan.FromSeconds(10), clock: () => now);
        Assert.True(t.TryAcquire("needs-you"));
        Assert.False(t.TryAcquire("needs-you"));
        Assert.True(t.TryAcquire("inbox"));                               // another kind is independent
        now = now.AddSeconds(9);
        Assert.False(t.TryAcquire("needs-you"));
        now = now.AddSeconds(2);
        Assert.True(t.TryAcquire("needs-you"));
    }

    [Fact]
    public void The_throttle_can_allow_a_small_burst()
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        var t = new ToastThrottle(TimeSpan.FromMinutes(1), maxPerWindow: 2, clock: () => now);
        Assert.True(t.TryAcquire("k"));
        Assert.True(t.TryAcquire("k"));
        Assert.False(t.TryAcquire("k"));
    }
}
