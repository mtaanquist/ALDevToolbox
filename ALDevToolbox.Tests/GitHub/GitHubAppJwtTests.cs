using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Services.GitHub;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The App JWT is hand-rolled (see the "no new external dependency" fence in
/// CLAUDE.md), so the shape GitHub checks - base64url segments, an RS256
/// signature over them, and the three claims - is checked here rather than
/// trusted to a library. A wrong token fails on the first install, far from
/// this code.
/// </summary>
public sealed class GitHubAppJwtTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_produces_a_token_the_apps_public_key_verifies()
    {
        using var key = RSA.Create(2048);
        var token = GitHubAppJwt.Create(123456, key.ExportRSAPrivateKeyPem(), Now);

        var parts = token.Split('.');
        parts.Should().HaveCount(3, "a JWS is header.payload.signature");

        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        using var verifier = RSA.Create();
        verifier.ImportRSAPublicKey(key.ExportRSAPublicKey(), out _);
        verifier.VerifyData(signingInput, FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Fact]
    public void Create_signs_the_app_id_as_iss_with_a_backdated_iat_and_a_nine_minute_expiry()
    {
        using var key = RSA.Create(2048);
        var token = GitHubAppJwt.Create(123456, key.ExportRSAPrivateKeyPem(), Now);
        var parts = token.Split('.');

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("123456");
        // Backdated a minute: GitHub rejects an iat even a second in its future.
        payload.RootElement.GetProperty("iat").GetInt64()
            .Should().Be(Now.AddSeconds(-60).ToUnixTimeSeconds());
        payload.RootElement.GetProperty("exp").GetInt64()
            .Should().Be(Now.Add(GitHubAppJwt.Lifetime).ToUnixTimeSeconds());
        GitHubAppJwt.Lifetime.Should().BeLessThan(TimeSpan.FromMinutes(10), "GitHub refuses a longer-lived App JWT");
    }

    [Fact]
    public void Create_accepts_a_pkcs8_key_as_well_as_the_pkcs1_one_github_hands_out()
    {
        using var key = RSA.Create(2048);
        var token = GitHubAppJwt.Create(1, key.ExportPkcs8PrivateKeyPem(), Now);

        token.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void Create_uses_the_url_safe_alphabet_with_no_padding()
    {
        using var key = RSA.Create(2048);
        var token = GitHubAppJwt.Create(987654321, key.ExportRSAPrivateKeyPem(), Now);

        token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
