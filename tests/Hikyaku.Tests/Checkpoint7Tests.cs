using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Checkpoint 7: Hokusai themes, config approvals, YOLO session scope — fixtures only, no live settings.</summary>
public class Checkpoint7ThemeTests
{
    [Fact]
    public void ThemeCatalog_Exposes_Eight_Hokusai_Palettes()
    {
        var ids = ThemeCatalog.All.Select(t => t.Id).ToArray();
        // Stage 43 added two from Hokusai's Large Flowers series. Kirifuri Falls and Thunderstorm Beneath the Summit followed.
        Assert.Equal(new[] { "washi-ink", "great-wave", "red-fuji", "pine-mist", "chrysanthemum", "peony", "kirifuri", "thunderstorm" }, ids);
        Assert.All(ThemeCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
            Assert.Contains("Hokusai", t.InspirationNote, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(t.Light);
            Assert.NotNull(t.Dark);
            Assert.True(ContrastRatio(t.Light.Text, t.Light.Bg) >= 4.5, t.Id + " light text/bg");
            Assert.True(ContrastRatio(t.Dark.Text, t.Dark.Bg) >= 4.5, t.Id + " dark text/bg");
            Assert.True(ContrastRatio(t.Light.Accent, t.Light.OnAccent) >= 3.0, t.Id + " light accent");
            Assert.True(ContrastRatio(t.Dark.Accent, t.Dark.OnAccent) >= 3.0, t.Id + " dark accent");
            Assert.True(ContrastRatio(t.Light.TextMuted, t.Light.Bg) >= 4.5, t.Id + " light muted");
            Assert.True(ContrastRatio(t.Dark.TextMuted, t.Dark.Bg) >= 4.5, t.Id + " dark muted");
        });
    }

    [Fact]
    public void Kirifuri_and_Thunderstorm_read_apart_from_the_earlier_families_and_from_each_other()
    {
        var fresh = new[] { "kirifuri", "thunderstorm" }.Select(ThemeCatalog.Get).ToArray();
        var earlier = new[] { "washi-ink", "great-wave", "red-fuji", "pine-mist", "chrysanthemum", "peony" }.Select(ThemeCatalog.Get);
        foreach (var theme in fresh)
        {
            foreach (var other in earlier.Append(fresh.Single(t => t.Id != theme.Id)))
            {
                // Light paper and accent both have to move, or the swatch is another pale sheet with a familiar pill.
                Assert.True(RgbDistance(theme.Light.Bg, other.Light.Bg) >= 36, theme.Id + " light bg vs " + other.Id);
                Assert.True(RgbDistance(theme.Light.Accent, other.Light.Accent) >= 40, theme.Id + " light accent vs " + other.Id);
                // Dark grounds are all near-black; the accent pill is what separates them in the gallery.
                Assert.True(RgbDistance(theme.Dark.Accent, other.Dark.Accent) >= 55, theme.Id + " dark accent vs " + other.Id);
            }
        }
    }

    [Fact]
    public void AppearanceMode_Resolves_System_Against_OsPreference()
    {
        Assert.True(AppearanceResolver.Resolve(AppearanceMode.Light, osDark: true));
        Assert.False(AppearanceResolver.Resolve(AppearanceMode.Dark, osDark: false));
        Assert.True(AppearanceResolver.Resolve(AppearanceMode.System, osDark: false));
        Assert.False(AppearanceResolver.Resolve(AppearanceMode.System, osDark: true));
    }

    [Fact]
    public void MotionPolicy_ZeroDuration_When_ReducedMotion()
    {
        Assert.Equal(TimeSpan.Zero, MotionPolicy.PanelDuration(reduceMotion: true));
        Assert.True(MotionPolicy.PanelDuration(reduceMotion: false) > TimeSpan.Zero);
        Assert.True(MotionPolicy.PanelDuration(reduceMotion: false) <= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void SidebarLayout_Narrow_Uses_Overlay_Wide_Inline()
    {
        Assert.Equal(SidebarPresentation.Overlay, SidebarLayout.ForWidth(700, collapsed: false));
        Assert.Equal(SidebarPresentation.Hidden, SidebarLayout.ForWidth(700, collapsed: true));
        Assert.Equal(SidebarPresentation.Inline, SidebarLayout.ForWidth(1100, collapsed: false));
        Assert.Equal(SidebarPresentation.CollapsedRail, SidebarLayout.ForWidth(1100, collapsed: true));
    }

    private static double ContrastRatio(string hexA, string hexB)
    {
        static double RelLum(string hex)
        {
            var c = Parse(hex);
            double Chan(double v)
            {
                v /= 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Chan(c.r) + 0.7152 * Chan(c.g) + 0.0722 * Chan(c.b);
        }
        static (byte r, byte g, byte b) Parse(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8) hex = hex[^6..];
            return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        var l1 = RelLum(hexA);
        var l2 = RelLum(hexB);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RgbDistance(string hexA, string hexB)
    {
        static (int r, int g, int b) Parse(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8) hex = hex[^6..];
            return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
        }
        var a = Parse(hexA);
        var b = Parse(hexB);
        var dr = a.r - b.r;
        var dg = a.g - b.g;
        var db = a.b - b.b;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}

public class Checkpoint7ConfigApprovalTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");

    private sealed class ConfigHandler : HttpMessageHandler
    {
        public string SchemaJson { get; set; } = """
            {"fields":{"approvals.mode":{"type":"select","description":"Dangerous command approval mode","category":"security","options":["manual","smart","off"]}},"category_order":["security"]}
            """;
        public string ConfigJson { get; set; } = """{"approvals":{"mode":"manual"}}""";
        public List<string> Puts { get; } = new();
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode GetStatus { get; set; } = HttpStatusCode.OK;
        public bool RequireAuthCookie { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (RequireAuthCookie && req.Headers.GetValues("Cookie").All(c => !c.Contains("hermes_session", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"auth"}""", Encoding.UTF8, "application/json")
                });

            if (path.EndsWith("/api/config/schema", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(GetStatus, SchemaJson));
            if (path.EndsWith("/api/config", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(GetStatus, ConfigJson));
            if (path.EndsWith("/api/config", StringComparison.Ordinal) && req.Method == HttpMethod.Put)
            {
                Puts.Add($"{req.RequestUri.Query} {req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult()}");
                return Task.FromResult(Json(PutStatus, "{}"));
            }
            throw new InvalidOperationException(path);
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public void ConfigPatch_Nests_Changed_Key_Only()
    {
        var patch = ConfigPatch.For("approvals.mode", "smart");
        Assert.Equal("""{"approvals":{"mode":"smart"}}""", patch.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
    }

    [Fact]
    public void ApprovalMode_Descriptions_Are_Honest_About_Dangerous_Commands()
    {
        Assert.Equal("manual", ApprovalMode.Manual.Wire);
        Assert.Equal("smart", ApprovalMode.Smart.Wire);
        Assert.Equal("off", ApprovalMode.Off.Wire);
        Assert.Contains("dangerous", ApprovalMode.Manual.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("every file", ApprovalMode.Manual.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile", ApprovalMode.Smart.ScopeNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other clients", ApprovalMode.Off.ScopeNote, StringComparison.OrdinalIgnoreCase);
        Assert.True(ApprovalMode.Off.RequiresConfirmation);
        Assert.False(ApprovalMode.Manual.RequiresConfirmation);
    }

    [Fact]
    public async Task ConfigApi_Schema_And_Values_And_Nested_Save()
    {
        var handler = new ConfigHandler();
        using var auth = new DashboardAuth(Base, handler);
        // Cookie jar empty is fine for fixture; API uses authenticated SendAsync path.
        var api = new ConfigApi(auth);

        var schema = await api.GetSchemaAsync("rin", default);
        Assert.True(schema.SupportsApprovalsMode);
        Assert.Equal(new[] { "manual", "smart", "off" }, schema.ApprovalsModeOptions);

        var values = await api.GetValuesAsync("rin", default);
        Assert.Equal(ApprovalMode.Manual, values.ApprovalsMode);

        await api.SavePatchAsync("rin", ConfigPatch.For("approvals.mode", "smart"), default);
        Assert.Single(handler.Puts);
        Assert.Contains("profile=rin", handler.Puts[0]);
        Assert.Contains("""{"config":{"approvals":{"mode":"smart"}}}""", handler.Puts[0]);
    }

    [Fact]
    public async Task ConfigApi_Unsupported_Schema_Disables_Approvals()
    {
        var handler = new ConfigHandler
        {
            SchemaJson = """{"fields":{"logging.level":{"type":"string","description":"x","category":"general"}},"category_order":[]}"""
        };
        using var auth = new DashboardAuth(Base, handler);
        var api = new ConfigApi(auth);
        var schema = await api.GetSchemaAsync("rin", default);
        Assert.False(schema.SupportsApprovalsMode);
        Assert.Empty(schema.ApprovalsModeOptions);
    }

    [Fact]
    public async Task ConfigApi_Auth_Errors_Surface_Clearly()
    {
        var handler = new ConfigHandler { GetStatus = HttpStatusCode.Unauthorized };
        using var auth = new DashboardAuth(Base, handler);
        var api = new ConfigApi(auth);
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => api.GetValuesAsync("rin", default));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void ApprovalChange_Off_Requires_Confirm_Gate()
    {
        Assert.True(ApprovalChangeGate.MayApplyWithoutConfirm(ApprovalMode.Manual, ApprovalMode.Smart));
        Assert.True(ApprovalChangeGate.MayApplyWithoutConfirm(ApprovalMode.Smart, ApprovalMode.Manual));
        Assert.False(ApprovalChangeGate.MayApplyWithoutConfirm(ApprovalMode.Manual, ApprovalMode.Off));
        Assert.True(ApprovalChangeGate.ConfirmRequired(ApprovalMode.Manual, ApprovalMode.Off));
    }
}

public class Checkpoint7YoloTests
{
    [Fact]
    public async Task SetYolo_Uses_Session_Scoped_ConfigSet_Wire()
    {
        var sock = new FakeRpcTransport();
        sock.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(sock);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                foreach (var raw in sock.Sent.ToArray())
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (!doc.RootElement.TryGetProperty("method", out var m)) continue;
                    if (m.GetString() != "config.set") continue;
                    if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.ToString();
                    sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"value\":\"1\"}}");
                    return;
                }
                await Task.Delay(5);
            }
        });

        var yolo = new SessionYoloControls(() => client, () => Task.FromResult("rt-9"));
        var on = await yolo.SetAsync(true, default);
        Assert.True(on);
        Assert.True(yolo.Enabled);

        var wire = sock.Sent.Single(s => s.Contains("config.set", StringComparison.Ordinal));
        using var sent = System.Text.Json.JsonDocument.Parse(wire);
        var p = sent.RootElement.GetProperty("params");
        Assert.Equal("yolo", p.GetProperty("key").GetString());
        Assert.Equal("1", p.GetProperty("value").GetString());
        Assert.Equal("rt-9", p.GetProperty("session_id").GetString());
        Assert.False(p.TryGetProperty("mode", out _)); // not three session modes — YOLO on/off only
    }

    [Fact]
    public void Yolo_Requires_Confirmation_When_Enabling()
    {
        Assert.True(SessionYoloControls.RequiresConfirmation(enable: true));
        Assert.False(SessionYoloControls.RequiresConfirmation(enable: false));
        Assert.Contains("this chat", SessionYoloControls.EnableConfirmMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile", SessionYoloControls.ScopeDistinction, StringComparison.OrdinalIgnoreCase);
    }
}

public class Checkpoint7CreditsPrivacyTests
{
    [Fact]
    public void PublicCredit_Identity_Is_Exact()
    {
        Assert.Equal("BookahCoding", PublicCredits.Organization);
        Assert.Equal("bookahgamer" + "@" + "gmail.com", PublicCredits.ContactEmail);
        Assert.Equal(2026, PublicCredits.CopyrightYear);
        var credits = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "CREDITS.md"));
        Assert.Contains(PublicCredits.ContactEmail, credits, StringComparison.Ordinal);
        Assert.Contains(PublicCredits.Organization, File.ReadAllText(Path.Combine(FindRepoRoot(), "LICENSE")), StringComparison.Ordinal);
    }

    [Fact]
    public void EmailAllowlist_Only_Deliberate_Credit_Paths()
    {
        Assert.True(PublicCredits.IsDeliberateCreditPath("LICENSE"));
        Assert.True(PublicCredits.IsDeliberateCreditPath("README.md"));
        Assert.True(PublicCredits.IsDeliberateCreditPath("docs/CREDITS.md"));
        Assert.False(PublicCredits.IsDeliberateCreditPath("src/Hikyaku.App/MainWindow.xaml.cs"));
        Assert.False(PublicCredits.IsDeliberateCreditPath("docs/HANDOFF.md"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hikyaku.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
