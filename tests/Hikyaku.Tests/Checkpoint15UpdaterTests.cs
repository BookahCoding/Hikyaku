using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 15c — GitHub Releases updater: check, bounded/allow-listed download, project-key (ECDSA P-256) signature
/// that binds version+platform+zip hash, safe extraction, transactional apply. Synthetic keys, zips and gateways
/// only; no real release, no real publisher key, no network.
/// </summary>
public sealed class Checkpoint15UpdaterTests : IDisposable
{
    private const string Repo = "BookahCoding/Hikyaku";
    private static readonly ReleaseVersion V1 = new(0, 1, 0);
    private static readonly ReleaseVersion V2 = new(0, 2, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hik-upd-" + Guid.NewGuid().ToString("N"));

    public Checkpoint15UpdaterTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Seen) Seen.Add(r);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(impl(r));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string tag = "v0.2.0", string body = "Notes", bool prerelease = false,
        string? zipUrl = null, string? sigUrl = null, bool withSig = true, long size = 1234)
    {
        zipUrl ??= $"https://github.com/{Repo}/releases/download/{tag}/Hikyaku-win-x64.zip";
        sigUrl ??= zipUrl + ".sig";
        var sig = withSig
            ? $$""",{"name":"Hikyaku-win-x64.zip.sig","browser_download_url":"{{sigUrl}}","size":96}"""
            : "";
        return $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"body":{{System.Text.Json.JsonSerializer.Serialize(body)}},
         "html_url":"https://github.com/{{Repo}}/releases/tag/{{tag}}",
         "assets":[{"name":"Hikyaku-win-x64.zip","browser_download_url":"{{zipUrl}}","size":{{size}}}{{sig}}]}
        """;
    }

    private static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> impl, ReleaseVersion? cur = null, Stub? stub = null) =>
        new(stub ?? new Stub(impl), Repo, cur ?? V1);

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, c) in entries)
            {
                var e = z.CreateEntry(n);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        return ms.ToArray();
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static string Spki(ECDsa k) => Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());

    // ---- versions ------------------------------------------------------------------------------

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("v0.0.1", true)]
    [InlineData("1.2.3", false)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3-rc1", false)]
    [InlineData("v1.2.3.4", false)]
    [InlineData("v01.2.3", false)]
    [InlineData("v-1.2.3", false)]
    [InlineData("v99999999999.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_strict_vMAJOR_MINOR_PATCH_tags_parse(string? tag, bool ok) =>
        Assert.Equal(ok, ReleaseVersion.TryParseTag(tag, out _));

    [Fact]
    public void Versions_compare_numerically_not_lexically()
    {
        ReleaseVersion.TryParseTag("v0.10.0", out var a);
        ReleaseVersion.TryParseTag("v0.9.0", out var b);
        Assert.True(a > b);
        Assert.True(V2 > V1);
        Assert.False(V1 > new ReleaseVersion(0, 1, 0));
        Assert.Equal("v0.2.0", V2.Tag);
    }

    [Theory]
    [InlineData("0.1.0", true, 0, 1, 0)]
    [InlineData("0.1.0+abc123", true, 0, 1, 0)]
    [InlineData("1.2.3.0", true, 1, 2, 3)]
    [InlineData("junk", false, 0, 0, 0)]
    public void Current_version_parses_assembly_informational_strings(string s, bool ok, int ma, int mi, int pa)
    {
        Assert.Equal(ok, ReleaseVersion.TryParseCurrent(s, out var v));
        if (ok) Assert.Equal(new ReleaseVersion(ma, mi, pa), v);
    }

    // ---- check ---------------------------------------------------------------------------------

    [Fact]
    public async Task Newer_release_is_available_with_exact_asset_urls_and_unauthenticated_request()
    {
        var stub = new Stub(_ => Json(ReleaseJson()));
        var r = Assert.IsType<UpdateAvailable>(await Checker(null!, stub: stub).CheckAsync(default));
        Assert.Equal(V2, r.Release.Version);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v0.2.0/Hikyaku-win-x64.zip", r.Release.ZipUrl.AbsoluteUri);
        Assert.EndsWith(".zip.sig", r.Release.SigUrl.AbsoluteUri);
        var req = Assert.Single(stub.Seen);
        Assert.Equal($"https://api.github.com/repos/{Repo}/releases/latest", req.RequestUri!.AbsoluteUri);
        Assert.Null(req.Headers.Authorization);
        Assert.NotEmpty(req.Headers.UserAgent);
    }

    [Theory]
    [InlineData("v0.1.0")] // same
    [InlineData("v0.0.9")] // older: never offered as an update
    public async Task Same_or_older_release_is_up_to_date(string tag)
    {
        var r = await Checker(_ => Json(ReleaseJson(tag))).CheckAsync(default);
        Assert.IsType<UpdateUpToDate>(r);
    }

    [Fact]
    public async Task Release_notes_are_plain_bounded_text()
    {
        var notes = "Fixes\u0007\u001b[31m red\r\n" + new string('x', 20_000);
        var r = Assert.IsType<UpdateAvailable>(await Checker(_ => Json(ReleaseJson(body: notes))).CheckAsync(default));
        Assert.DoesNotContain('\u0007', r.Release.Notes);
        Assert.DoesNotContain('\u001b', r.Release.Notes);
        Assert.True(r.Release.Notes.Length <= UpdateChecker.MaxNotesChars + 1);
    }

    [Theory]
    [InlineData("v0.2.0-rc1")]
    [InlineData("latest")]
    public async Task Bad_tag_is_malformed(string tag)
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json(ReleaseJson(tag))).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, r.Kind);
    }

    [Fact]
    public async Task Prerelease_is_not_offered()
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json(ReleaseJson(prerelease: true))).CheckAsync(default));
        Assert.Equal(UpdateFailure.NoRelease, r.Kind);
    }

    [Fact]
    public async Task Release_without_signature_asset_is_not_installable()
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json(ReleaseJson(withSig: false))).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, r.Kind);
        Assert.Contains("signature", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.example/Hikyaku-win-x64.zip")]
    [InlineData("http://github.com/BookahCoding/Hikyaku/releases/download/v0.2.0/Hikyaku-win-x64.zip")]
    [InlineData("https://github.com/Other/Repo/releases/download/v0.2.0/Hikyaku-win-x64.zip")]
    [InlineData("https://github.com/BookahCoding/Hikyaku/releases/download/v9.9.9/Hikyaku-win-x64.zip")] // tag mismatch
    [InlineData("https://github.com.evil.example/BookahCoding/Hikyaku/releases/download/v0.2.0/Hikyaku-win-x64.zip")]
    public async Task Asset_urls_outside_this_repo_and_tag_are_rejected(string zipUrl)
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json(ReleaseJson(zipUrl: zipUrl, sigUrl: zipUrl + ".sig"))).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, r.Kind);
    }

    [Fact]
    public async Task Oversized_zip_is_refused_at_check_time()
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json(ReleaseJson(size: UpdateInstaller.MaxZipBytes + 1))).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, r.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.NoRelease)]
    [InlineData(HttpStatusCode.Unauthorized, UpdateFailure.AuthRequired)]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.AuthRequired)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.Failed)]
    public async Task Http_failures_map_to_honest_kinds(HttpStatusCode code, UpdateFailure expected)
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json("{}", code)).CheckAsync(default));
        Assert.Equal(expected, r.Kind);
    }

    [Fact]
    public async Task Exhausted_rate_limit_is_reported_as_rate_limited()
    {
        var r = Assert.IsType<UpdateCheckFailed>(await Checker(_ =>
        {
            var resp = Json("{}", HttpStatusCode.Forbidden);
            resp.Headers.Add("x-ratelimit-remaining", "0");
            return resp;
        }).CheckAsync(default));
        Assert.Equal(UpdateFailure.RateLimited, r.Kind);
    }

    [Fact]
    public async Task Network_failure_is_offline_and_garbage_is_malformed()
    {
        var off = Assert.IsType<UpdateCheckFailed>(await Checker(_ => throw new HttpRequestException("down")).CheckAsync(default));
        Assert.Equal(UpdateFailure.Offline, off.Kind);
        var bad = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json("not json")).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, bad.Kind);
        var huge = Assert.IsType<UpdateCheckFailed>(await Checker(_ => Json("{\"body\":\"" + new string('a', 600_000) + "\"}")).CheckAsync(default));
        Assert.Equal(UpdateFailure.Malformed, huge.Kind);
    }

    [Fact]
    public async Task Cancelled_check_throws_instead_of_reporting_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(_ => Json(ReleaseJson())).CheckAsync(cts.Token));
    }

    // ---- download ------------------------------------------------------------------------------

    private static Uri Zip => new($"https://github.com/{Repo}/releases/download/v0.2.0/Hikyaku-win-x64.zip");

    private static HttpResponseMessage Bytes(byte[] b, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new ByteArrayContent(b) };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    [Fact]
    public async Task Download_follows_allow_listed_redirects_and_never_sends_credentials()
    {
        var payload = Encoding.ASCII.GetBytes("zip-bytes");
        var stub = new Stub(r => r.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/x/y")
            : Bytes(payload));
        var dest = Path.Combine(_root, "d.zip");
        var progress = 0L;
        await new UpdateDownloader(stub).DownloadAsync(Zip, dest, 1024, new Progress<long>(n => progress = n), default);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.All(stub.Seen, r => Assert.Null(r.Headers.Authorization));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Theory]
    [InlineData("https://evil.example/payload")]
    [InlineData("http://objects.githubusercontent.com/payload")]
    [InlineData("https://objects.githubusercontent.com.evil.example/payload")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    public async Task Download_rejects_redirects_off_the_allow_list_or_off_https(string to)
    {
        var stub = new Stub(_ => Redirect(to));
        var dest = Path.Combine(_root, "d.zip");
        await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(stub).DownloadAsync(Zip, dest, 1024, null, default));
        Assert.False(File.Exists(dest));
        Assert.Single(stub.Seen); // the disallowed hop was never requested
    }

    [Fact]
    public async Task Download_rejects_a_non_allow_listed_start_url_and_redirect_loops()
    {
        var never = new Stub(_ => Bytes(new byte[1]));
        await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(never).DownloadAsync(new Uri("https://evil.example/a.zip"), Path.Combine(_root, "a"), 1024, null, default));
        Assert.Empty(never.Seen);

        var loop = new Stub(_ => Redirect("https://github.com/loop"));
        await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(loop).DownloadAsync(Zip, Path.Combine(_root, "b"), 1024, null, default));
        Assert.True(loop.Seen.Count <= UpdateDownloader.MaxRedirects + 1);
    }

    [Fact]
    public async Task Download_enforces_declared_and_streamed_size_bounds_and_removes_partials()
    {
        var dest = Path.Combine(_root, "big.zip");
        // Declared length too large.
        var declared = new Stub(_ => Bytes(new byte[100]));
        await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(declared).DownloadAsync(Zip, dest, 10, null, default));
        // Undeclared (chunked) stream that runs past the bound.
        var chunked = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NoLengthStream(100)) });
        await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(chunked).DownloadAsync(Zip, dest, 10, null, default));
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    private sealed class NoLengthStream(int length) : Stream
    {
        private int _left = length;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _left); _left -= n; return n;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Download_http_error_and_cancellation_leave_nothing_behind()
    {
        var dest = Path.Combine(_root, "e.zip");
        await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new Stub(_ => Bytes(new byte[1], HttpStatusCode.NotFound))).DownloadAsync(Zip, dest, 10, null, default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(new Stub(_ => Bytes(new byte[1]))).DownloadAsync(Zip, dest, 10, null, cts.Token));
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    // ---- signature -----------------------------------------------------------------------------

    [Fact]
    public void Signature_round_trips_and_binds_version_platform_and_content()
    {
        using var key = NewKey();
        var zip = WriteFile("s.zip", MakeZip(("Hikyaku.exe", "v2")));
        var sig = ReleaseSigning.Sign(key, V2, zip);
        var verifier = new ReleaseSignatureVerifier(Spki(key));

        Assert.True(verifier.IsConfigured);
        Assert.Equal(SignatureResult.Ok, verifier.Verify(V2, zip, sig));
        // Rollback/mix-up: a validly signed older release cannot pose as a newer one.
        Assert.Equal(SignatureResult.Mismatch, verifier.Verify(new ReleaseVersion(9, 9, 9), zip, sig));
        Assert.Equal(SignatureResult.Mismatch, verifier.Verify(V2, zip, sig, platform: "win-arm64"));
        // Content tamper.
        var tampered = WriteFile("t.zip", MakeZip(("Hikyaku.exe", "evil")));
        Assert.Equal(SignatureResult.Mismatch, verifier.Verify(V2, tampered, sig));
        // Another key.
        using var other = NewKey();
        Assert.Equal(SignatureResult.Mismatch, new ReleaseSignatureVerifier(Spki(other)).Verify(V2, zip, sig));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")] // wrong length
    public void Malformed_signatures_are_rejected_not_thrown(string sig)
    {
        using var key = NewKey();
        var zip = WriteFile("m.zip", MakeZip(("Hikyaku.exe", "x")));
        Assert.Equal(SignatureResult.Malformed, new ReleaseSignatureVerifier(Spki(key)).Verify(V2, zip, sig));
    }

    [Fact]
    public void No_embedded_key_or_wrong_curve_means_not_configured_and_nothing_verifies()
    {
        Assert.False(new ReleaseSignatureVerifier(null).IsConfigured);
        Assert.False(new ReleaseSignatureVerifier("").IsConfigured);
        Assert.False(new ReleaseSignatureVerifier("garbage").IsConfigured);
        Assert.True(new ReleaseSignatureVerifier(UpdateTrust.PublisherPublicKeySpkiBase64).IsConfigured);   // the shipped key is a usable P-256 public key
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(new ReleaseSignatureVerifier(Spki(p384)).IsConfigured);
        var zip = WriteFile("n.zip", MakeZip(("Hikyaku.exe", "x")));
        Assert.Equal(SignatureResult.NoTrustedKey, new ReleaseSignatureVerifier(null).Verify(V2, zip, "AAAA"));
    }

    // ---- safe extraction -----------------------------------------------------------------------

    private string Stage(string name = "stage") => Path.Combine(_root, name);

    [Fact]
    public void Extract_writes_files_under_staging_only()
    {
        var zip = WriteFile("ok.zip", MakeZip(("Hikyaku.exe", "exe"), ("sub/Hikyaku.dll", "dll"), ("sub/", "")));
        UpdateStaging.Extract(zip, Stage());
        Assert.Equal("exe", File.ReadAllText(Path.Combine(Stage(), "Hikyaku.exe")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(Stage(), "sub", "Hikyaku.dll")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("Hikyaku.exe:stream")]
    [InlineData("CON.txt")]
    [InlineData("sub/NUL")]
    [InlineData("trailing.")]
    [InlineData("space ")]
    public void Extract_rejects_traversal_absolute_ads_reserved_and_odd_names(string name)
    {
        var zip = WriteFile("bad.zip", MakeZip(("Hikyaku.exe", "exe"), (name, "x")));
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(zip, Stage()));
        Assert.False(Directory.Exists(Stage()));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Extract_rejects_duplicates_missing_exe_and_too_many_entries()
    {
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(
            WriteFile("d.zip", MakeZip(("Hikyaku.exe", "a"), ("hikyaku.EXE", "b"))), Stage("d")));
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(
            WriteFile("n.zip", MakeZip(("readme.txt", "a"))), Stage("n")));
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(
            WriteFile("m.zip", MakeZip(("Hikyaku.exe", "a"), ("b", "b"), ("c", "c"))), Stage("m"),
            new ExtractLimits(MaxEntries: 2)));
    }

    [Fact]
    public void Extract_bounds_actual_bytes_written_not_declared_sizes()
    {
        var zip = WriteFile("z.zip", MakeZip(("Hikyaku.exe", new string('a', 5000))));
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(zip, Stage("z1"), new ExtractLimits(MaxEntryBytes: 1000)));
        Assert.Throws<UpdateException>(() => UpdateStaging.Extract(zip, Stage("z2"), new ExtractLimits(MaxTotalBytes: 1000)));
        Assert.False(Directory.Exists(Stage("z1")));
        Assert.False(Directory.Exists(Stage("z2")));
    }

    // ---- installer (download + verify + stage) -------------------------------------------------

    private (UpdateInstaller Installer, Stub Http, UpdateRelease Release, ECDsa Key) Install(
        bool signCorrectly = true, bool keyConfigured = true, byte[]? zipOverride = null)
    {
        var key = NewKey();
        var zipBytes = zipOverride ?? MakeZip(("Hikyaku.exe", "new-exe"), ("Hikyaku.dll", "new-dll"));
        var zipPath = WriteFile("served.zip", zipBytes);
        var sig = ReleaseSigning.Sign(key, signCorrectly ? V2 : new ReleaseVersion(0, 0, 5), zipPath);
        var stub = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith(".sig") ? Bytes(Encoding.ASCII.GetBytes(sig)) : Bytes(zipBytes));
        var release = new UpdateRelease(V2, "v0.2.0", "notes", Zip, new Uri(Zip + ".sig"),
            new Uri($"https://github.com/{Repo}/releases/tag/v0.2.0"), zipBytes.Length);
        var verifier = new ReleaseSignatureVerifier(keyConfigured ? Spki(key) : null);
        return (new UpdateInstaller(stub, verifier, Path.Combine(_root, "work")), stub, release, key);
    }

    [Fact]
    public async Task Prepare_with_no_trusted_key_fails_closed_before_any_network_call()
    {
        var (inst, http, rel, key) = Install(keyConfigured: false);
        using var _ = key;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => inst.PrepareAsync(rel, null, default));
        Assert.Equal(UpdateFailure.NotTrusted, ex.Kind);
        Assert.Empty(http.Seen);
        Assert.False(Directory.Exists(Path.Combine(_root, "work")));
    }

    [Fact]
    public async Task Prepare_with_valid_signature_stages_files_and_a_marker_then_deletes_the_download()
    {
        var (inst, _, rel, key) = Install();
        using var _ = key;
        var staged = await inst.PrepareAsync(rel, null, default);
        Assert.Equal(V2, staged.Version);
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(staged.StagingDir, "Hikyaku.exe")));
        Assert.Equal(V2, StagedUpdate.ReadMarker(staged.StagingDir)!.Version);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "work"), "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Prepare_with_signature_for_another_version_is_refused_and_leaves_no_staging()
    {
        var (inst, _, rel, key) = Install(signCorrectly: false);
        using var _ = key;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => inst.PrepareAsync(rel, null, default));
        Assert.Equal(UpdateFailure.NotTrusted, ex.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")));
    }

    [Fact]
    public async Task Prepare_with_a_signed_but_unsafe_zip_is_refused()
    {
        var (inst, _, rel, key) = Install(zipOverride: MakeZip(("Hikyaku.exe", "x"), ("../evil.txt", "y")));
        using var _ = key;
        await Assert.ThrowsAsync<UpdateException>(() => inst.PrepareAsync(rel, null, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")));
    }

    // ---- apply ---------------------------------------------------------------------------------

    private (string Install, string Staging) Layout(bool marker = true, bool exe = true)
    {
        var install = Path.Combine(_root, "install");
        var staging = Path.Combine(_root, "staging-apply");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(staging, "sub"));
        if (exe) File.WriteAllText(Path.Combine(install, "Hikyaku.exe"), "old-exe");
        File.WriteAllText(Path.Combine(install, "Hikyaku.dll"), "old-dll");
        File.WriteAllText(Path.Combine(install, "user-note.txt"), "keep me");
        File.WriteAllText(Path.Combine(staging, "Hikyaku.exe"), "new-exe");
        File.WriteAllText(Path.Combine(staging, "Hikyaku.dll"), "new-dll");
        File.WriteAllText(Path.Combine(staging, "sub", "extra.dll"), "extra");
        if (marker) StagedUpdate.WriteMarker(staging, V2);
        return (install, staging);
    }

    [Fact]
    public async Task Apply_replaces_files_keeps_unrelated_files_and_leaves_no_backup()
    {
        var (install, staging) = Layout();
        await UpdateApplier.ApplyAsync(staging, install, parentPid: 0, ct: default);
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(install, "Hikyaku.exe")));
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(install, "Hikyaku.dll")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(install, "sub", "extra.dll")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "user-note.txt")));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateApplier.RollbackDir)));
    }

    [Fact]
    public async Task Apply_rolls_back_everything_when_a_file_cannot_be_replaced()
    {
        var (install, staging) = Layout();
        // Locked target (like a running DLL) makes the swap fail midway.
        using var locked = new FileStream(Path.Combine(install, "Hikyaku.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<UpdateException>(() => UpdateApplier.ApplyAsync(staging, install, 0, ct: default));
        locked.Dispose();
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(install, "Hikyaku.exe")));
        Assert.Equal("old-dll", File.ReadAllText(Path.Combine(install, "Hikyaku.dll")));
        Assert.False(File.Exists(Path.Combine(install, "sub", "extra.dll")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "user-note.txt")));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateApplier.RollbackDir)));
    }

    [Fact]
    public async Task Apply_refuses_unverified_staging_or_a_non_install_directory()
    {
        var (install, staging) = Layout(marker: false);
        await Assert.ThrowsAsync<UpdateException>(() => UpdateApplier.ApplyAsync(staging, install, 0, ct: default));
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(install, "Hikyaku.exe")));

        var (install2, staging2) = Layout(exe: false);
        File.Delete(Path.Combine(install2, "Hikyaku.exe"));
        await Assert.ThrowsAsync<UpdateException>(() => UpdateApplier.ApplyAsync(staging2, install2, 0, ct: default));
    }

    [Fact]
    public async Task Apply_times_out_instead_of_replacing_files_of_a_still_running_app()
    {
        var (install, staging) = Layout();
        var self = Environment.ProcessId; // this test process stays alive
        await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateApplier.ApplyAsync(staging, install, self, TimeSpan.FromMilliseconds(100), default));
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(install, "Hikyaku.exe")));
    }
}
