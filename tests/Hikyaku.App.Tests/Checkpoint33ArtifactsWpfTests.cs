using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 33 — the Artifacts page through the real MainViewModel with a fake gateway: bounded scan, filters, explicit save / preview, profile gate.</summary>
[Collection("StaMath")]
public class Checkpoint33ArtifactsWpfTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public readonly List<string> Downloads = new();
        public Func<string, HttpResponseMessage>? Download;
        public int ListedChats = 3;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            var url = r.RequestUri!.PathAndQuery;
            Requests.Add(url);
            if (path.EndsWith("/api/fs/download", StringComparison.Ordinal))
            {
                Downloads.Add(url);
                return Task.FromResult(Download?.Invoke(url) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            }
            if (path.EndsWith("/messages", StringComparison.Ordinal))
            {
                var id = path.Split('/')[^2];
                var rows = id == "s1"
                    ? """[{"id":1,"role":"assistant","content":"Report: MEDIA:/opt/data/out/report.pdf and ![chart](/opt/data/out/chart.png)"}]"""
                    : id == "s2"
                        ? """[{"id":1,"role":"assistant","content":"see [docs](https://example.com/a) and ![inline](data:image/png;base64,AQID)"}]"""
                        : """[{"id":1,"role":"assistant","content":"nothing here"}]""";
                return Task.FromResult(Json("{\"session_id\":\"" + id + "\",\"messages\":" + rows + "}"));
            }
            var sessions = string.Join(",", Enumerable.Range(1, ListedChats).Select(i => $"{{\"id\":\"s{i}\",\"title\":\"Chat {i}\",\"last_active\":{100 - i}}}"));
            return Task.FromResult(Json("{\"sessions\":[" + sessions + "],\"total\":" + ListedChats + ",\"limit\":50,\"offset\":0}"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Rig
    {
        public MainViewModel Vm = null!;
        public Gateway Gw = new();
        public bool Cancel;
        public List<string> SuggestedNames = [];
        public List<(string Path, byte[] Bytes)> Written = [];
        public List<string> Copied = [];

        public Rig(params string[] profiles)
        {
            profiles = profiles.Length == 0 ? ["default"] : profiles;
            var selection = new ProfileSelectionService(new MemPrefs33());
            selection.SetSelectedAsync(profiles.Select(p => new ProfileKey("gw", p))).GetAwaiter().GetResult();
            var gate = new NotificationGate(selection);
            var auth = new DashboardAuth(new Uri("https://gw.example/"), Gw);
            var catalog = new SessionCatalog(gate, () => new SessionsApi(auth));
            catalog.SetActiveProfile(new ProfileKey("gw", "default"));
            Vm = new MainViewModel(selection, gate, () => new DemoTransport(), () => "t",
                () => null, () => null, () => catalog, () => null, () => null, () => null, () => new MediaApi(auth), () => null);
            Vm.PickArtifactSavePath = name => { SuggestedNames.Add(name); return Cancel ? null : @"C:\out\" + name; };
            Vm.WriteArtifactFile = (path, bytes) => { Written.Add((path, bytes)); return Task.CompletedTask; };
            Vm.CopyArtifactText = t => Copied.Add(t);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void A_scan_lists_pictures_files_and_links_from_recent_chats_and_reads_nothing_else()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            Assert.Equal(["chart.png", "docs", "inline", "report.pdf"], rig.Vm.ArtifactRows.Select(r => r.Name).Order().ToList());
            Assert.Equal(4, rig.Vm.ArtifactRows.Count);
            Assert.Contains("4 item(s) from 3 recent chat(s)", rig.Vm.ArtifactStatus);
            Assert.Empty(rig.Gw.Downloads);                                           // a scan downloads no file
            Assert.Contains("Chat 1", rig.Vm.ArtifactRows.First(r => r.Name == "report.pdf").Detail);
        });
    }

    [Fact]
    public void The_filter_and_search_narrow_the_list()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            rig.Vm.SetArtifactFilter(ArtifactFilter.Images);
            Assert.All(rig.Vm.ArtifactRows, r => Assert.Equal(ArtifactKind.Image, r.Item.Kind));
            Assert.Equal(2, rig.Vm.ArtifactRows.Count);
            rig.Vm.SetArtifactFilter(ArtifactFilter.Links);
            Assert.Equal(["docs"], rig.Vm.ArtifactRows.Select(r => r.Name));
            rig.Vm.SetArtifactFilter(ArtifactFilter.All);
            rig.Vm.ArtifactSearch = "REPORT";
            Assert.Equal(["report.pdf"], rig.Vm.ArtifactRows.Select(r => r.Name));
            rig.Vm.ArtifactSearch = "chat 2";                                         // by the chat's title
            Assert.Equal(2, rig.Vm.ArtifactRows.Count);
        });
    }

    [Fact]
    public void Save_asks_for_a_path_first_then_fetches_with_the_items_own_profile_and_writes_the_bytes()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            var row = rig.Vm.ArtifactRows.First(r => r.Name == "report.pdf");
            rig.Cancel = true;
            rig.Vm.SaveArtifactAsync(row).GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Downloads);                                           // cancelled: nothing fetched, nothing written
            Assert.Empty(rig.Written);

            rig.Cancel = false;
            rig.Vm.SaveArtifactAsync(row).GetAwaiter().GetResult();
            var url = Assert.Single(rig.Gw.Downloads);
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + url).Query);
            Assert.Equal(("/opt/data/out/report.pdf", "default"), (q["path"], q["profile"]));
            var written = Assert.Single(rig.Written);
            Assert.Equal(new byte[] { 1, 2, 3 }, written.Bytes);
            Assert.Equal(@"C:\out\report.pdf", written.Path);
            Assert.Equal("report.pdf", rig.SuggestedNames.Last());
            Assert.Equal("Saved report.pdf.", rig.Vm.ArtifactStatus);
        });
    }

    [Fact]
    public void An_embedded_picture_saves_without_any_request_and_a_failed_download_says_so_and_writes_nothing()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            rig.Vm.SaveArtifactAsync(rig.Vm.ArtifactRows.First(r => r.Name == "inline")).GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Downloads);
            Assert.Equal([1, 2, 3], Assert.Single(rig.Written).Bytes);
            Assert.EndsWith(".png", rig.SuggestedNames.Last());

            rig.Written.Clear();
            rig.Gw.Download = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
            rig.Vm.SaveArtifactAsync(rig.Vm.ArtifactRows.First(r => r.Name == "chart.png")).GetAwaiter().GetResult();
            Assert.Empty(rig.Written);
            Assert.Contains("Couldn't get the file", rig.Vm.ArtifactStatus);
        });
    }

    [Fact]
    public void Nothing_is_saved_without_a_save_handler_and_a_link_cannot_be_saved_only_copied()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            var link = rig.Vm.ArtifactRows.First(r => r.Name == "docs");
            Assert.False(link.SaveCommand.CanExecute(null));
            Assert.False(link.PreviewCommand.CanExecute(null));
            Assert.True(link.CopyCommand.CanExecute(null));
            rig.Vm.CopyArtifactLink(link);
            Assert.Equal(["https://example.com/a"], rig.Copied);

            rig.Vm.PickArtifactSavePath = null;
            rig.Vm.SaveArtifactAsync(rig.Vm.ArtifactRows.First(r => r.Name == "report.pdf")).GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Downloads);
        });
    }

    [Fact]
    public void Preview_fetches_on_the_click_and_a_file_that_is_not_a_picture_is_refused_politely()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            var chart = rig.Vm.ArtifactRows.First(r => r.Name == "chart.png");
            Assert.Empty(rig.Gw.Downloads);
            rig.Vm.PreviewArtifactAsync(chart).GetAwaiter().GetResult();                // bytes [1,2,3] are not an image
            Assert.Single(rig.Gw.Downloads);
            Assert.Null(rig.Vm.ArtifactPreviewImage);
            Assert.Contains("can't be shown as a picture", rig.Vm.ArtifactStatus);
        });
    }

    [Fact]
    public void A_scan_without_a_chosen_profile_or_connection_says_so_and_a_second_scan_replaces_the_first()
    {
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            Assert.Equal(4, rig.Vm.ArtifactRows.Count);
            rig.Gw.ListedChats = 1;
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            Assert.Equal(2, rig.Vm.ArtifactRows.Count);                                 // only chat 1 now: not appended to the old list

            var selection = new ProfileSelectionService(new MemPrefs33());
            selection.SetSelectedAsync([new ProfileKey("gw", "work")]).GetAwaiter().GetResult();
            var vm = new MainViewModel(selection, new NotificationGate(selection), () => new DemoTransport(), () => "t",
                () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
            vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            Assert.Contains("Connect to a gateway", vm.ArtifactStatus);
            Assert.Empty(vm.ArtifactRows);
        });
    }

    [Fact]
    public void A_profile_that_is_not_chosen_here_is_never_scanned_or_fetched()
    {
        RunSta(() =>
        {
            var rig = new Rig("work");                                                   // the sidebar's profile ("default") is not chosen
            rig.Vm.ScanArtifactsAsync().GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Requests);
            Assert.Empty(rig.Vm.ArtifactRows);

            var item = new ArtifactItem(ArtifactKind.File, "x.txt", "/o/x.txt", "s", null, new ProfileKey("gw", "default"), false, 0);
            rig.Vm.SaveArtifactAsync(new ArtifactRowVM(item, rig.Vm)).GetAwaiter().GetResult();
            Assert.Empty(rig.Gw.Downloads);
            Assert.Empty(rig.Written);
        });
    }

    [Fact]
    public void The_page_is_in_both_views_since_stage_41()
    {
        // Stage 33 hid it in Simple like Desktop; the maintainer asked for it in Simple too (it only reads from the gateway).
        RunSta(() =>
        {
            var rig = new Rig();
            rig.Vm.ApplyExperience(true);
            Assert.Equal(Visibility.Visible, rig.Vm.ArtifactsNavVisibility);
            rig.Vm.ShowArtifactsCommand!.Execute(null);
            Assert.Equal(MainNav.Artifacts, rig.Vm.Nav);
            rig.Vm.ApplyExperience(false);
            Assert.Equal(Visibility.Visible, rig.Vm.ArtifactsNavVisibility);
            Assert.Equal(MainNav.Artifacts, rig.Vm.Nav);                                 // switching view keeps the page
        });
    }
}

file sealed class MemPrefs33 : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}
