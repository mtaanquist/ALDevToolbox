using System.Security.Cryptography;
using System.Text;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Signs the short-lived JSON Web Token GitHub requires when the toolbox acts
/// as the App itself (rather than as one of its installations).
///
/// <para>Hand-rolled on <see cref="RSA"/> rather than pulled from a JWT
/// package: the token is three base64url segments over a fixed three-claim
/// payload, and the "no new external dependency" fence in CLAUDE.md is not
/// worth spending on twenty lines. See <c>.design/github-integration.md</c>.</para>
/// </summary>
public static class GitHubAppJwt
{
    /// <summary>
    /// GitHub rejects an App JWT whose lifetime exceeds ten minutes. Nine
    /// leaves room for the backdated <c>iat</c> below without tripping it.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    /// <summary>
    /// Builds and signs the App JWT. <paramref name="privateKeyPem"/> is the
    /// PEM file GitHub issues for the App (PKCS#1 or PKCS#8 — both import).
    /// </summary>
    /// <param name="appId">The App's numeric id, signed as the <c>iss</c> claim.</param>
    /// <param name="privateKeyPem">The App's private key, in PEM form.</param>
    /// <param name="now">Current time; the token is backdated 60s for clock drift.</param>
    public static string Create(long appId, string privateKeyPem, DateTimeOffset now)
    {
        // GitHub rejects a token whose iat is in the future by even a second,
        // and our clock is not guaranteed to agree with theirs.
        var issuedAt = now.AddSeconds(-60).ToUnixTimeSeconds();
        var expiresAt = now.Add(Lifetime).ToUnixTimeSeconds();

        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"iat":{{issuedAt}},"exp":{{expiresAt}},"iss":"{{appId}}"}"""));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Base64 with the URL-safe alphabet and no padding, as JWS requires.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
