using System.Security.Cryptography;

namespace ALDevToolbox.Services;

/// <summary>
/// One copy of the random-token + SHA-256 plumbing used by the password
/// reset, magic-link, and invite flows (#79). The plaintext token is
/// returned for emailing; the hash is what lands in the database, so the
/// raw value disappears the moment the email is sent.
/// </summary>
internal static class TokenIssuer
{
    /// <summary>
    /// Generates 32 random bytes, returns them as a lowercase hex string
    /// plus the SHA-256 hash of that string. The caller stores
    /// <paramref name="Sha256Hash"/>; the plaintext goes into the email.
    /// </summary>
    public static (string PlainText, string Sha256Hash) Issue()
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToHexString(rawBytes).ToLowerInvariant();
        return (raw, Sha256Hex(raw));
    }

    /// <summary>Hashes <paramref name="value"/> with SHA-256, lowercase hex.</summary>
    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
