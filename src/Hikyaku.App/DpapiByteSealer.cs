using System.Security.Cryptography;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>DPAPI-backed byte sealer for offline chat transcripts (current-user scope).</summary>
public sealed class DpapiByteSealer : IByteSealer
{
    public byte[] Seal(byte[] plain) =>
        ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    public byte[]? Unseal(byte[] sealedBytes)
    {
        try { return ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }
}
