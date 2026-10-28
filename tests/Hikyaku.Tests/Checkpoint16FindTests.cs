using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 16c — the transcript matcher behind Find in chat (pure; no UI).</summary>
public class Checkpoint16FindTests
{
    private static readonly string[] Texts = ["Hello World", "nothing here", "say HELLO again", "", "héllo accent"];

    [Fact]
    public void Matches_are_case_insensitive_message_indexes_in_order()
    {
        Assert.Equal(new[] { 0, 2 }, TranscriptFinder.Find(Texts, "hello"));
        Assert.Equal(new[] { 0 }, TranscriptFinder.Find(Texts, "WORLD"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_queries_match_nothing(string? q) => Assert.Empty(TranscriptFinder.Find(Texts, q));

    [Fact]
    public void The_query_is_trimmed_but_inner_spaces_and_regex_characters_are_literal()
    {
        Assert.Equal(new[] { 0 }, TranscriptFinder.Find(Texts, "  Hello World  "));
        Assert.Empty(TranscriptFinder.Find(["a.b", "axb"], "a.b").Skip(1));        // '.' is not a wildcard
        Assert.Equal(new[] { 0 }, TranscriptFinder.Find(["a.b", "axb"], "a.b"));
        Assert.Equal(new[] { 0 }, TranscriptFinder.Find(["(x)+", "xx"], "(x)+"));
    }

    [Fact]
    public void Accents_are_not_folded_so_a_match_is_what_the_user_typed()
    {
        Assert.Equal(new[] { 4 }, TranscriptFinder.Find(Texts, "héllo"));
        Assert.DoesNotContain(4, TranscriptFinder.Find(Texts, "hello"));
    }

    [Fact]
    public void Match_count_is_capped()
    {
        var many = Enumerable.Repeat("x", TranscriptFinder.MaxMatches + 50).ToArray();
        Assert.Equal(TranscriptFinder.MaxMatches, TranscriptFinder.Find(many, "x").Count);
    }

    [Fact]
    public void Null_texts_are_skipped_not_thrown_on() =>
        Assert.Equal(new[] { 1 }, TranscriptFinder.Find([null!, "x"], "x"));

    [Fact]
    public void Next_and_previous_wrap()
    {
        Assert.Equal(1, TranscriptFinder.Next(0, 3));
        Assert.Equal(0, TranscriptFinder.Next(2, 3));       // wraps forward
        Assert.Equal(2, TranscriptFinder.Previous(0, 3));   // wraps back
        Assert.Equal(1, TranscriptFinder.Previous(2, 3));
        Assert.Equal(0, TranscriptFinder.Next(0, 1));
        Assert.Equal(0, TranscriptFinder.Previous(0, 1));
    }

    [Fact]
    public void Navigation_over_no_matches_is_minus_one()
    {
        Assert.Equal(-1, TranscriptFinder.Next(-1, 0));
        Assert.Equal(-1, TranscriptFinder.Previous(-1, 0));
    }
}
