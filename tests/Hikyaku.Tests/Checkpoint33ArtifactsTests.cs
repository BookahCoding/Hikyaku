using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 33: the artifacts gallery's index — what a chat's stored rows say it produced. Fixtures only.</summary>
public class Checkpoint33ArtifactsTests
{
    private static readonly ProfileKey P = new("gw", "default");
    private const string Fence = "```";

    private static SessionMessage Asst(string id, string text, IReadOnlyList<StoredToolCall>? calls = null) =>
        new(id, "assistant", text, ToolCalls: calls);

    private static SessionMessage Tool(string id, string callId, string content) =>
        new(id, "tool", content, ToolCallId: callId);

    // ---- extraction ----

    [Fact]
    public void A_media_token_a_markdown_image_a_preview_directive_and_a_link_are_found_in_text_order()
    {
        var found = ReplyMediaExtractor.Extract(
            "Here: ![chart](/opt/data/out/chart.png)\nMEDIA:/opt/data/out/report.pdf.\n::preview{file=\"/opt/data/out/page.html\"}\nsee [docs](https://example.com/a)");
        Assert.Equal(["chart.png", "report.pdf", "page.html", "docs"], found.Select(m => m.Name));
        Assert.Equal([ArtifactKind.Image, ArtifactKind.File, ArtifactKind.File, ArtifactKind.Link], found.Select(m => m.Kind));
        Assert.Equal("/opt/data/out/report.pdf", found[1].Source);                 // the sentence's full stop is not part of the path
    }

    [Fact]
    public void Paths_in_code_are_samples_not_artifacts()
    {
        var text = "Use `MEDIA:/a/x.png` in a reply.\n" + Fence + "\n![x](/b/y.png)\nMEDIA:/c/z.png\n" + Fence + "\nreal: MEDIA:/d/w.png\n" + Fence + "\nstill code ![x](/e/q.png)";
        var found = ReplyMediaExtractor.Extract(text);
        Assert.Equal(["w.png"], found.Select(m => m.Name));                          // an unclosed fence runs to the end
    }

    [Fact]
    public void Web_images_data_images_and_odd_text_are_handled()
    {
        var found = ReplyMediaExtractor.Extract("![cat](https://example.com/cat.jpg) ![inline](data:image/png;base64,AAAA) [a](ftp://x/y) not MULTIMEDIA:/x/y.png");
        Assert.Equal([ArtifactKind.Image, ArtifactKind.Image], found.Select(m => m.Kind));
        Assert.Equal("inline", found[1].Name);
        Assert.Empty(ReplyMediaExtractor.Extract(null));
        Assert.Empty(ReplyMediaExtractor.Extract("plain words with no media"));
    }

    // ---- generated pictures ----

    [Fact]
    public void A_generated_picture_uses_the_host_path_and_a_failure_makes_none()
    {
        var ok = GeneratedImage.From("""{"success":true,"host_image":"/opt/data/img/1.png","image":"/sandbox/1.png","agent_visible_image":"/workspace/1.png"}""")!;
        Assert.Equal("/opt/data/img/1.png", ok.Source);
        Assert.Equal(["/opt/data/img/1.png", "/sandbox/1.png", "/workspace/1.png"], ok.Echoes);
        Assert.Equal("/x.png", GeneratedImage.From("""{"image":"/x.png"}""")!.Source);
        Assert.Null(GeneratedImage.From("""{"success":false,"image":"/x.png"}"""));
        Assert.Null(GeneratedImage.From("""{"note":"none"}"""));
        Assert.Null(GeneratedImage.From("plain text"));
        Assert.Null(GeneratedImage.From(null));
    }

    // ---- indexing a chat ----

    [Fact]
    public void A_chat_lists_its_artifacts_newest_first_with_each_source_once_and_the_chat_to_open()
    {
        var rows = new[]
        {
            Asst("1", "first MEDIA:/o/a.txt"),
            Asst("2", "again MEDIA:/o/a.txt and ![p](/o/p.png)"),
            Asst("3", "later [site](https://example.com)"),
        };
        var items = ArtifactIndex.FromRows(rows, "s1", "Report chat", P);
        Assert.Equal(["site", "a.txt", "p.png"], items.Select(i => i.Name));          // newest first; a.txt once (row 2, its newest mention)
        Assert.Single(items, i => i.Source == "/o/a.txt");
        Assert.Equal(1, items.Single(i => i.Source == "/o/a.txt").Order);   // the second row (0-based)
        Assert.All(items, i => Assert.Equal(("s1", "Report chat", P), (i.SessionId, i.SessionTitle, i.Profile)));
    }

    [Fact]
    public void A_generated_picture_shows_once_and_the_sandbox_path_the_model_repeats_is_dropped()
    {
        var rows = new[]
        {
            Asst("1", "", [new StoredToolCall("c1", "image_generate", "a cat")]),
            Tool("2", "c1", """{"host_image":"/opt/data/img/cat.png","agent_visible_image":"/workspace/cat.png"}"""),
            Asst("3", "Done: ![cat](/workspace/cat.png) and MEDIA:/opt/data/img/cat.png"),
        };
        var items = ArtifactIndex.FromRows(rows, "s1", null, P);
        var cat = Assert.Single(items);
        Assert.Equal("/opt/data/img/cat.png", cat.Source);
        Assert.True(cat.Generated);
        Assert.Equal(ArtifactKind.Image, cat.Kind);
    }

    [Fact]
    public void Hidden_rows_user_prompts_and_failed_tools_add_nothing()
    {
        var rows = new SessionMessage[]
        {
            new("1", "user", "MEDIA:/o/mine.png"),
            new("2", "assistant", "MEDIA:/o/secret.png", DisplayKind: "hidden"),
            Asst("3", "", [new StoredToolCall("c", "image_generate", "x")]),
            Tool("4", "c", """{"success":false,"error":"quota"}"""),
        };
        Assert.Empty(ArtifactIndex.FromRows(rows, "s", null, P));
    }

    [Fact]
    public void A_chat_is_capped()
    {
        var text = string.Join(" ", Enumerable.Range(0, ArtifactIndex.MaxPerChat + 50).Select(i => $"MEDIA:/o/f{i}.txt"));
        Assert.Equal(ArtifactIndex.MaxPerChat, ArtifactIndex.FromRows([Asst("1", text)], "s", null, P).Count);
    }

    // ---- what can be saved ----

    [Theory]
    [InlineData("/opt/data/a.png", true, false, false)]
    [InlineData("file:///opt/data/a.png", true, false, false)]
    [InlineData("~/a.png", true, false, false)]
    [InlineData("data:image/png;base64,AAAA", false, true, false)]
    [InlineData("https://example.com/a.png", false, false, true)]
    [InlineData("relative/a.png", false, false, false)]            // relative to a directory this app does not know: not fetched
    public void Only_gateway_paths_and_inline_pictures_can_be_saved(string source, bool onGateway, bool inline, bool web)
    {
        var item = new ArtifactItem(ArtifactKind.File, "a", source, "s", null, P, false, 0);
        Assert.Equal((onGateway, inline, web), (item.OnGateway, item.IsInlineData, item.IsWeb));
        Assert.Equal(onGateway || inline, item.CanSave);
        if (source.StartsWith("file://")) Assert.Equal("/opt/data/a.png", item.GatewayPath);
    }

    [Fact]
    public void Inline_pictures_decode_only_when_valid_and_small_and_names_are_safe()
    {
        Assert.Equal([1, 2, 3], ArtifactData.DecodeDataUrl("data:image/png;base64,AQID"));
        Assert.Null(ArtifactData.DecodeDataUrl("data:image/png;base64,!!!"));
        Assert.Null(ArtifactData.DecodeDataUrl("data:image/png,not-base64"));
        Assert.Null(ArtifactData.DecodeDataUrl("https://x/y.png"));
        Assert.Null(ArtifactData.DecodeDataUrl("data:image/png;base64," + new string('A', ArtifactData.MaxInlineBytes * 2)));

        var evil = new ArtifactItem(ArtifactKind.File, "x", "/o/..\\..\\evil:name?.txt", "s", null, P, false, 0);
        var name = ArtifactData.SafeName(evil);
        Assert.DoesNotContain("\\", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("..", name.Replace("evil", ""));
        Assert.EndsWith(".png", ArtifactData.SafeName(new ArtifactItem(ArtifactKind.Image, "image", "data:image/png;base64,AA==", "s", null, P, false, 0)));
    }

    // ---- scanning recent chats ----

    [Fact]
    public async Task A_scan_reads_a_bounded_number_of_chats_and_one_failure_does_not_stop_the_rest()
    {
        var chats = Enumerable.Range(0, ArtifactScanner.MaxChats + 5).Select(i => new SessionSummary("s" + i, "Chat " + i)).ToList();
        var asked = new List<string>();
        var result = await ArtifactScanner.ScanAsync(chats, P, (id, _) =>
        {
            asked.Add(id);
            if (id == "s1") throw new HttpRequestException("down");
            return Task.FromResult(new SessionMessagesPage(id, [Asst("1", $"MEDIA:/o/{id}.txt")]));
        });
        Assert.Equal(ArtifactScanner.MaxChats, asked.Count);
        Assert.Equal((ArtifactScanner.MaxChats - 1, 1, true), (result.ChatsRead, result.ChatsFailed, result.Truncated));
        Assert.Equal(ArtifactScanner.MaxChats - 1, result.Items.Count);
        Assert.DoesNotContain(result.Items, i => i.SessionId == "s1");
    }

    [Fact]
    public async Task A_scan_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArtifactScanner.ScanAsync([new SessionSummary("s")], P, (_, _) => Task.FromResult(new SessionMessagesPage("s", [])), cts.Token));
    }
}
