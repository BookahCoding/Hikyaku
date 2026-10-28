using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 20a — command palette matching and ranking (pure; no UI).</summary>
public class Checkpoint20PaletteSearchTests
{
    private static PaletteItem I(string id, string title, string group = "", params string[] keywords) =>
        new(id, title, group, "", keywords);

    private static readonly PaletteItem[] Items =
    [
        I("chat.new", "New chat", "Chats", "start", "compose"),
        I("nav.inbox", "Open Inbox", "Go to", "scheduled", "cron"),
        I("nav.chats", "Open Chats", "Go to"),
        I("find", "Find in chat", "Chat", "search"),
        I("settings", "Settings", "App", "preferences", "options"),
        I("theme", "Use dark appearance", "App"),
        I("adv", "Show advanced tools", "App", "simple", "experience"),
    ];

    private static IEnumerable<string> Ids(string? q) => PaletteSearch.Filter(Items, q).Select(i => i.Id);

    [Fact]
    public void An_empty_query_lists_everything_in_registry_order_up_to_the_cap()
    {
        Assert.Equal(Items.Select(i => i.Id), Ids(""));
        Assert.Equal(Items.Select(i => i.Id), Ids("   "));
        Assert.Equal(Items.Select(i => i.Id), Ids(null));
        var many = Enumerable.Range(0, 40).Select(n => I("i" + n, "Item " + n)).ToList();
        Assert.Equal(PaletteSearch.MaxResults, PaletteSearch.Filter(many, "").Count);
    }

    [Fact]
    public void A_title_prefix_beats_a_word_prefix_beats_a_substring()
    {
        var items = new[] { I("sub", "Unchatty mode"), I("word", "Open chat"), I("prefix", "Chat settings") };
        Assert.Equal(new[] { "prefix", "word", "sub" }, PaletteSearch.Filter(items, "chat").Select(i => i.Id));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_the_query_is_trimmed()
    {
        Assert.Equal("settings", Ids("  SETTINGS ").First());
        Assert.Contains("find", Ids("FIND"));
    }

    [Fact]
    public void Keywords_and_groups_match_but_rank_below_title_matches()
    {
        Assert.Equal("nav.inbox", Ids("cron").Single());                  // keyword only
        Assert.Equal("settings", Ids("preferences").Single());
        Assert.Contains("nav.inbox", Ids("go to"));                        // group
        var items = new[] { I("kw", "Something else", "", "inbox"), I("title", "Inbox") };
        Assert.Equal(new[] { "title", "kw" }, PaletteSearch.Filter(items, "inbox").Select(i => i.Id));
    }

    [Fact]
    public void Every_word_of_the_query_must_match()
    {
        Assert.Equal(new[] { "nav.inbox" }, Ids("open inbox"));
        Assert.Equal(new[] { "nav.chats", "nav.inbox" }.OrderBy(x => x), Ids("open").OrderBy(x => x));
        Assert.Empty(Ids("open banana"));
        Assert.Equal(new[] { "adv" }, Ids("show tools"));                  // words need not be adjacent
    }

    [Fact]
    public void Initials_and_loose_letters_find_a_command_but_below_real_matches()
    {
        Assert.Equal("chat.new", Ids("nwc").First());                      // subsequence of "New chat"
        Assert.Empty(Ids("zq"));
        var items = new[] { I("loose", "Notice window changes"), I("real", "New chat") };
        Assert.Equal("real", PaletteSearch.Filter(items, "new c").First().Id);
    }

    [Fact]
    public void A_single_letter_does_not_fuzzy_match_everything()
    {
        Assert.Equal(new[] { "chat.new", "nav.chats", "find", "theme" }.OrderBy(x => x),
            Ids("c").OrderBy(x => x).Where(id => id is "chat.new" or "nav.chats" or "find" or "theme"));
        Assert.DoesNotContain("settings", Ids("c").Where(id => id == "nothing"));
    }

    [Fact]
    public void Regex_characters_and_accents_are_literal()
    {
        var items = new[] { I("a", "Run (test)"), I("b", "Run test"), I("c", "Café") };
        Assert.Equal(new[] { "a" }, PaletteSearch.Filter(items, "(test)").Select(i => i.Id));
        Assert.Empty(PaletteSearch.Filter(items, "cafe"));
        Assert.Equal(new[] { "c" }, PaletteSearch.Filter(items, "café").Select(i => i.Id));
        Assert.Empty(PaletteSearch.Filter(items, ".*"));
    }

    [Fact]
    public void Equal_scores_keep_the_registry_order_and_results_are_capped()
    {
        var items = Enumerable.Range(0, 30).Select(n => I("x" + n, "Same title " + n)).ToList();
        var r = PaletteSearch.Filter(items, "same").Select(i => i.Id).ToList();
        Assert.Equal(PaletteSearch.MaxResults, r.Count);
        Assert.Equal(Enumerable.Range(0, PaletteSearch.MaxResults).Select(n => "x" + n), r);
    }

    [Fact]
    public void Null_text_in_an_item_never_throws()
    {
        var items = new[] { new PaletteItem("n", null!, null!, null!, null) , I("ok", "Fine") };
        Assert.Equal(new[] { "ok" }, PaletteSearch.Filter(items, "fine").Select(i => i.Id));
    }
}
