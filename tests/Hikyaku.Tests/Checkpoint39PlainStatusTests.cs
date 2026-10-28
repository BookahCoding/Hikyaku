using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 39: plain-language status for Simple view and the text-size choices.</summary>
public class Checkpoint39PlainStatusTests
{
    [Fact]
    public void Every_connection_state_has_a_plain_sentence_and_connected_says_nothing()
    {
        Assert.All(Enum.GetValues<ConnectionKind>(), k =>
        {
            var line = PlainStatus.For(k);
            if (k == ConnectionKind.Connected) Assert.Equal("", line.Text);
            else Assert.NotEqual("", line.Text);
        });
    }

    [Fact]
    public void The_plain_lines_never_contain_developer_words_addresses_or_counters()
    {
        string[] jargon = ["protocol", "json", "rpc", "ws", "dpapi", "bookah", "http", "attempt", "pending", "url", "token", "herald", "api/", "/"];
        foreach (var k in Enum.GetValues<ConnectionKind>())
        {
            var text = PlainStatus.For(k).Text.ToLowerInvariant();
            var words = System.Text.RegularExpressions.Regex.Split(text, @"[^a-z/]+");
            foreach (var j in jargon) Assert.DoesNotContain(j, words);
            Assert.DoesNotContain("/", text);
        }
        Assert.DoesNotContain("Hermes JSON", PlainStatus.OfflineCopyNote);
    }

    [Fact]
    public void Only_signed_out_states_offer_to_sign_in_again()
    {
        Assert.True(PlainStatus.For(ConnectionKind.SessionExpired).OfferSignIn);
        Assert.True(PlainStatus.For(ConnectionKind.NeedsSignIn).OfferSignIn);
        Assert.All(Enum.GetValues<ConnectionKind>().Where(k => k is not (ConnectionKind.SessionExpired or ConnectionKind.NeedsSignIn)),
            k => Assert.False(PlainStatus.For(k).OfferSignIn, k.ToString()));
    }

    [Fact]
    public void A_failure_tells_the_person_what_to_do_and_not_the_technical_reason()
    {
        var failed = PlainStatus.For(ConnectionKind.Failed).Text;
        Assert.Contains("internet connection", failed);
        Assert.Contains("try again", failed);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(0, 100)]
    [InlineData(107, 100)]
    [InlineData(108, 115)]
    [InlineData(115, 115)]
    [InlineData(122, 115)]
    [InlineData(123, 130)]
    [InlineData(500, 130)]
    [InlineData(-50, 100)]
    public void Any_text_size_becomes_the_nearest_of_the_three_choices(int input, int expected) =>
        Assert.Equal(expected, TextScale.Normalize(input));

    [Fact]
    public void The_text_size_has_a_factor_and_a_label()
    {
        Assert.Equal(1.15, TextScale.Factor(115));
        Assert.Equal(1.0, TextScale.Factor(9999 * -1));
        Assert.Equal(["Normal", "Larger", "Largest"], TextScale.Choices.Select(TextScale.Label));
    }
}
