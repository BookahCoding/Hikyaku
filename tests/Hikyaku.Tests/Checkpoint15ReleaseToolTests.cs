using System.IO.Compression;
using Hikyaku.Core;
using Hikyaku.ReleaseTool;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 15c — owner-side release tool, run against temp files only (never a real key).</summary>
public sealed class Checkpoint15ReleaseToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hik-tool-" + Guid.NewGuid().ToString("N"));

    public Checkpoint15ReleaseToolTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (ReleaseToolCli.Run(args, o, e), o.ToString(), e.ToString());
    }

    private string Zip()
    {
        var p = Path.Combine(_dir, "Hikyaku-win-x64.zip");
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create)) z.CreateEntry("Hikyaku.exe");
        return p;
    }

    [Fact]
    public void Keygen_sign_verify_round_trip_matches_the_apps_verifier()
    {
        var key = Path.Combine(_dir, "keys", "private.pem");
        var gen = Run("keygen", "--out", key);
        Assert.Equal(0, gen.Code);
        var pub = gen.Out.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Last();
        Assert.DoesNotContain("PRIVATE", gen.Out);   // the private key is never printed
        Assert.Contains("PRIVATE KEY", File.ReadAllText(key));

        var zip = Zip();
        Assert.Equal(0, Run("sign", "--key", key, "--version", "v0.2.0", "--zip", zip).Code);
        var sig = zip + ".sig";
        Assert.True(File.Exists(sig));

        Assert.Equal(0, Run("verify", "--pub", pub, "--version", "v0.2.0", "--zip", zip, "--sig", sig).Code);
        Assert.Equal(1, Run("verify", "--pub", pub, "--version", "v0.3.0", "--zip", zip, "--sig", sig).Code);
        Assert.Equal(SignatureResult.Ok, new ReleaseSignatureVerifier(pub).Verify(new ReleaseVersion(0, 2, 0), zip, File.ReadAllText(sig)));
    }

    [Fact]
    public void Keygen_refuses_to_overwrite_or_to_write_inside_the_repository()
    {
        var key = Path.Combine(_dir, "k.pem");
        Assert.Equal(0, Run("keygen", "--out", key).Code);
        var before = File.ReadAllText(key);
        var again = Run("keygen", "--out", key);
        Assert.Equal(1, again.Code);
        Assert.Equal(before, File.ReadAllText(key));

        // A folder that contains Hikyaku.sln is a repo checkout.
        var fakeRepo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(fakeRepo);
        File.WriteAllText(Path.Combine(fakeRepo, "Hikyaku.sln"), "");
        var inRepo = Run("keygen", "--out", Path.Combine(fakeRepo, "secrets", "k.pem"));
        Assert.Equal(1, inRepo.Code);
        Assert.Contains("outside", inRepo.Err);
        Assert.False(File.Exists(Path.Combine(fakeRepo, "secrets", "k.pem")));
    }

    [Fact]
    public void Sign_rejects_bad_versions_and_non_p256_keys()
    {
        var key = Path.Combine(_dir, "k.pem");
        Run("keygen", "--out", key);
        var zip = Zip();
        Assert.Equal(1, Run("sign", "--key", key, "--version", "1.2.3", "--zip", zip).Code);
        Assert.Equal(1, Run("sign", "--key", key, "--version", "v1.2.3-rc1", "--zip", zip).Code);
        Assert.False(File.Exists(zip + ".sig"));

        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var weak = Path.Combine(_dir, "p384.pem");
        File.WriteAllText(weak, p384.ExportPkcs8PrivateKeyPem());
        Assert.Equal(1, Run("sign", "--key", weak, "--version", "v1.2.3", "--zip", zip).Code);
        Assert.False(File.Exists(zip + ".sig"));
    }

    [Fact]
    public void Unknown_commands_and_missing_arguments_print_usage()
    {
        Assert.Equal(2, Run().Code);
        Assert.Equal(2, Run("nope").Code);
        Assert.Equal(2, Run("sign", "--zip", "x").Code);
    }
}
