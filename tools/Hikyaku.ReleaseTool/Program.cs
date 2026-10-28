using System.Security.Cryptography;
using Hikyaku.Core;

namespace Hikyaku.ReleaseTool;

public static class Program
{
    public static int Main(string[] args) => ReleaseToolCli.Run(args, Console.Out, Console.Error);
}

/// <summary>
/// Owner-side release signing. <c>keygen</c> makes the publisher keypair (keep the private key OFFLINE and OUT of
/// this repository; only the printed public key is pasted into <c>UpdateTrust</c>). <c>sign</c> writes the detached
/// signature that is uploaded next to the zip. <c>verify</c> uses the same verifier the app does.
/// </summary>
public static class ReleaseToolCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0) return Usage(error);
        var o = ParseOptions(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "keygen" => KeyGen(o, output, error),
                "sign" => Sign(o, output, error),
                "verify" => Verify(o, output, error),
                _ => Usage(error)
            };
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("usage:\n  keygen --out <private-key.pem>\n  sign --key <private-key.pem> --version vX.Y.Z --zip <file> [--out <sig>]\n  verify --pub <spki-base64> --version vX.Y.Z --zip <file> --sig <file>");
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < a.Length; i += 2) d[a[i]] = a[i + 1];
        return d;
    }

    private static int KeyGen(Dictionary<string, string> o, TextWriter output, TextWriter error)
    {
        if (!o.TryGetValue("--out", out var outPath)) return Usage(error);
        var full = Path.GetFullPath(outPath);
        if (File.Exists(full)) { error.WriteLine("error: refusing to overwrite an existing file."); return 1; }
        if (InsideRepo(full)) { error.WriteLine("error: the private key must be stored outside the Hikyaku repository."); return 1; }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, key.ExportPkcs8PrivateKeyPem());
        output.WriteLine("Private key written to: " + full);
        output.WriteLine("Keep it offline and backed up by you. It is never needed by the app and must never be committed.");
        output.WriteLine("Public key (paste into UpdateTrust.PublisherPublicKeySpkiBase64):");
        output.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Sign(Dictionary<string, string> o, TextWriter output, TextWriter error)
    {
        if (!o.TryGetValue("--key", out var keyPath) || !o.TryGetValue("--version", out var tag) || !o.TryGetValue("--zip", out var zip))
            return Usage(error);
        if (!ReleaseVersion.TryParseTag(tag, out var version)) { error.WriteLine("error: --version must look like v1.2.3."); return 1; }
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        if (key.KeySize != 256) { error.WriteLine("error: the key must be ECDSA P-256."); return 1; }
        var sig = ReleaseSigning.Sign(key, version, zip);
        var outPath = o.TryGetValue("--out", out var p) ? p : zip + ".sig";
        File.WriteAllText(outPath, sig);
        output.WriteLine($"Signed {Path.GetFileName(zip)} as {version.Tag} ({ReleaseSigning.Platform}) -> {outPath}");
        return 0;
    }

    private static int Verify(Dictionary<string, string> o, TextWriter output, TextWriter error)
    {
        if (!o.TryGetValue("--pub", out var pub) || !o.TryGetValue("--version", out var tag)
            || !o.TryGetValue("--zip", out var zip) || !o.TryGetValue("--sig", out var sigPath))
            return Usage(error);
        if (!ReleaseVersion.TryParseTag(tag, out var version)) { error.WriteLine("error: --version must look like v1.2.3."); return 1; }
        var result = new ReleaseSignatureVerifier(pub).Verify(version, zip, File.ReadAllText(sigPath));
        output.WriteLine(result.ToString());
        return result == SignatureResult.Ok ? 0 : 1;
    }

    private static bool InsideRepo(string fullPath)
    {
        for (var dir = Path.GetDirectoryName(fullPath); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Hikyaku.sln"))) return true;
        return false;
    }
}
