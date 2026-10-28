using System.Security.Cryptography;
using System.Text;

namespace Hikyaku.Core;

/// <summary>
/// The project's publisher key: the PUBLIC half (base64 SubjectPublicKeyInfo, ECDSA P-256) of a keypair made with
/// <c>tools/Hikyaku.ReleaseTool keygen</c> on 2026-10-27. The private key is kept offline by the owner and never enters this
/// repository. If it is lost, a new build embedding a new public key must be shipped by hand. If this string were empty, nothing could be installed.
/// </summary>
public static class UpdateTrust
{
    public const string PublisherPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAi+efFYKqEzUFUjwo0YVt6Fmnua6ue/nw/rAEaNjJ4KzwRUAJYO+s96RplfLkjT9mKWysRzF6tHeXezy1JTrbw==";
}

public enum SignatureResult { Ok, NoTrustedKey, Malformed, Mismatch }

/// <summary>
/// Detached ECDSA P-256 / SHA-256 signature over a message that binds the format, platform, version and the zip's
/// SHA-256. Binding the version stops a validly signed older package being presented as a newer release.
/// </summary>
public static class ReleaseSigning
{
    public const string Platform = "win-x64";

    public static byte[] HashFile(string path)
    {
        using var f = File.OpenRead(path);
        return SHA256.HashData(f);
    }

    public static byte[] Message(ReleaseVersion version, string platform, byte[] zipSha256) =>
        Encoding.UTF8.GetBytes($"hikyaku-release-v1\nplatform={platform}\nversion={version}\nsha256={Convert.ToHexString(zipSha256).ToLowerInvariant()}\n");

    /// <summary>Base64 IEEE-P1363 signature (64 bytes). Used by the release tool and tests.</summary>
    public static string Sign(ECDsa key, ReleaseVersion version, string zipPath, string platform = Platform) =>
        Convert.ToBase64String(key.SignData(Message(version, platform, HashFile(zipPath)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

public sealed class ReleaseSignatureVerifier
{
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private readonly ECDsa? _key;

    public ReleaseSignatureVerifier(string? publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64)) return;
        try
        {
            var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (k.ExportParameters(false).Curve.Oid.Value == P256Oid) _key = k; else k.Dispose();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            _key = null; // unusable key = no trust, never a guess
        }
    }

    public bool IsConfigured => _key is not null;

    public SignatureResult Verify(ReleaseVersion version, string zipPath, string signatureBase64, string platform = ReleaseSigning.Platform)
    {
        if (_key is null) return SignatureResult.NoTrustedKey;
        byte[] sig;
        try { sig = Convert.FromBase64String((signatureBase64 ?? "").Trim()); }
        catch (FormatException) { return SignatureResult.Malformed; }
        if (sig.Length != 64) return SignatureResult.Malformed;
        var message = ReleaseSigning.Message(version, platform, ReleaseSigning.HashFile(zipPath));
        return _key.VerifyData(message, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? SignatureResult.Ok : SignatureResult.Mismatch;
    }
}
