using System.Security.Cryptography;

namespace Quicklight.Core.Update;

/// <summary>
/// Release signatures: ECDSA P-256 over SHA-256 of Quicklight.exe, stored as base64 text in Quicklight.exe.sig.
/// Only the public key is here; the private key stays with the publisher (tools\new-signing-key.ps1,
/// %USERPROFILE%\.quicklight\update-signing-key.pem) and signs releases in tools\sign-release.ps1.
/// Even someone who can publish GitHub releases cannot ship an update without that key.
/// </summary>
public static class UpdateSignature
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzO8BoZFzfKl7w3+9JSSuHVzQvrhp
        Pz6EMykbVtJF2txcCgpNpIiBhdqli6M5e9LvE/9OefboxnZxeBLIMv+3Ng==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(byte[] data, string signatureBase64, string publicKeyPem = PublicKeyPem)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return false; } // malformed key or signature
    }
}
